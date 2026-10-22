using Aide.Domain.Entities;
using Aide.Infrastructure;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aide.IntegrationTests.Persistence;

/// <summary>
/// FR-79 / D-V2-5: the LlmCredentials migration applied to a database at ReleaseVersions holding v1 per-project keys
/// (encrypted with the app's protector): each key becomes a connection of the project's earliest Owner and stays bound.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class LlmCredentialMigrationTests(AideApiFactory factory)
{
    private const string PreviousMigration = "20261015174144_ReleaseVersions";
    private const string ProjectKey = "sk-ant-v1-project-key-wxyz";

    private static readonly Guid WithKey = Guid.Parse("01990000-0000-7000-8000-0000000001a0");
    private static readonly Guid OllamaWithStaleKey = Guid.Parse("01990000-0000-7000-8000-0000000001b0");
    private static readonly Guid NoKey = Guid.Parse("01990000-0000-7000-8000-0000000001c0");
    private static readonly Guid EarliestOwner = Guid.Parse("01990000-0000-7000-8000-0000000001e1");
    private static readonly Guid LaterOwner = Guid.Parse("01990000-0000-7000-8000-0000000001e2");
    private static readonly Guid EarlierContributor = Guid.Parse("01990000-0000-7000-8000-0000000001e3");

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR79_v1_project_key_becomes_a_connection_of_the_earliest_owner_and_stays_bound()
    {
        var protector = factory.Services.GetRequiredService<ISecretProtector>();
        var ciphertext = protector.Protect(ProjectKey);
        await using var db = await MigratedDatabaseAsync(ciphertext);

        var credential = Assert.Single(await db.LlmCredentials.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(
            (EarliestOwner, LlmProvider.Anthropic, "Migrated from Keyed", LlmCredential.UnknownLast4, LlmCredentialStatus.Unchecked, (DateTimeOffset?)null),
            (credential.UserId, credential.Provider, credential.Label, credential.Last4, credential.Status, credential.LastValidatedAt));
        Assert.Equal(7, credential.Id.Version);

        // The ciphertext is reused as-is and still decrypts with the app's protector (same purpose).
        Assert.Equal(ciphertext, credential.SecretEncrypted);
        Assert.Equal(ProjectKey, protector.Unprotect(credential.SecretEncrypted));

        var settings = await db.ProjectLlmSettings.AsNoTracking().SingleAsync(s => s.ProjectId == WithKey, Ct);
        Assert.Equal(((Guid?)credential.Id, (Guid?)EarliestOwner, LlmProvider.Anthropic, "claude-opus-5-5"),
            (settings.CredentialId, settings.BoundByUserId, settings.Provider, settings.DefaultModel));

        // A key saved for a provider that takes none, and a project without a key, migrate to no binding.
        Assert.All(
            await db.ProjectLlmSettings.AsNoTracking().Where(s => s.ProjectId == OllamaWithStaleKey || s.ProjectId == NoKey).ToListAsync(Ct),
            s => Assert.Equal(((Guid?)null, (Guid?)null), (s.CredentialId, s.BoundByUserId)));

        Assert.Equal(0, await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'project_llm_settings' AND column_name = 'api_key_encrypted'")
            .SingleAsync(Ct));
    }

    [Fact]
    public async Task FR79_migrated_connection_last4_is_backfilled_once_by_decrypting_the_key()
    {
        var protector = factory.Services.GetRequiredService<ISecretProtector>();
        await using var db = await MigratedDatabaseAsync(protector.Protect(ProjectKey));

        Assert.Equal(1, await LlmCredentialLast4Backfill.FillAsync(db, protector, Ct));
        Assert.Equal(0, await LlmCredentialLast4Backfill.FillAsync(db, protector, Ct));

        Assert.Equal("wxyz", (await db.LlmCredentials.AsNoTracking().SingleAsync(Ct)).Last4);
    }

    [Fact]
    public async Task FR79_deleting_the_bound_connection_clears_the_binding_and_keeps_the_runs()
    {
        await using var db = await MigratedDatabaseAsync(factory.Services.GetRequiredService<ISecretProtector>().Protect(ProjectKey));
        var credential = await db.LlmCredentials.SingleAsync(Ct);
        db.AgentRuns.Add(new AgentRun
        {
            ProjectId = WithKey,
            Agent = Aide.Domain.Agents.AgentKind.IdeaRefinement,
            Phase = Aide.Domain.Lifecycle.Phase.Refinement,
            Provider = LlmProvider.Anthropic,
            Model = "claude-opus-5-5",
            PromptVersion = "idea-refinement@1",
            CredentialId = credential.Id,
            CreatedAt = T0,
        });
        await db.SaveChangesAsync(Ct);

        await db.LlmCredentials.Where(c => c.Id == credential.Id).ExecuteDeleteAsync(Ct);

        Assert.Null(await db.ProjectLlmSettings.Where(s => s.ProjectId == WithKey).Select(s => s.CredentialId).SingleAsync(Ct));
        Assert.Null(await db.AgentRuns.Where(r => r.ProjectId == WithKey).Select(r => r.CredentialId).SingleAsync(Ct));
    }

    /// <summary>A new database at ReleaseVersions, seeded with v1-style LLM settings, then migrated to the latest version.</summary>
    private async Task<AideDbContext> MigratedDatabaseAsync(string ciphertext)
    {
        var connectionString = await factory.CreateEmptyDatabaseAsync(Ct);
        var options = new DbContextOptionsBuilder<AideDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable(DependencyInjection.MigrationsHistoryTable))
            .UseSnakeCaseNamingConvention()
            .Options;
        var db = new AideDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration, Ct);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = Seed;
            command.Parameters.AddWithValue("t0", T0);
            command.Parameters.AddWithValue("key", ciphertext);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await db.Database.MigrateAsync(Ct);
        return db;
    }

    /// <summary>
    /// Project 1a0 has an Anthropic key and three members: a contributor (earliest), an Owner at +1h (expected owner of
    /// the connection) and an Owner at +2h. Project 1b0 is on Ollama with a leftover key; 1c0 is on OpenAI with none.
    /// </summary>
    private const string Seed =
        """
        INSERT INTO users (id, user_name, normalized_user_name, email, normalized_email, email_confirmed, name, system_role, created_at,
                           phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count)
        VALUES ('01990000-0000-7000-8000-0000000001e1', 'early@aide.test', 'EARLY@AIDE.TEST', 'early@aide.test', 'EARLY@AIDE.TEST', true, 'Early', 'user', @t0, false, false, false, 0),
               ('01990000-0000-7000-8000-0000000001e2', 'late@aide.test', 'LATE@AIDE.TEST', 'late@aide.test', 'LATE@AIDE.TEST', true, 'Late', 'user', @t0, false, false, false, 0),
               ('01990000-0000-7000-8000-0000000001e3', 'contrib@aide.test', 'CONTRIB@AIDE.TEST', 'contrib@aide.test', 'CONTRIB@AIDE.TEST', true, 'Contrib', 'user', @t0, false, false, false, 0);
        INSERT INTO projects (id, name, slug, idea, current_phase, status, checkpoint, approval_mode, compliance_threshold,
                              max_files_per_step, max_review_cycles, tokens_used, execution_paused, created_at, updated_at)
        VALUES ('01990000-0000-7000-8000-0000000001a0', 'Keyed', 'keyed', 'i', 'refinement', 'active', 1, 'advisory', 0.9, 20, 3, 0, false, @t0, @t0),
               ('01990000-0000-7000-8000-0000000001b0', 'Ollama', 'ollama', 'i', 'refinement', 'active', 1, 'advisory', 0.9, 20, 3, 0, false, @t0, @t0),
               ('01990000-0000-7000-8000-0000000001c0', 'Nokey', 'nokey', 'i', 'refinement', 'active', 1, 'advisory', 0.9, 20, 3, 0, false, @t0, @t0);
        INSERT INTO project_members (project_id, user_id, role, created_at)
        VALUES ('01990000-0000-7000-8000-0000000001a0', '01990000-0000-7000-8000-0000000001e3', 'contributor', @t0),
               ('01990000-0000-7000-8000-0000000001a0', '01990000-0000-7000-8000-0000000001e2', 'owner', @t0 + interval '2 hours'),
               ('01990000-0000-7000-8000-0000000001a0', '01990000-0000-7000-8000-0000000001e1', 'owner', @t0 + interval '1 hour'),
               ('01990000-0000-7000-8000-0000000001b0', '01990000-0000-7000-8000-0000000001e1', 'owner', @t0),
               ('01990000-0000-7000-8000-0000000001c0', '01990000-0000-7000-8000-0000000001e1', 'owner', @t0);
        INSERT INTO project_llm_settings (project_id, provider, default_model, models_by_agent, api_key_encrypted)
        VALUES ('01990000-0000-7000-8000-0000000001a0', 'anthropic', 'claude-opus-5-5', '{}', @key),
               ('01990000-0000-7000-8000-0000000001b0', 'ollama', 'qwen2.5-coder', '{}', @key),
               ('01990000-0000-7000-8000-0000000001c0', 'openai', 'gpt-5', '{}', NULL);
        """;
}
