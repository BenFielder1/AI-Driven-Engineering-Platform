using System.Text;
using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Stack;

namespace Aide.Domain.Deployments;

/// <summary>A target as the §4.22 derivation sees it: its component, provider, resource name and URLs.</summary>
public sealed record DerivedEnvTarget(DeploymentComponent Component, DeploymentProvider Provider, string? Name, string? ProductionUrl, string? PreviewUrl);

/// <summary>
/// A database as the §4.22 derivation will see it (M30, FR-152/FR-153): its environment and provider. M29 passes none; the
/// <c>DATABASE_URL</c>, <c>DIRECT_URL</c>, <c>AIDE_RUN_MIGRATIONS</c> and Supabase rows plug in here.
/// </summary>
public sealed record DerivedEnvDatabase(DeploymentEnvironment Environment, string Provider, string DisplayName);

/// <summary>
/// The inputs of <see cref="DerivedEnvRules.Derive"/>: the composition, the frontend pack's public prefix
/// (<c>NEXT_PUBLIC_</c>, <c>VITE_</c>, or <c>null</c> for Angular), the targets and the databases.
/// </summary>
public sealed record DerivedEnvInput(
    StackComposition? Composition,
    string? PublicPrefix,
    IReadOnlyList<DerivedEnvTarget> Targets,
    IReadOnlyList<DerivedEnvDatabase> Databases);

/// <summary>
/// A variable AIDE derives (§4.22): key, the components it applies to, its value per environment (only the environments
/// whose source is known; empty while the backend has no URL yet), whether it is public or sensitive, and where it comes from.
/// </summary>
public sealed record DerivedEnvVar(
    string Key,
    IReadOnlyList<DeploymentComponent> AppliesTo,
    IReadOnlyDictionary<DeploymentEnvironment, string> Values,
    bool Public,
    bool Sensitive,
    string DerivedFrom);

/// <summary>
/// FR-145, FR-146, §4.22: the derived environment variables, a pure function of the composition, the targets and (from M30)
/// the databases:
/// <list type="bullet">
/// <item><c>API_ORIGIN</c> (frontend): origin model <c>proxy</c>, or <c>same-origin</c> with frontend and backend both on
/// Railway (the SPA container's proxy target) — the backend URL per environment.</item>
/// <item><c>&lt;prefix&gt;API_BASE_URL</c> (frontend, public; <c>VITE_API_BASE_URL</c>, <c>NEXT_PUBLIC_API_BASE_URL</c>, Angular
/// <c>API_BASE_URL</c>): <c>cross-origin</c> — the backend URL per environment.</item>
/// <item><c>BACKEND_URL</c> (frontend, server-side): <c>fe-nextjs</c> in ui mode — the backend URL per environment.</item>
/// <item><c>CORS_ALLOWED_ORIGINS</c> (backend): <c>cross-origin</c> — production: the frontend's production URL; preview: the
/// frontend's preview URL and its host's preview URL pattern.</item>
/// </list>
/// Recomputed whenever a target URL (or, from M30, a database) changes.
/// </summary>
public static class DerivedEnvRules
{
    public const string ApiOrigin = "API_ORIGIN";
    public const string ApiBaseUrl = "API_BASE_URL";
    public const string BackendUrl = "BACKEND_URL";
    public const string CorsAllowedOrigins = "CORS_ALLOWED_ORIGINS";

    private static readonly DeploymentComponent[] FrontendOnly = [DeploymentComponent.Frontend];
    private static readonly DeploymentComponent[] BackendOnly = [DeploymentComponent.Backend];

    public static IReadOnlyList<DerivedEnvVar> Derive(DerivedEnvInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var composition = input.Composition;
        var list = new List<DerivedEnvVar>();
        if (composition is null)
        {
            return list;
        }

        var frontend = input.Targets.FirstOrDefault(t => t.Component == DeploymentComponent.Frontend);
        var backend = input.Targets.FirstOrDefault(t => t.Component == DeploymentComponent.Backend);
        if (composition.Frontend is not null && composition.Backend is not null)
        {
            var backendHost = DisplayHost(composition.Deploy.BackendHost, backend);
            var backendUrls = Urls(backend);
            var model = composition.OriginModel;
            var railwaySameOrigin = model == OriginModels.SameOrigin
                && composition.Deploy.FrontendHost == "deploy-railway" && composition.Deploy.BackendHost == "deploy-railway";
            if (railwaySameOrigin && RailwayPrivateOrigin(backend?.Name) is { } privateOrigin)
            {
                // FR-146, §4.22: the SPA container proxies /api over Railway's private network; Railway resolves the reference
                // in each environment (production, aide-preview), so one value serves both.
                list.Add(new DerivedEnvVar(
                    ApiOrigin,
                    FrontendOnly,
                    new Dictionary<DeploymentEnvironment, string> { [DeploymentEnvironment.Production] = privateOrigin, [DeploymentEnvironment.Preview] = privateOrigin },
                    Public: false,
                    Sensitive: false,
                    $"backend private network ({backendHost})"));
            }
            else if (model == OriginModels.Proxy || railwaySameOrigin)
            {
                list.Add(new DerivedEnvVar(ApiOrigin, FrontendOnly, backendUrls, Public: false, Sensitive: false, $"backend target URL ({backendHost})"));
            }

            if (model == OriginModels.CrossOrigin)
            {
                list.Add(new DerivedEnvVar(
                    $"{input.PublicPrefix}{ApiBaseUrl}", FrontendOnly, backendUrls, Public: true, Sensitive: false, $"backend target URL ({backendHost})"));
                list.Add(new DerivedEnvVar(
                    CorsAllowedOrigins, BackendOnly, CorsOrigins(frontend), Public: false, Sensitive: false,
                    $"frontend target URLs ({DisplayHost(composition.Deploy.FrontendHost, frontend)})"));
            }

            if (composition.Derived.NextjsMode == StackIds.Ui)
            {
                list.Add(new DerivedEnvVar(BackendUrl, FrontendOnly, backendUrls, Public: false, Sensitive: false, $"backend target URL ({backendHost})"));
            }
        }

        // M30 (FR-152, FR-153): DATABASE_URL, DIRECT_URL, AIDE_RUN_MIGRATIONS and the Supabase keys from input.Databases.
        return [.. list.OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    /// <summary>
    /// FR-146: the backend's origin on Railway's private network as a Railway reference variable,
    /// <c>http://${{&lt;service&gt;.RAILWAY_PRIVATE_DOMAIN}}:${{&lt;service&gt;.PORT}}</c> (AIDE sets <c>PORT</c> on a backend
    /// service it creates); <c>null</c> without a service name (the public URLs are used then).
    /// </summary>
    public static string? RailwayPrivateOrigin(string? backendServiceName) =>
        string.IsNullOrWhiteSpace(backendServiceName) ? null
        : $"http://${{{{{backendServiceName}.RAILWAY_PRIVATE_DOMAIN}}}}:${{{{{backendServiceName}.PORT}}}}";

    /// <summary>The keys AIDE derives for <paramref name="input"/> (an Owner can't set them, §4.22).</summary>
    public static IReadOnlySet<string> DerivedKeys(DerivedEnvInput input) =>
        Derive(input).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The preview URL pattern of a frontend host (for <c>CORS_ALLOWED_ORIGINS</c>): Vercel <c>https://&lt;name&gt;-*.vercel.app</c>,
    /// Netlify <c>https://*--&lt;name&gt;.netlify.app</c>, Cloudflare <c>https://*-&lt;name&gt;.*.workers.dev</c>; <c>null</c> where
    /// previews have one stable URL (Render, Railway) or don't exist (GitHub Pages), or the name isn't known.
    /// </summary>
    public static string? PreviewPattern(DeploymentProvider provider, string? name) => string.IsNullOrEmpty(name) ? null : provider switch
    {
        DeploymentProvider.Vercel => $"https://{name}-*.vercel.app",
        DeploymentProvider.Netlify => $"https://*--{name}.netlify.app",
        DeploymentProvider.Cloudflare => $"https://*-{name}.*.workers.dev",
        _ => null,
    };

    private static Dictionary<DeploymentEnvironment, string> Urls(DerivedEnvTarget? target)
    {
        var values = new Dictionary<DeploymentEnvironment, string>();
        if (Origin(target?.ProductionUrl) is { } production)
        {
            values[DeploymentEnvironment.Production] = production;
        }

        if (Origin(target?.PreviewUrl) is { } preview)
        {
            values[DeploymentEnvironment.Preview] = preview;
        }

        return values;
    }

    private static Dictionary<DeploymentEnvironment, string> CorsOrigins(DerivedEnvTarget? frontend)
    {
        var values = new Dictionary<DeploymentEnvironment, string>();
        if (frontend is null)
        {
            return values;
        }

        if (Origin(frontend.ProductionUrl) is { } production)
        {
            values[DeploymentEnvironment.Production] = production;
        }

        var preview = new[] { Origin(frontend.PreviewUrl), PreviewPattern(frontend.Provider, frontend.Name) }.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (preview.Count > 0)
        {
            values[DeploymentEnvironment.Preview] = string.Join(',', preview);
        }

        return values;
    }

    /// <summary>A URL's origin (<c>https://host[:port]</c>, no path or trailing slash); <c>null</c> when it isn't an absolute http(s) URL.</summary>
    public static string? Origin(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;

    private static string DisplayHost(string? hostOption, DerivedEnvTarget? target) =>
        target is not null ? DeploymentPlatforms.DisplayName(target.Provider)
        : DeploymentPlatforms.FromHostOption(hostOption) is { } provider ? DeploymentPlatforms.DisplayName(provider)
        : "not deployed";
}

/// <summary>A user variable as the inventory sees it (no value).</summary>
public sealed record EnvUserVar(
    string Key,
    IReadOnlyList<DeploymentComponent> AppliesTo,
    IReadOnlyList<DeploymentEnvironment> Environments,
    bool Sensitive,
    bool Public,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SyncedAt,
    string? SyncError);

/// <summary>The sync state of a stored derived variable.</summary>
public sealed record EnvSyncState(DateTimeOffset UpdatedAt, DateTimeOffset? SyncedAt, string? SyncError);

/// <summary>
/// One row of the FR-145 environment inventory: <see cref="Source"/> <c>user</c> (an Owner's, value encrypted),
/// <c>derived</c> (AIDE's, read-only, with <see cref="DerivedFrom"/>) or <c>required</c> (a pack's required variable that has
/// no value). <see cref="HasValue"/> is false for required rows and for derived rows whose source has no URL yet.
/// </summary>
public sealed record EnvInventoryRow(
    string Key,
    IReadOnlyList<DeploymentComponent> AppliesTo,
    IReadOnlyList<DeploymentEnvironment> Environments,
    bool Sensitive,
    bool Public,
    DeploymentEnvSource Source,
    string? DerivedFrom,
    string? Description,
    bool HasValue,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? SyncedAt,
    string? SyncError);

/// <summary>
/// FR-145, §4.18, §4.22: the environment inventory and the rules for user variables. Pure.
/// <list type="bullet">
/// <item>Rows: derived variables first claim their keys, then user variables, then the packs' required variables that have
/// neither (flagged <c>required</c>), ordered by key.</item>
/// <item>A user variable can't use a derived key, and a public one (by flag or by the frontend's public prefix) can't look
/// secret: its key matches <c>SECRET</c>, <c>SERVICE_ROLE</c>, <c>PRIVATE</c> or <c>PASSWORD</c>, or its value holds
/// <c>sb_secret_</c>, <c>sk_live</c> or a private key (§4.18 secret patterns).</item>
/// </list>
/// </summary>
public static partial class EnvInventoryRules
{
    public const string PublicSecretMessage = "secret values can't be public";

    private static readonly string[] PublicPrefixes = ["NEXT_PUBLIC_", "VITE_"];

    public static IReadOnlyList<EnvInventoryRow> Build(
        IReadOnlyList<DerivedEnvVar> derived,
        IReadOnlyDictionary<string, EnvSyncState> derivedSync,
        IReadOnlyList<EnvUserVar> users,
        IReadOnlyList<StackEnvVar> required)
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(derivedSync);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(required);
        var rows = new Dictionary<string, EnvInventoryRow>(StringComparer.Ordinal);
        foreach (var d in derived)
        {
            var sync = derivedSync.GetValueOrDefault(d.Key);
            rows[d.Key] = new EnvInventoryRow(
                d.Key, d.AppliesTo, [.. d.Values.Keys.Order()], d.Sensitive, d.Public, DeploymentEnvSource.Derived, d.DerivedFrom,
                Description: null, HasValue: d.Values.Count > 0, sync?.UpdatedAt, sync?.SyncedAt, sync?.SyncError);
        }

        foreach (var u in users.Where(u => !rows.ContainsKey(u.Key)))
        {
            rows[u.Key] = new EnvInventoryRow(
                u.Key, u.AppliesTo, u.Environments, u.Sensitive, u.Public, DeploymentEnvSource.User, null, null, HasValue: true, u.UpdatedAt, u.SyncedAt, u.SyncError);
        }

        foreach (var r in required.Where(r => !rows.ContainsKey(r.Key)))
        {
            rows[r.Key] = new EnvInventoryRow(
                r.Key,
                [.. r.AppliesTo.Select(a => EnumStrings.TryParse<DeploymentComponent>(a, out var c) ? (DeploymentComponent?)c : null).OfType<DeploymentComponent>().Distinct()],
                [.. r.Targets.Select(t => EnumStrings.TryParse<DeploymentEnvironment>(t, out var e) ? (DeploymentEnvironment?)e : null).OfType<DeploymentEnvironment>().Distinct().Order()],
                r.Sensitive,
                r.Public,
                DeploymentEnvSource.Required,
                null,
                r.Description,
                HasValue: false,
                null,
                null,
                null);
        }

        return [.. rows.Values.OrderBy(r => r.Key, StringComparer.Ordinal)];
    }

    /// <summary>The required keys without a value (the readiness checklist's "required env vars").</summary>
    public static IReadOnlyList<string> MissingRequired(IReadOnlyList<EnvInventoryRow> rows, DeploymentComponent component)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows.Where(r => !r.HasValue && (r.AppliesTo.Count == 0 || r.AppliesTo.Contains(component)) && r.Source != DeploymentEnvSource.User).Select(r => r.Key)];
    }

    /// <summary>Whether a key starts with a public prefix (<c>NEXT_PUBLIC_</c>, <c>VITE_</c>, or the frontend pack's).</summary>
    public static bool HasPublicPrefix(string key, string? publicPrefix)
    {
        ArgumentNullException.ThrowIfNull(key);
        return PublicPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal))
            || (!string.IsNullOrEmpty(publicPrefix) && key.StartsWith(publicPrefix, StringComparison.Ordinal));
    }

    /// <summary>§4.18 secret patterns: the key names a secret, or the value holds a secret key.</summary>
    public static bool LooksSecret(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        return SecretKeyPattern().IsMatch(key) || (value is not null && SecretValuePattern().IsMatch(value));
    }

    /// <summary>
    /// FR-145: a user variable: the v2 checks (<see cref="DeploymentEnvRules.Validate"/>), components applied to (at least one,
    /// each once), not a derived key, and not public while looking secret. <paramref name="isPublic"/> is the effective flag
    /// (<see cref="IsPublic"/>).
    /// </summary>
    public static Result ValidateUser(
        string? key,
        string? value,
        IReadOnlyCollection<DeploymentEnvironment>? targets,
        IReadOnlyCollection<DeploymentComponent>? appliesTo,
        bool isPublic,
        IReadOnlySet<string> derivedKeys)
    {
        ArgumentNullException.ThrowIfNull(derivedKeys);
        var problems = new List<string>();
        var basic = DeploymentEnvRules.Validate(key, value, targets);
        if (basic.IsFailure)
        {
            problems.AddRange(basic.Error!.Details);
        }

        if (appliesTo is not null && (appliesTo.Count == 0 || appliesTo.Distinct().Count() != appliesTo.Count))
        {
            problems.Add("appliesTo: Choose frontend, backend or both, each once.");
        }

        if (key is not null && derivedKeys.Contains(key))
        {
            problems.Add($"key: AIDE derives {key} from the deployment targets; it can't be set by hand.");
        }

        if (isPublic && key is not null && LooksSecret(key, value))
        {
            problems.Add($"public: {char.ToUpperInvariant(PublicSecretMessage[0])}{PublicSecretMessage[1..]}.");
        }

        if (isPublic && appliesTo is not null && !appliesTo.Contains(DeploymentComponent.Frontend))
        {
            problems.Add("appliesTo: A public variable reaches the browser, so it applies to the frontend.");
        }

        return ProjectRulesBridge.ToResult(problems, isPublic && key is not null && LooksSecret(key, value)
            ? $"The environment variable is invalid: {PublicSecretMessage}."
            : "The environment variable is invalid.");
    }

    /// <summary>The effective public flag: as sent, or (not sent) whether the key has a public prefix. A prefixed key is always public.</summary>
    public static bool IsPublic(string key, bool? requested, string? publicPrefix) =>
        HasPublicPrefix(key, publicPrefix) || (requested ?? false);

    [GeneratedRegex(@"SECRET|SERVICE_?ROLE|PRIVATE|PASSWORD", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretKeyPattern();

    [GeneratedRegex(@"sb_secret_|sk_live|PRIVATE KEY-----|service_role", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretValuePattern();
}

/// <summary>Turns collected <c>field: message</c> problems into a <c>VALIDATION</c> result (as <c>ProjectRules</c>).</summary>
internal static class ProjectRulesBridge
{
    public static Result ToResult(List<string> problems, string title) => Projects.ProjectRules.ToResult(problems, title);
}

/// <summary>
/// §4.22 storage of a derived variable's per-environment values in <c>deployment_env_vars.value_encrypted</c> (one row per
/// key, unique (project, key)): a small JSON object <c>{"preview": …, "production": …}</c>, encrypted like any value. Pure.
/// </summary>
public static class DerivedEnvValues
{
    public static string Serialize(IReadOnlyDictionary<DeploymentEnvironment, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (environment, value) in values.OrderBy(v => v.Key))
        {
            sb.Append(first ? string.Empty : ",").Append(System.Text.Json.JsonSerializer.Serialize(environment.ToWire())).Append(':').Append(System.Text.Json.JsonSerializer.Serialize(value));
            first = false;
        }

        return sb.Append('}').ToString();
    }

    public static IReadOnlyDictionary<DeploymentEnvironment, string> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var values = new Dictionary<DeploymentEnvironment, string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return values;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (EnumStrings.TryParse<DeploymentEnvironment>(property.Name, out var environment) && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    values[environment] = property.Value.GetString()!;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return values;
    }
}
