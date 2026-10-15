using System.Security.Cryptography;
using Aide.Api.Auth;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Release;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Seed;

/// <summary>DEP-2 dev seed settings (section <c>Seed</c>). <c>Seed:Enabled</c> is true in appsettings.Development.json
/// and set by the Kubernetes overlay; it is off everywhere else.</summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public bool Enabled { get; set; }
}

/// <summary>
/// DEP-2, DEP-V2 (§9): when <c>Seed:Enabled</c>, after the migrations have been applied (skipped while any are pending),
/// idempotently creates the Admin <c>admin@aide.local</c> with a generated password, logged once at Warning (the
/// only secret ever logged, so the operator can sign in), and a demo project using the Fake LLM provider and Local Git,
/// owned by that Admin and <b>released at <see cref="DemoReleasedVersion"/></b> (its Local repository tagged
/// <c>v0.1.0</c>), so "Start new version" (FR-60) can be tried at once. An existing account with the seed email is left untouched (its
/// password is never reset); other Admins don't matter. The demo project is created once per database: a
/// <see cref="EventTypes.DemoProjectSeeded"/> marker event records it, so deleting the demo doesn't bring it back.
/// </summary>
public sealed partial class DevSeeder(IServiceScopeFactory scopes, ILogger<DevSeeder> logger) : IHostedService
{
    public const string AdminEmail = "admin@aide.local";
    public const string AdminName = "AIDE Admin";
    public const string DemoProjectName = "Demo: Todo API";
    public const int PasswordLength = 20;

    /// <summary>DEP-V2: the version the demo project is seeded as released at.</summary>
    public const string DemoReleasedVersion = "0.1.0";

    /// <summary>The reason recorded on the demo's seeded transition into release.</summary>
    public const string DemoReleasedReason = "Demo project seeded as released 0.1.0";

    public const string DemoIdea =
        "A small REST API for a personal todo list. Users can create, list, update, complete and delete todos; each todo has a title, optional notes, an optional due date and a done flag. Lists can be filtered by done/not done and sorted by due date. Data is stored in a relational database, and the API is documented with OpenAPI.";

    // No look-alike characters (0/O, 1/l/I).
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (!scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SeedOptions>>().Value.Enabled)
        {
            return;
        }

        try
        {
            await SeedAsync(scope.ServiceProvider, cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.NpgsqlException or InvalidOperationException)
        {
            // A seed failure must not stop the API (the readiness check reports database problems).
            LogSeedFailed(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs the seed once against the scope's database, in one transaction holding the account advisory lock (so
    /// concurrent replicas and registrations can't race it). Public for tests.
    /// </summary>
    public async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var db = services.GetRequiredService<AideDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        if (pending > 0)
        {
            LogPendingMigrations(logger, pending);
            return;
        }

        var accounts = services.GetRequiredService<AccountService>();
        string? newPassword = null;
        Guid? demoProjectId = null;

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await accounts.LockAccountsAsync(cancellationToken);

            var admin = await services.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(AdminEmail);
            if (admin is null)
            {
                (admin, newPassword) = await CreateAdminAsync(services, cancellationToken);
                if (admin is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return;
                }
            }

            if (!await DemoAlreadySeededAsync(services, db, admin, cancellationToken))
            {
                demoProjectId = await CreateDemoProjectAsync(services, db, admin, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        if (newPassword is not null)
        {
            LogAdminSeeded(logger, AdminEmail, newPassword);
        }

        if (demoProjectId is { } projectId)
        {
            LogDemoProject(logger, projectId);
        }
    }

    /// <summary>
    /// The demo project is seeded once per database: the <see cref="EventTypes.DemoProjectSeeded"/> marker (kept when
    /// the project is deleted) says so. A demo project from before the marker existed (named
    /// <see cref="DemoProjectName"/>, with the seed Admin as a member) counts too, and gets the marker.
    /// </summary>
    private static async Task<bool> DemoAlreadySeededAsync(IServiceProvider services, AideDbContext db, AppUser admin, CancellationToken cancellationToken)
    {
        if (await db.Events.AnyAsync(e => e.Type == EventTypes.DemoProjectSeeded, cancellationToken))
        {
            return true;
        }

        var legacy = await db.ProjectMembers
            .Where(m => m.UserId == admin.Id)
            .Join(db.Projects.Where(p => p.Name == DemoProjectName), m => m.ProjectId, p => p.Id, (m, p) => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (legacy is null)
        {
            return false;
        }

        AddMarker(services, db, legacy.Value);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static async Task<Guid> CreateDemoProjectAsync(IServiceProvider services, AideDbContext db, AppUser admin, CancellationToken cancellationToken)
    {
        var creator = services.GetRequiredService<ProjectCreator>();
        var created = await creator.CreateAsync(
            new CreateProjectRequest
            {
                Name = DemoProjectName,
                Idea = DemoIdea,
                ApprovalMode = ApprovalMode.Advisory,
                Git = new CreateProjectGitRequest { Provider = GitProvider.Local },
            },
            admin.Id,
            SystemActor.Name,
            cancellationToken);
        if (created.IsFailure)
        {
            throw new InvalidOperationException($"The demo project is invalid: {created.Error!.Message}");
        }

        // The demo always runs offline on the Fake provider, whatever the system default is.
        var llm = await db.ProjectLlmSettings.FirstAsync(s => s.ProjectId == created.Value.Id, cancellationToken);
        if (llm.Provider != LlmProvider.Fake)
        {
            var catalog = services.GetRequiredService<ILlmProviderCatalog>();
            llm.Provider = LlmProvider.Fake;
            llm.DefaultModel = catalog.DefaultModel(LlmProvider.Fake);
            llm.ModelsByAgent = new Dictionary<string, string>(catalog.DefaultAgentModels(LlmProvider.Fake));
        }

        AddMarker(services, db, created.Value.Id);
        await db.SaveChangesAsync(cancellationToken);
        await MarkDemoReleasedAsync(services, db, created.Value, cancellationToken);
        return created.Value.Id;
    }

    /// <summary>
    /// DEP-V2: the demo starts released at 0.1.0. Its Local repository is created (initial commit on the default branch)
    /// and tagged <c>v0.1.0</c>; the first release version becomes <c>released</c> with that tag and commit, a succeeded
    /// release attempt is recorded, and the project is put in release / released with one seeded state transition (a seed
    /// shortcut: no agent work is faked). If the repository can't be created (no git CLI), the version is still released
    /// without a commit, so the database stays consistent.
    /// </summary>
    private static async Task MarkDemoReleasedAsync(IServiceProvider services, AideDbContext db, Project project, CancellationToken cancellationToken)
    {
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var version = await db.ReleaseVersions.FirstAsync(v => v.Id == project.CurrentReleaseVersionId, cancellationToken);
        var tag = ReleaseRecords.TagFor(version.TagPrefix, DemoReleasedVersion);
        string? commit = null;
        try
        {
            var prepared = await services.GetRequiredService<WorkingCopyPreparer>().PrepareAsync(project.Id, cancellationToken);
            if (prepared.IsSuccess)
            {
                var workingCopies = services.GetRequiredService<IWorkingCopies>();
                var settings = await db.ProjectGitSettings.AsNoTracking().FirstAsync(s => s.ProjectId == project.Id, cancellationToken);
                commit = await workingCopies.ResolveCommitAsync(project.Id, $"refs/heads/{settings.DefaultBranch}", cancellationToken);
                if (commit is not null)
                {
                    await workingCopies.CreateTagAsync(project.Id, tag, commit, ReleaseRules.TagMessage(project.Name, tag, DemoReleasedVersion), cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException or UnauthorizedAccessException)
        {
            LogDemoRepositoryFailed(services.GetRequiredService<ILogger<DevSeeder>>(), ex, project.Id);
            commit = null;
        }

        await db.Entry(version).ReloadAsync(cancellationToken);
        version.Version = DemoReleasedVersion;
        version.Status = ReleaseVersionStatus.Released;
        version.Tag = tag;
        version.MergeCommitSha = commit;
        version.ReleasedAt = now;

        var tracked = await db.Projects.FirstAsync(p => p.Id == project.Id, cancellationToken);
        tracked.CurrentPhase = Phase.Release;
        tracked.Status = ProjectStatus.Released;
        tracked.ReleasedVersion = DemoReleasedVersion;
        tracked.DevelopmentVersion = null;
        tracked.UpdatedAt = now;
        db.StateTransitions.Add(new StateTransition
        {
            ProjectId = project.Id,
            ReleaseVersionId = version.Id,
            FromPhase = Phase.Refinement,
            ToPhase = Phase.Release,
            Actor = SystemActor.Name,
            Reason = DemoReleasedReason,
            Checkpoint = tracked.Checkpoint,
            CreatedAt = now,
        });
        db.Releases.Add(new Release
        {
            ProjectId = project.Id,
            ReleaseVersionId = version.Id,
            Checkpoint = tracked.Checkpoint,
            Tag = tag,
            MergeCommitSha = commit,
            Status = ReleaseStatus.Succeeded,
            Attempt = 1,
            CreatedAt = now,
            CompletedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void AddMarker(IServiceProvider services, AideDbContext db, Guid projectId) =>
        db.AddEvent(
            EventTypes.DemoProjectSeeded,
            projectId: null,
            new { projectId },
            SystemActor.Name,
            services.GetRequiredService<TimeProvider>().GetUtcNow());

    /// <summary>Creates the seed Admin (inside the caller's transaction) with a generated password.</summary>
    private async Task<(AppUser? Admin, string? Password)> CreateAdminAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var password = GeneratePassword();
        var admin = new AppUser
        {
            Email = AdminEmail,
            UserName = AdminEmail,
            Name = AdminName,
            SystemRole = SystemRole.Admin,
            EmailConfirmed = true,
            CreatedAt = services.GetRequiredService<TimeProvider>().GetUtcNow(),
        };
        var result = await users.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            LogAdminFailed(logger, string.Join("; ", result.Errors.Select(e => e.Code)));
            return (null, null);
        }

        return (admin, password);
    }

    /// <summary>20 random characters with at least one letter and one digit (the password policy).</summary>
    public static string GeneratePassword()
    {
        while (true)
        {
            var password = RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength);
            if (password.Any(char.IsLetter) && password.Any(char.IsDigit))
            {
                return password;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seeded admin {Email} with password {Password} (shown once; change it after signing in)")]
    private static partial void LogAdminSeeded(ILogger logger, string email, string password);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded demo project {ProjectId}")]
    private static partial void LogDemoProject(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dev seed skipped: {Count} pending migration(s); apply them and restart")]
    private static partial void LogPendingMigrations(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dev seed could not create the admin account ({Errors})")]
    private static partial void LogAdminFailed(ILogger logger, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dev seed could not create the Local repository of demo project {ProjectId}; it is released without a tagged commit")]
    private static partial void LogDemoRepositoryFailed(ILogger logger, Exception exception, Guid projectId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dev seed failed")]
    private static partial void LogSeedFailed(ILogger logger, Exception exception);
}
