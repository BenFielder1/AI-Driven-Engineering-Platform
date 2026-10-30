using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Stack;
using Aide.UnitTests.Stack;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-145, FR-146, §4.22: the origin model, the derived environment variables and the inventory rules.</summary>
public class DeploymentOriginAndEnvTests
{
    private const string PagesUrl = "https://acme.github.io/todo";
    private const string RailwayProduction = "https://todo-api-production.up.railway.app";
    private const string RailwayPreview = "https://todo-api-aide-preview.up.railway.app";

    private static readonly IReadOnlySet<string> NoDerivedKeys = new HashSet<string>(StringComparer.Ordinal);
    private static readonly DeploymentEnvironment[] Both = [DeploymentEnvironment.Preview, DeploymentEnvironment.Production];
    private static readonly DeploymentComponent[] FrontendOnly = [DeploymentComponent.Frontend];
    private static readonly DeploymentComponent[] BackendOnly = [DeploymentComponent.Backend];

    private static StackComposition PagesAndRailway() => StackTestCatalog.Valid(StackTestCatalog.Sel(
        frontend: "fe-react-vite", backend: "be-fastapi", vcs: "vcs-github", frontendHost: "deploy-github-pages", backendHost: "deploy-railway"));

    private static DerivedEnvTarget PagesTarget => new(DeploymentComponent.Frontend, DeploymentProvider.GithubPages, "todo", PagesUrl, null);

    private static DerivedEnvTarget RailwayTarget => new(DeploymentComponent.Backend, DeploymentProvider.Railway, "todo-api", RailwayProduction, RailwayPreview);

    private static DerivedEnvVar Var(IReadOnlyList<DerivedEnvVar> vars, string key) => Assert.Single(vars, v => v.Key == key);

    // ------------------------------------------------------------------ FR-146 origin model and derived variables

    [Fact]
    public void FR146_A_React_SPA_on_GitHub_Pages_with_FastAPI_on_Railway_is_cross_origin_with_VITE_API_BASE_URL_and_CORS_from_the_targets()
    {
        var composition = PagesAndRailway();
        Assert.Equal(OriginModels.CrossOrigin, composition.OriginModel);

        var vars = DerivedEnvRules.Derive(new DerivedEnvInput(composition, "VITE_", [PagesTarget, RailwayTarget], []));

        Assert.Equal(["CORS_ALLOWED_ORIGINS", "VITE_API_BASE_URL"], vars.Select(v => v.Key).ToArray());
        var apiBase = Var(vars, "VITE_API_BASE_URL");
        Assert.Equal((true, false), (apiBase.Public, apiBase.Sensitive));
        Assert.Equal(FrontendOnly, apiBase.AppliesTo);
        Assert.Equal(RailwayProduction, apiBase.Values[DeploymentEnvironment.Production]);
        Assert.Equal(RailwayPreview, apiBase.Values[DeploymentEnvironment.Preview]);
        Assert.Contains("Railway", apiBase.DerivedFrom, StringComparison.Ordinal);

        // The Pages URL's origin (no path); Pages has no previews, so there is no preview origin.
        var cors = Var(vars, "CORS_ALLOWED_ORIGINS");
        Assert.Equal(BackendOnly, cors.AppliesTo);
        Assert.Equal("https://acme.github.io", cors.Values[DeploymentEnvironment.Production]);
        Assert.False(cors.Values.ContainsKey(DeploymentEnvironment.Preview));
        Assert.Contains("GitHub Pages", cors.DerivedFrom, StringComparison.Ordinal);
    }

    [Fact]
    public void FR146_Derived_variables_have_no_value_until_the_backend_target_has_a_URL()
    {
        var vars = DerivedEnvRules.Derive(new DerivedEnvInput(PagesAndRailway(), "VITE_", [], []));

        Assert.Empty(Var(vars, "VITE_API_BASE_URL").Values);
        Assert.Empty(Var(vars, "CORS_ALLOWED_ORIGINS").Values);
        Assert.Contains("Railway", Var(vars, "VITE_API_BASE_URL").DerivedFrom, StringComparison.Ordinal);
    }

    [Fact]
    public void FR146_Proxy_derives_API_ORIGIN_for_the_frontend_and_no_CORS()
    {
        var composition = PagesAndRailway() with { OriginModel = OriginModels.Proxy };

        var vars = DerivedEnvRules.Derive(new DerivedEnvInput(composition, "VITE_", [PagesTarget, RailwayTarget], []));

        var origin = Assert.Single(vars);
        Assert.Equal((DerivedEnvRules.ApiOrigin, false), (origin.Key, origin.Public));
        Assert.Equal(FrontendOnly, origin.AppliesTo);
        Assert.Equal(RailwayProduction, origin.Values[DeploymentEnvironment.Production]);
    }

    [Fact]
    public void FR146_Same_origin_derives_nothing_except_for_an_SPA_container_proxying_to_a_backend_on_Railway()
    {
        var sameOrigin = PagesAndRailway() with { OriginModel = OriginModels.SameOrigin };
        Assert.Empty(DerivedEnvRules.Derive(new DerivedEnvInput(sameOrigin, "VITE_", [PagesTarget, RailwayTarget], [])));

        var railway = sameOrigin with { Deploy = sameOrigin.Deploy with { FrontendHost = "deploy-railway" } };
        var vars = DerivedEnvRules.Derive(new DerivedEnvInput(railway, "VITE_", [RailwayTarget], []));
        Assert.Equal(DerivedEnvRules.ApiOrigin, Assert.Single(vars).Key);
    }

    [Fact]
    public void FR146_CORS_for_a_Vercel_frontend_adds_its_preview_URL_and_preview_pattern()
    {
        var composition = PagesAndRailway() with { Deploy = PagesAndRailway().Deploy with { FrontendHost = "deploy-vercel" } };
        var vercel = new DerivedEnvTarget(DeploymentComponent.Frontend, DeploymentProvider.Vercel, "todo-web", "https://todo-web.vercel.app/", "https://todo-web-git-aide-preview.vercel.app");

        var cors = Var(DerivedEnvRules.Derive(new DerivedEnvInput(composition, "VITE_", [vercel, RailwayTarget], [])), DerivedEnvRules.CorsAllowedOrigins);

        Assert.Equal("https://todo-web.vercel.app", cors.Values[DeploymentEnvironment.Production]);
        Assert.Equal("https://todo-web-git-aide-preview.vercel.app,https://todo-web-*.vercel.app", cors.Values[DeploymentEnvironment.Preview]);
    }

    [Fact]
    public void FR146_A_Next_js_frontend_in_ui_mode_gets_BACKEND_URL_and_the_NEXT_PUBLIC_API_base_when_cross_origin()
    {
        var composition = PagesAndRailway() with
        {
            Frontend = new StackComponent(StackIds.NextJs, "frontend"),
            Derived = PagesAndRailway().Derived with { NextjsMode = StackIds.Ui },
        };

        var vars = DerivedEnvRules.Derive(new DerivedEnvInput(composition, "NEXT_PUBLIC_", [RailwayTarget], []));

        Assert.Equal(["BACKEND_URL", "CORS_ALLOWED_ORIGINS", "NEXT_PUBLIC_API_BASE_URL"], vars.Select(v => v.Key).ToArray());
        Assert.False(Var(vars, "BACKEND_URL").Public);
        Assert.Equal(RailwayPreview, Var(vars, "BACKEND_URL").Values[DeploymentEnvironment.Preview]);
    }

    [Fact]
    public void FR146_An_API_only_or_frontend_only_composition_derives_nothing()
    {
        var composition = PagesAndRailway();
        Assert.Empty(DerivedEnvRules.Derive(new DerivedEnvInput(composition with { Frontend = null }, null, [RailwayTarget], [])));
        Assert.Empty(DerivedEnvRules.Derive(new DerivedEnvInput(composition with { Backend = null }, "VITE_", [PagesTarget], [])));
        Assert.Empty(DerivedEnvRules.Derive(new DerivedEnvInput(null, null, [], [])));
        Assert.Equal(["CORS_ALLOWED_ORIGINS", "VITE_API_BASE_URL"], DerivedEnvRules.DerivedKeys(new DerivedEnvInput(composition, "VITE_", [], [])).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("same-origin", "same-origin")]
    [InlineData("proxy", "same-origin")]
    [InlineData("cross-origin", "cross-origin")]
    [InlineData(null, null)]
    public void FR146_AIDE_origin_models_map_to_the_stack_packs_vocabulary(string? model, string? pack)
    {
        Assert.Equal(pack, OriginModels.ToPackVocabulary(model));
        Assert.Equal(model is null, OriginModels.Explanation(model) is null);
    }

    [Theory]
    [InlineData("https://a.example.com/path/", "https://a.example.com")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("ftp://a.example.com", null)]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void FR146_Derived_URLs_are_origins(string? url, string? origin) => Assert.Equal(origin, DerivedEnvRules.Origin(url));

    [Theory]
    [InlineData(DeploymentProvider.Vercel, "https://web-*.vercel.app")]
    [InlineData(DeploymentProvider.Netlify, "https://*--web.netlify.app")]
    [InlineData(DeploymentProvider.Cloudflare, "https://*-web.*.workers.dev")]
    [InlineData(DeploymentProvider.GithubPages, null)]
    [InlineData(DeploymentProvider.Render, null)]
    public void FR146_Preview_URL_patterns_per_frontend_host(DeploymentProvider provider, string? pattern) =>
        Assert.Equal(pattern, DerivedEnvRules.PreviewPattern(provider, "web"));

    // ------------------------------------------------------------------ FR-145 inventory rules

    [Theory]
    [InlineData("VITE_STRIPE_SECRET", "abc")]
    [InlineData("NEXT_PUBLIC_DB_PASSWORD", "abc")]
    [InlineData("VITE_SUPABASE_KEY", "sb_secret_abc123")]
    [InlineData("VITE_PAYMENTS_KEY", "sk_live_abc123")]
    public void FR145_A_public_variable_that_looks_secret_is_refused(string key, string value)
    {
        var result = EnvInventoryRules.ValidateUser(key, value, Both, null, EnvInventoryRules.IsPublic(key, null, "VITE_"), NoDerivedKeys);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error!.Code);
        Assert.Contains(EnvInventoryRules.PublicSecretMessage, result.Error.Message, StringComparison.Ordinal);
        Assert.Contains(result.Error.Details, d => d.StartsWith("public:", StringComparison.Ordinal));
    }

    [Fact]
    public void FR145_The_same_secret_is_accepted_when_it_is_not_public()
    {
        Assert.True(EnvInventoryRules.ValidateUser("STRIPE_SECRET", "sk_live_abc123", Both, BackendOnly, false, NoDerivedKeys).IsSuccess);
        Assert.True(EnvInventoryRules.ValidateUser("VITE_SUPABASE_URL", "https://abc.supabase.co", Both, FrontendOnly, true, NoDerivedKeys).IsSuccess);
    }

    [Fact]
    public void FR145_A_prefixed_key_is_always_public_and_an_explicit_flag_makes_any_key_public()
    {
        Assert.True(EnvInventoryRules.IsPublic("VITE_X", false, null));
        Assert.True(EnvInventoryRules.IsPublic("NEXT_PUBLIC_X", null, "VITE_"));
        Assert.True(EnvInventoryRules.IsPublic("NG_X", null, "NG_"));
        Assert.True(EnvInventoryRules.IsPublic("API_KEY", true, "VITE_"));
        Assert.False(EnvInventoryRules.IsPublic("API_KEY", null, "VITE_"));
    }

    [Fact]
    public void FR145_A_user_value_on_a_derived_key_is_refused()
    {
        var derived = DerivedEnvRules.DerivedKeys(new DerivedEnvInput(PagesAndRailway(), "VITE_", [], []));

        var result = EnvInventoryRules.ValidateUser("CORS_ALLOWED_ORIGINS", "https://evil.example", Both, BackendOnly, false, derived);

        Assert.True(result.IsFailure);
        Assert.Contains(result.Error!.Details, d => d.StartsWith("key:", StringComparison.Ordinal));
    }

    [Fact]
    public void FR145_A_public_variable_applies_to_the_frontend_and_appliesTo_lists_each_component_once()
    {
        AssertField(EnvInventoryRules.ValidateUser("VITE_X", "1", Both, BackendOnly, true, NoDerivedKeys), "appliesTo");
        AssertField(EnvInventoryRules.ValidateUser("X", "1", Both, [], false, NoDerivedKeys), "appliesTo");
        AssertField(EnvInventoryRules.ValidateUser("X", "1", Both, [DeploymentComponent.Backend, DeploymentComponent.Backend], false, NoDerivedKeys), "appliesTo");
    }

    [Fact]
    public void FR145_The_inventory_lists_derived_then_user_then_missing_required_rows_by_key()
    {
        var now = DateTimeOffset.UnixEpoch;
        var derived = DerivedEnvRules.Derive(new DerivedEnvInput(PagesAndRailway(), "VITE_", [RailwayTarget], []));
        IReadOnlyList<EnvUserVar> users =
        [
            new("VITE_API_BASE_URL", FrontendOnly, Both, false, true, now, null, null),
            new("SENTRY_DSN", FrontendOnly, Both, false, false, now, now, "Vercel refused it."),
        ];
        IReadOnlyList<StackEnvVar> required =
        [
            new("SENTRY_DSN", ["frontend"], ["preview", "production"], false, false, "Error reporting.", ["fe-react-vite"]),
            new("SECRET_KEY", ["backend"], ["production"], true, false, "Signing key.", ["be-fastapi"]),
        ];
        var sync = new Dictionary<string, EnvSyncState>(StringComparer.Ordinal) { ["VITE_API_BASE_URL"] = new(now, now, null) };

        var rows = EnvInventoryRules.Build(derived, sync, users, required);

        Assert.Equal(["CORS_ALLOWED_ORIGINS", "SECRET_KEY", "SENTRY_DSN", "VITE_API_BASE_URL"], rows.Select(r => r.Key).ToArray());
        var apiBase = rows.Single(r => r.Key == "VITE_API_BASE_URL");
        Assert.Equal((DeploymentEnvSource.Derived, true, (DateTimeOffset?)now), (apiBase.Source, apiBase.HasValue, apiBase.SyncedAt));
        Assert.NotNull(apiBase.DerivedFrom);
        var cors = rows.Single(r => r.Key == "CORS_ALLOWED_ORIGINS");
        Assert.Equal((DeploymentEnvSource.Derived, false), (cors.Source, cors.HasValue));
        var sentry = rows.Single(r => r.Key == "SENTRY_DSN");
        Assert.Equal((DeploymentEnvSource.User, true, "Vercel refused it."), (sentry.Source, sentry.HasValue, sentry.SyncError));
        var secret = rows.Single(r => r.Key == "SECRET_KEY");
        Assert.Equal((DeploymentEnvSource.Required, false, true, "Signing key."), (secret.Source, secret.HasValue, secret.Sensitive, secret.Description));
        Assert.Equal(BackendOnly, secret.AppliesTo);

        Assert.Equal(["CORS_ALLOWED_ORIGINS", "SECRET_KEY"], EnvInventoryRules.MissingRequired(rows, DeploymentComponent.Backend));
        Assert.Empty(EnvInventoryRules.MissingRequired(rows, DeploymentComponent.Frontend));
    }

    [Fact]
    public void FR145_Derived_values_round_trip_per_environment()
    {
        var values = new Dictionary<DeploymentEnvironment, string> { [DeploymentEnvironment.Production] = "https://a", [DeploymentEnvironment.Preview] = "https://b,https://c-*.vercel.app" };

        var json = DerivedEnvValues.Serialize(values);

        Assert.Equal("""{"preview":"https://b,https://c-*.vercel.app","production":"https://a"}""", json);
        Assert.Equal(values, DerivedEnvValues.Parse(json));
        Assert.Empty(DerivedEnvValues.Parse("not json"));
        Assert.Empty(DerivedEnvValues.Parse("[1]"));
    }

    private static void AssertField(Result result, string field)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.StartsWith(field + ":", StringComparison.Ordinal));
    }
}
