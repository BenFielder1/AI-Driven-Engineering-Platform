using System.Security.Cryptography;
using Aide.Api.Auth;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Release;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Aide.Infrastructure.Presets;
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
/// <para>
/// §9 (v2): a second demo project, <see cref="DemoPresetProjectName"/>, uses the <see cref="DemoPresetId"/> preset (Fake +
/// Local) and starts in <b>planning</b>: its project request and specification (with the preset's sections) are seeded
/// locked, and the plan generation job is queued, so the Fake's scripted specialist plan appears as a draft. It has its own
/// marker (<see cref="EventTypes.DemoPresetProjectSeeded"/>), so databases seeded before it get it on the next start.
/// </para>
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

    /// <summary>§9: the second demo project, on a preset, in planning.</summary>
    public const string DemoPresetProjectName = "Demo: Recipe Finder";

    /// <summary>§9: the preset of the second demo project.</summary>
    public const string DemoPresetId = "nextjs-frontend";

    /// <summary>The reason recorded on the preset demo's seeded transition into planning.</summary>
    public const string DemoPresetReason = "Demo project seeded in planning on the nextjs-frontend preset";

    public const string DemoPresetIdea =
        "A Next.js web app to search recipes from a public recipe API, view a recipe's ingredients and steps, and keep a list of favourites in the browser. There is no database of its own: the app calls the recipe API through backend-for-frontend route handlers, and tests use mocked API responses.";

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
        Guid? presetProjectId = null;

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

            if (!await db.Events.AnyAsync(e => e.Type == EventTypes.DemoPresetProjectSeeded, cancellationToken))
            {
                presetProjectId = await CreatePresetDemoProjectAsync(services, db, admin, cancellationToken);
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

        if (presetProjectId is { } presetProject)
        {
            LogDemoProject(logger, presetProject);
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

        await UseFakeLlmAsync(services, db, created.Value.Id, cancellationToken);
        AddMarker(services, db, created.Value.Id);
        await db.SaveChangesAsync(cancellationToken);
        await MarkDemoReleasedAsync(services, db, created.Value, cancellationToken);
        return created.Value.Id;
    }

    /// <summary>The demos always run offline on the Fake provider, whatever the system default is.</summary>
    private static async Task UseFakeLlmAsync(IServiceProvider services, AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        var llm = await db.ProjectLlmSettings.FirstAsync(s => s.ProjectId == projectId, cancellationToken);
        if (llm.Provider != LlmProvider.Fake)
        {
            var catalog = services.GetRequiredService<ILlmProviderCatalog>();
            llm.Provider = LlmProvider.Fake;
            llm.DefaultModel = catalog.DefaultModel(LlmProvider.Fake);
            llm.ModelsByAgent = new Dictionary<string, string>(catalog.DefaultAgentModels(LlmProvider.Fake));
        }
    }

    /// <summary>
    /// §9: the preset demo, created once (its marker is written even when the preset isn't shipped, so a broken preset
    /// doesn't retry on every start). The project request (checkpoint 1) and the specification (checkpoint 2, the eleven
    /// sections plus the preset's) are locked, the project is put in planning at checkpoint 3 with one seeded transition,
    /// and the plan generation job is queued in the same transaction (a seed shortcut, like the released demo).
    /// </summary>
    private static async Task<Guid?> CreatePresetDemoProjectAsync(IServiceProvider services, AideDbContext db, AppUser admin, CancellationToken cancellationToken)
    {
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var preset = services.GetRequiredService<IPresetCatalog>().Find(DemoPresetId);
        if (preset is null)
        {
            db.AddEvent(EventTypes.DemoPresetProjectSeeded, projectId: null, new { projectId = (Guid?)null, presetId = DemoPresetId, skipped = "preset not loaded" }, SystemActor.Name, now);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        var created = await services.GetRequiredService<ProjectCreator>().CreateAsync(
            new CreateProjectRequest
            {
                Name = DemoPresetProjectName,
                Idea = DemoPresetIdea,
                ApprovalMode = ApprovalMode.Advisory,
                PresetId = DemoPresetId,
                Git = new CreateProjectGitRequest { Provider = GitProvider.Local },
            },
            admin.Id,
            SystemActor.Name,
            cancellationToken);
        if (created.IsFailure)
        {
            throw new InvalidOperationException($"The preset demo project is invalid: {created.Error!.Message}");
        }

        var projectId = created.Value.Id;
        await UseFakeLlmAsync(services, db, projectId, cancellationToken);
        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);
        var versionId = project.CurrentReleaseVersionId!.Value;
        const int planningCheckpoint = 3;
        AddLockedArtifact(db, project, versionId, ArtifactType.ProjectRequest, 1, PresetDemoRequest(), admin.Id, now);
        AddLockedArtifact(db, project, versionId, ArtifactType.TechnicalSpecification, 2, PresetDemoSpecification(preset.SpecSectionTitles), admin.Id, now);

        project.CurrentPhase = Phase.Planning;
        project.Status = PhaseOrder.StatusFor(Phase.Planning);
        project.Checkpoint = planningCheckpoint;
        project.UpdatedAt = now;
        db.StateTransitions.Add(new StateTransition
        {
            ProjectId = projectId,
            ReleaseVersionId = versionId,
            FromPhase = Phase.Refinement,
            ToPhase = Phase.Planning,
            Actor = SystemActor.Name,
            Reason = DemoPresetReason,
            Checkpoint = planningCheckpoint,
            CreatedAt = now,
        });
        await services.GetRequiredService<JobEnqueuer>().EnqueueAsync(
            JobTypes.PlanGeneration,
            new LifecycleJobPayload(projectId, planningCheckpoint, LifecycleJobPayload.PhaseEntered),
            projectId,
            cancellationToken,
            now);
        db.AddEvent(EventTypes.DemoPresetProjectSeeded, projectId: null, new { projectId, presetId = DemoPresetId }, SystemActor.Name, now);
        await db.SaveChangesAsync(cancellationToken);
        return projectId;
    }

    private static void AddLockedArtifact(AideDbContext db, Project project, Guid versionId, ArtifactType type, int checkpoint, string content, Guid authorId, DateTimeOffset now) =>
        db.Artifacts.Add(new Artifact
        {
            ProjectId = project.Id,
            ReleaseVersionId = versionId,
            Type = type,
            Version = 1,
            Content = content,
            ContentHash = ContentHash.Compute(content),
            Status = ArtifactStatus.Locked,
            CheckpointAtCreation = checkpoint,
            CreatedByUserId = authorId,
            ApprovedBy = authorId,
            ApprovedAt = now,
            CreatedAt = now,
        });

    private static string PresetDemoRequest() =>
        $"# {DemoPresetProjectName}\n\n## Summary\n{DemoPresetIdea}\n\n## Users and roles\n- **Visitor**: searches recipes and keeps favourites in this browser.\n\n## Core features\n**Must**\n- Search recipes by name and list the results.\n- View a recipe's ingredients and steps.\n- Save and remove favourites (kept in the browser).\n- Show clear loading, empty and error states.\n\n## Out of scope\n- Accounts and a database of its own.\n";

    /// <summary>A valid specification (FR-75): the eleven sections, FR-1–FR-4 with criteria, and the preset's sections.</summary>
    private static string PresetDemoSpecification(IReadOnlyList<string> presetSections)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Overview"] = "Demo: Recipe Finder lets visitors search a public recipe API, view recipes and keep favourites in the browser.",
            ["Functional Requirements"] = "- **FR-1** Search recipes. Given a query, when the visitor searches, then matching recipes are listed.\n- **FR-2** List results with loading, empty and error states. Given no matches, when the search finishes, then an empty state is shown.\n- **FR-3** View a recipe. Given a recipe id, when it is opened, then its ingredients and steps are shown; an unknown id shows not found.\n- **FR-4** Favourites. Given a recipe, when the visitor saves it, then it appears in the favourites list after a reload.",
            ["Technical Architecture"] = "Next.js (App Router) with TypeScript strict and Tailwind; BFF route handlers under app/api call the recipe API.\n\n### Dependencies\n- next\n- react\n- react-dom\n- zod\n- vitest\n- @playwright/test",
            ["Data Models"] = "**Recipe**: `id` (string), `title` (string), `ingredients` (string[]), `steps` (string[]). **Favourite**: `recipeId` (string), `savedAt` (ISO timestamp).",
            ["API Contracts"] = "| Method | Path | Success | Errors |\n| --- | --- | --- | --- |\n| GET | /api/recipes | 200 list | 400, 502 |\n| GET | /api/recipes/{id} | 200 recipe | 404, 502 |",
            ["Feature Specifications"] = "Queries are trimmed (1–100 characters). Favourites live in localStorage.",
            ["Design System & UI/UX"] = "A search page and a recipe page; 44 px tap targets, WCAG AA contrast, works at 360 px wide.",
            ["Security & Authentication"] = "No accounts. The recipe API key, if any, stays server-side in route handlers.",
            ["Testing Strategy"] = "Vitest for the pure rules and route handlers (mocked upstream), Playwright for search, view and favourites.",
            ["Deployment & Infrastructure"] = "Vercel, from GitHub; environment variables per the Environment Variables section.",
            ["Open Questions"] = "- Which recipe API to use: default to a free public API with a server-side key.",
        };
        var sections = SpecSections.Required.Select((title, i) => $"## {i + 1}. {title}\n{bodies.GetValueOrDefault(title, title)}");
        var extra = presetSections.Select(title => $"## {title}\n{title} of {DemoPresetProjectName}: see the requirements and API contracts above.");
        return string.Join("\n\n", sections.Concat(extra)) + "\n";
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
