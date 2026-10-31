using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Entities;
using Aide.Infrastructure.Git;

namespace Aide.UnitTests.Databases;

/// <summary>§4.21, FR-148, FR-153: the preview database is never production's, and the merge is pinned to the migrated head.</summary>
public class DatabaseSeparationRulesTests
{
    private static DatabaseIdentity Id(ManagedDatabaseProvider provider, string? resourceId = null, string? supabaseRef = null, params string?[] connections) =>
        new(provider, resourceId, supabaseRef, connections);

    [Theory]
    [InlineData(ManagedDatabaseProvider.Supabase, "abcdefgh", " ABCDEFGH ")]
    [InlineData(ManagedDatabaseProvider.Render, "dpg-abc123", "dpg-ABC123")]
    [InlineData(ManagedDatabaseProvider.Railway, "p:s:env1", "p:s:env1")]
    public void S4_21_The_same_resource_id_of_the_same_provider_is_the_same_database(ManagedDatabaseProvider provider, string production, string preview) =>
        Assert.True(DatabaseSeparationRules.Same(Id(provider, production), Id(provider, preview)));

    [Fact]
    public void S4_21_Different_resources_are_separate()
    {
        Assert.False(DatabaseSeparationRules.Same(Id(ManagedDatabaseProvider.Supabase, "abcd"), Id(ManagedDatabaseProvider.Supabase, "efgh")));
        Assert.False(DatabaseSeparationRules.Same(Id(ManagedDatabaseProvider.Railway, "p:s:production"), Id(ManagedDatabaseProvider.Railway, "p:s:preview")));
        Assert.False(DatabaseSeparationRules.Same(Id(ManagedDatabaseProvider.Render, "x"), Id(ManagedDatabaseProvider.Railway, "x")));
        Assert.False(DatabaseSeparationRules.Same(Id(ManagedDatabaseProvider.Supabase), Id(ManagedDatabaseProvider.Supabase)));
    }

    [Fact]
    public void S4_21_A_Supabase_ref_matches_whichever_field_holds_it() =>
        Assert.True(DatabaseSeparationRules.Same(Id(ManagedDatabaseProvider.Supabase, null, "abcd"), Id(ManagedDatabaseProvider.Supabase, "ABCD")));

    [Theory]
    [InlineData("postgresql://u:p@db.example.com:5432/app", "postgres://other:secret@DB.EXAMPLE.COM/app")]
    [InlineData("postgresql://u:p@db.example.com/postgres", "Host=db.example.com;Port=5432;Username=x")]
    [InlineData("Host=db.example.com;Port=6543;Database=App", "postgresql://u@db.example.com:6543/app")]
    public void S4_21_A_connection_string_with_the_same_host_port_and_database_is_the_same_database(string production, string preview) =>
        Assert.True(DatabaseSeparationRules.Same(Id(ManagedDatabaseProvider.External, null, null, production), Id(ManagedDatabaseProvider.External, null, null, preview)));

    [Theory]
    [InlineData("postgresql://u:p@db.example.com:5432/app", "postgresql://u:p@db.example.com:5433/app")]
    [InlineData("postgresql://u:p@db.example.com:5432/app", "postgresql://u:p@db.example.com:5432/app_preview")]
    [InlineData("postgresql://u:p@db.example.com:5432/app", "postgresql://u:p@preview.example.com:5432/app")]
    public void S4_21_A_different_port_database_or_host_is_separate(string production, string preview) =>
        Assert.False(DatabaseSeparationRules.Same(Id(ManagedDatabaseProvider.External, null, null, production), Id(ManagedDatabaseProvider.External, null, null, preview)));

    [Fact]
    public void S4_21_An_external_preview_on_a_Render_production_external_url_is_the_same_database()
    {
        var render = Id(ManagedDatabaseProvider.Render, "dpg-1", null, "postgresql://u:p@dpg-1-a/app", "postgresql://u:p@dpg-1-a.oregon-postgres.render.com/app");
        var external = Id(ManagedDatabaseProvider.External, null, null, "postgresql://me:pw@dpg-1-a.oregon-postgres.render.com:5432/app");

        Assert.True(DatabaseSeparationRules.Same(external, render));
    }

    [Fact]
    public void S4_21_An_identity_comes_from_the_row_and_its_connection()
    {
        var row = new ManagedDatabase { Provider = ManagedDatabaseProvider.Render, ResourceId = "dpg-1" };
        var identity = DatabaseIdentity.Of(row, new ManagedDatabaseConnection { Url = "postgresql://a@h/db", ExternalUrl = "postgresql://a@h.ext/db", SupabaseRef = "r" });

        Assert.Equal((ManagedDatabaseProvider.Render, "dpg-1", "r"), (identity.Provider, identity.ResourceId, identity.SupabaseRef));
        Assert.Equal(["postgresql://a@h/db", "postgresql://a@h.ext/db", null], identity.ConnectionStrings);
    }

    private const string Ref = "abcdefghijklmnop";
    private const string Pooler = "aws-0-eu-west-2.pooler.supabase.com";

    /// <summary>The production Supabase project <see cref="Ref"/>, as AIDE stores it (ref, resource id, session pooler).</summary>
    private static DatabaseIdentity LinkedSupabase(string supabaseRef = Ref) =>
        DatabaseIdentity.Of(
            new ManagedDatabase { Provider = ManagedDatabaseProvider.Supabase, ResourceId = supabaseRef },
            new ManagedDatabaseConnection { SupabaseRef = supabaseRef, PoolerHost = Pooler, PoolerUser = $"postgres.{supabaseRef}", DatabaseName = "postgres" });

    private static DatabaseIdentity External(string connection) => Id(ManagedDatabaseProvider.External, null, null, connection);

    [Theory]
    [InlineData($"postgresql://postgres:pw@db.{Ref}.supabase.co:5432/postgres")]
    [InlineData($"postgresql://postgres.{Ref}:pw@{Pooler}:5432/postgres")]
    [InlineData($"postgresql://postgres.{Ref}:pw@{Pooler}:6543/postgres")]
    [InlineData($"Host={Pooler};Port=6543;Username=postgres.{Ref};Database=postgres")]
    [InlineData($"postgresql://postgres.{Ref}:pw@aws-1-us-east-1.pooler.supabase.com:6543/postgres")]
    public void S4_21_An_External_URI_of_the_linked_Supabase_project_is_the_same_database(string connection)
    {
        Assert.True(DatabaseSeparationRules.Same(External(connection), LinkedSupabase()));
        Assert.True(DatabaseSeparationRules.Same(LinkedSupabase(), External(connection)));
    }

    [Theory]
    [InlineData($"postgresql://postgres:pw@db.{Ref}.supabase.co:5432/postgres", $"postgresql://postgres.{Ref}:pw@{Pooler}:5432/postgres")]
    [InlineData($"postgresql://postgres.{Ref}:pw@{Pooler}:5432/postgres", $"postgresql://postgres.{Ref}:other@{Pooler}:6543/postgres")]
    [InlineData($"postgresql://postgres:pw@db.{Ref}.supabase.co:5432/postgres", $"postgresql://postgres.{Ref}:pw@{Pooler}:6543/postgres")]
    public void S4_21_External_URIs_of_one_Supabase_project_direct_session_or_transaction_are_the_same_database(string production, string preview) =>
        Assert.True(DatabaseSeparationRules.Same(External(production), External(preview)));

    [Fact]
    public void S4_21_Different_Supabase_projects_on_the_same_regional_pooler_are_separate()
    {
        Assert.False(DatabaseSeparationRules.Same(LinkedSupabase(), LinkedSupabase("qrstuvwxyzabcdef")));
        Assert.False(DatabaseSeparationRules.Same(
            External($"postgresql://postgres.{Ref}:pw@{Pooler}:5432/postgres"), External($"postgresql://postgres.qrstuvwxyzabcdef:pw@{Pooler}:5432/postgres")));
        Assert.False(DatabaseSeparationRules.Same(External($"postgresql://postgres.qrstuvwxyzabcdef:pw@{Pooler}:6543/postgres"), LinkedSupabase()));
        Assert.False(DatabaseSeparationRules.Same(External("postgresql://postgres:pw@db.qrstuvwxyzabcdef.supabase.co/postgres"), LinkedSupabase()));
    }

    [Fact]
    public void S4_21_A_Supabase_ref_comes_from_the_direct_host_or_the_pooler_user()
    {
        Assert.Equal(Ref, DatabaseSeparationRules.SupabaseRefOf(DatabaseSeparationRules.Endpoint($"postgresql://postgres@DB.{Ref.ToUpperInvariant()}.supabase.co/postgres")));
        Assert.Equal(Ref, DatabaseSeparationRules.SupabaseRefOf(DatabaseSeparationRules.Endpoint($"postgresql://postgres.{Ref}@{Pooler}:6543/postgres")));
        Assert.Null(DatabaseSeparationRules.SupabaseRefOf(DatabaseSeparationRules.Endpoint($"postgresql://app@{Pooler}:6543/postgres")));
        Assert.Null(DatabaseSeparationRules.SupabaseRefOf(DatabaseSeparationRules.Endpoint($"postgresql://postgres.{Ref}@db.example.com/postgres")));
        Assert.Null(DatabaseSeparationRules.SupabaseRefOf(null));
    }

    [Fact]
    public void S4_21_Unreadable_connection_strings_name_no_endpoint()
    {
        Assert.Null(DatabaseSeparationRules.Endpoint(null));
        Assert.Null(DatabaseSeparationRules.Endpoint("   "));
        Assert.Null(DatabaseSeparationRules.Endpoint("Port=5432;Database=app"));
        Assert.Equal(new DatabaseEndpoint("db.example.com", 5432, "postgres"), DatabaseSeparationRules.Endpoint("postgresql://db.example.com."));
    }

    [Fact]
    public void FR153_The_merge_is_refused_when_the_head_moved_since_the_production_migrations()
    {
        var pinned = new ReleaseRequest("aide/todo/v0.1.0", "v0.1.0", "0.1.0", "Todo", 7) { ExpectedHeadSha = new string('a', 40) };

        Assert.Null(pinned.HeadMovedSinceMigrations(new string('a', 40)));
        var moved = pinned.HeadMovedSinceMigrations(new string('b', 40));
        Assert.NotNull(moved);
        Assert.Equal((ReleaseOutcomeKind.Failed, ErrorCodes.GitError, true), (moved.Kind, moved.ErrorCode, moved.Transient));
        Assert.Null((pinned with { ExpectedHeadSha = null }).HeadMovedSinceMigrations(new string('b', 40)));
    }
}
