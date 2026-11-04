using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Entities;
using Aide.Domain.Stack;

namespace Aide.Domain.Deployments;

/// <summary>A deployable part of a composition (§4.28). Wire values: <c>frontend</c>, <c>backend</c>, <c>database</c>.</summary>
public enum DeploymentPart
{
    Frontend = 0,
    Backend = 1,
    Database = 2,
}

/// <summary>
/// FR-192, §4.28: what a platform shares between a group's parts. Wire values: <c>none</c>, <c>project</c> (one real
/// project), <c>shared_setup</c> (one connection and name, one platform project per part: Vercel, D-V5-8).
/// </summary>
public enum SharedProjectCapability
{
    None = 0,
    Project = 1,
    SharedSetup = 2,
}

/// <summary>§4.28: a group: parts on one platform set up together, with the platform's capability.</summary>
public sealed record DeploymentGroupSpec(DeploymentProvider Provider, IReadOnlyList<DeploymentPart> Parts, SharedProjectCapability Capability);

/// <summary>
/// §4.28, FR-188: one window of the Deployment step: a group (several parts), or one part of a host. <see cref="Platform"/>
/// is the host's wire name (<c>railway</c>, <c>netlify</c>, <c>supabase</c>, <c>external</c> …). <see cref="Environment"/>
/// is set where a database needs one window per environment (Supabase, External).
/// </summary>
public sealed record DeploymentWindow(string Platform, IReadOnlyList<DeploymentPart> Parts, SharedProjectCapability Capability, DeploymentEnvironment? Environment = null);

/// <summary>R13: what <see cref="DeploymentGrouping.NormaliseSeparate"/> kept and removed, as wire values in part order.</summary>
public sealed record SeparatePartsNormalised(IReadOnlyList<string> Kept, IReadOnlyList<string> Removed);

/// <summary>
/// §4.28, D-V5-8, FR-187: which parts of a composition form deployment groups. Pure. Frontend and backend hosts map to
/// their platforms, <c>render-postgres</c> and <c>railway-postgres</c> to theirs. Parts on one platform, minus
/// <c>deploy.separateParts</c>, form a group of at least two. Supabase and External never group (FR-152: production and
/// preview stay separate), and Netlify, Cloudflare and GitHub Pages only host frontends, so they never group either.
/// </summary>
public static class DeploymentGrouping
{
    public const string FrontendPart = "frontend";
    public const string BackendPart = "backend";
    public const string DatabasePart = "database";

    private const string RenderPostgres = "render-postgres";
    private const string RailwayPostgres = "railway-postgres";

    /// <summary>The wire values of <see cref="DeploymentPart"/>, in part order.</summary>
    public static IReadOnlyList<string> PartNames { get; } = [FrontendPart, BackendPart, DatabasePart];

    /// <summary>§4.28 <c>SharedProject</c> capability of a platform. Fake behaves as Railway.</summary>
    public static SharedProjectCapability Capability(DeploymentProvider provider) => provider switch
    {
        DeploymentProvider.Railway or DeploymentProvider.Render or DeploymentProvider.Fake => SharedProjectCapability.Project,
        DeploymentProvider.Vercel => SharedProjectCapability.SharedSetup,
        _ => SharedProjectCapability.None,
    };

    /// <summary>The platform of a database host option that can share a platform; <c>null</c> for Supabase, External and None.</summary>
    public static DeploymentProvider? PlatformOfDatabaseHost(string? databaseHost) => databaseHost switch
    {
        RenderPostgres => DeploymentProvider.Render,
        RailwayPostgres => DeploymentProvider.Railway,
        _ => null,
    };

    /// <summary>§4.28 <c>Groups(composition)</c>: the groups in order of their first part. <c>null</c> has none.</summary>
    public static IReadOnlyList<DeploymentGroupSpec> Groups(StackComposition? composition) =>
        composition is null ? [] : Groups(
            composition.Frontend is null ? null : composition.Deploy.FrontendHost,
            composition.Backend is null ? null : composition.Deploy.BackendHost,
            composition.Data is null ? null : composition.Deploy.DatabaseHost,
            composition.Deploy.SeparateParts);

    /// <summary>The groups of host option ids (<c>null</c> or <c>none</c>: no host) and the separate parts (normalised by R13 first).</summary>
    public static IReadOnlyList<DeploymentGroupSpec> Groups(string? frontendHost, string? backendHost, string? databaseHost, IReadOnlyList<string>? separateParts)
    {
        var parts = PartsOf(frontendHost, backendHost, databaseHost);
        var separate = NormaliseSeparate(frontendHost, backendHost, databaseHost, separateParts).Kept;
        return
        [
            .. parts
                .Where(p => !separate.Contains(WireName(p.Part), StringComparer.Ordinal))
                .GroupBy(p => p.Platform)
                .Where(g => g.Count() >= 2)
                .Select(g => new DeploymentGroupSpec(g.Key, [.. g.Select(p => p.Part)], Capability(g.Key))),
        ];
    }

    /// <summary>
    /// R13: a part in <c>separateParts</c> must be on a platform it shares with another part (whether or not that one is
    /// also separate). Anything else is removed. Unknown names are removed too (<see cref="StackCompatibility"/> refuses them first).
    /// </summary>
    public static SeparatePartsNormalised NormaliseSeparate(string? frontendHost, string? backendHost, string? databaseHost, IReadOnlyList<string>? separateParts)
    {
        if (separateParts is not { Count: > 0 })
        {
            return new SeparatePartsNormalised([], []);
        }

        var parts = PartsOf(frontendHost, backendHost, databaseHost);
        var kept = new List<string>();
        foreach (var name in PartNames.Where(n => separateParts.Contains(n, StringComparer.Ordinal)))
        {
            var match = parts.Where(p => WireName(p.Part) == name).ToList();
            if (match.Count == 1 && parts.Count(p => p.Platform == match[0].Platform) >= 2)
            {
                kept.Add(name);
            }
        }

        return new SeparatePartsNormalised(kept, [.. separateParts.Distinct(StringComparer.Ordinal).Except(kept, StringComparer.Ordinal)]);
    }

    /// <summary>§4.28, FR-188: the Deployment step's windows: one per group, then one per ungrouped part (a lone database: one per environment, except Railway's).</summary>
    public static IReadOnlyList<DeploymentWindow> Windows(StackComposition? composition) =>
        composition is null ? [] : Windows(
            composition.Frontend is null ? null : composition.Deploy.FrontendHost,
            composition.Backend is null ? null : composition.Deploy.BackendHost,
            composition.Data is null ? null : composition.Deploy.DatabaseHost,
            composition.Deploy.SeparateParts);

    /// <summary>The windows of host option ids and the separate parts.</summary>
    public static IReadOnlyList<DeploymentWindow> Windows(string? frontendHost, string? backendHost, string? databaseHost, IReadOnlyList<string>? separateParts)
    {
        var groups = Groups(frontendHost, backendHost, databaseHost, separateParts);
        var grouped = groups.SelectMany(g => g.Parts).ToHashSet();
        var windows = groups.Select(g => new DeploymentWindow(g.Provider.ToWire(), g.Parts, g.Capability)).ToList();
        foreach (var (part, host) in new[] { (DeploymentPart.Frontend, frontendHost), (DeploymentPart.Backend, backendHost) })
        {
            if (!grouped.Contains(part) && DeploymentPlatforms.FromHostOption(host) is { } provider)
            {
                windows.Add(new DeploymentWindow(provider.ToWire(), [part], SharedProjectCapability.None));
            }
        }

        if (grouped.Contains(DeploymentPart.Database))
        {
            return windows;
        }

        // Railway's aide-preview environment holds both in one service: one window. Elsewhere each environment is its own resource.
        if (PlatformOfDatabaseHost(databaseHost) == DeploymentProvider.Railway)
        {
            windows.Add(new DeploymentWindow(DeploymentProvider.Railway.ToWire(), [DeploymentPart.Database], SharedProjectCapability.None));
        }
        else if (ManagedDatabaseRules.ProviderForHost(databaseHost) is { } other)
        {
            windows.Add(new DeploymentWindow(other.ToWire(), [DeploymentPart.Database], SharedProjectCapability.None, DeploymentEnvironment.Production));
            windows.Add(new DeploymentWindow(other.ToWire(), [DeploymentPart.Database], SharedProjectCapability.None, DeploymentEnvironment.Preview));
        }

        return windows;
    }

    /// <summary>The group of <paramref name="provider"/> in <paramref name="composition"/>, or <c>null</c>.</summary>
    public static DeploymentGroupSpec? GroupOf(StackComposition? composition, DeploymentProvider provider) =>
        Groups(composition).FirstOrDefault(g => g.Provider == provider);

    /// <summary>
    /// §4.28 (amends §4.21): a Railway database with no Railway part to share a project with: it is separate, or nothing
    /// else is on Railway. It gets its own project and a public URL through a TCP proxy.
    /// </summary>
    public static bool RailwayDatabaseStandsAlone(StackComposition? composition) =>
        PlatformOfDatabaseHost(composition?.Deploy.DatabaseHost) == DeploymentProvider.Railway
        && !Groups(composition).Any(g => g.Provider == DeploymentProvider.Railway && g.Parts.Contains(DeploymentPart.Database));

    /// <summary>
    /// §4.28, R8: whether the app using a Railway database (the backend, else a Next.js full-stack app) is in one Railway
    /// group with it, so <c>${{Postgres.DATABASE_URL}}</c> resolves. Otherwise the app needs the public URL.
    /// </summary>
    public static bool RailwayDatabaseSharesProjectWithApp(StackComposition? composition)
    {
        if (composition is null || GroupOf(composition, DeploymentProvider.Railway) is not { } group || !group.Parts.Contains(DeploymentPart.Database))
        {
            return false;
        }

        return composition.Backend is not null
            ? group.Parts.Contains(DeploymentPart.Backend)
            : composition.Derived.NextjsMode == StackIds.FullStack && group.Parts.Contains(DeploymentPart.Frontend);
    }

    public static string WireName(DeploymentPart part) => part switch
    {
        DeploymentPart.Frontend => FrontendPart,
        DeploymentPart.Backend => BackendPart,
        _ => DatabasePart,
    };

    private static List<(DeploymentPart Part, DeploymentProvider Platform)> PartsOf(string? frontendHost, string? backendHost, string? databaseHost)
    {
        var parts = new List<(DeploymentPart, DeploymentProvider)>();
        void Add(DeploymentPart part, DeploymentProvider? platform)
        {
            if (platform is { } p && Capability(p) != SharedProjectCapability.None)
            {
                parts.Add((part, p));
            }
        }

        Add(DeploymentPart.Frontend, DeploymentPlatforms.FromHostOption(frontendHost));
        Add(DeploymentPart.Backend, DeploymentPlatforms.FromHostOption(backendHost));
        Add(DeploymentPart.Database, PlatformOfDatabaseHost(databaseHost));
        return parts;
    }
}

/// <summary>The state of one part as a group's status sees it.</summary>
public enum DeploymentGroupPartState
{
    Configuring = 0,
    Ready = 1,
    Attention = 2,
    Error = 3,
}

/// <summary>What <see cref="DeploymentGroupRules.Validate"/> needs from a <c>PUT …/groups/:provider</c>.</summary>
public sealed record DeploymentGroupPut(
    DeploymentProvider Provider,
    StackComposition? Composition,
    GitProvider GitProvider,
    bool FakeMode,
    Guid? ConnectionId,
    bool Create,
    string? CreateName,
    bool Link,
    string? LinkResourceId,
    string? LinkFrontend,
    string? LinkBackend,
    string? LinkDatabase,
    bool HasGroup,
    IReadOnlyList<DeploymentPart> PartsSetUpAlone);

/// <summary>
/// §4.28, FR-187–FR-192: the rules of deployment groups: the PUT's checks that need no platform call, part resource names,
/// who may join a group, the group's status from its parts, and the shared project's link. Pure.
/// </summary>
public static class DeploymentGroupRules
{
    /// <summary>The longest group name: part suffixes and <c>-preview</c> must still fit <see cref="DeploymentTargetSetupRules.NameMaxLength"/>.</summary>
    public const int NameMaxLength = DeploymentTargetSetupRules.NameMaxLength - 12;

    /// <summary>FR-192: the group's status reason when the platform can't place resources in the project (§11 Q5): the group still works.</summary>
    public const string PlacementUnavailable = "project_placement_unavailable";

    /// <summary>The <c>DEPLOYMENT_GROUP_INVALID</c> error, with the copy's <c>{reason}</c> as <c>reason</c>.</summary>
    public static DomainError Invalid(DeploymentProvider provider, string reason) =>
        new(ErrorCodes.DeploymentGroupInvalid, $"These parts can't share a {DeploymentPlatforms.DisplayName(provider)} project: {reason}.") { Reason = reason };

    /// <summary>The composition's group for the route's provider: Fake mode's <c>fake</c> takes the first group.</summary>
    public static DeploymentGroupSpec? Spec(StackComposition? composition, DeploymentProvider provider, bool fakeMode) =>
        fakeMode && provider == DeploymentProvider.Fake
            ? DeploymentGrouping.Groups(composition) is { Count: > 0 } groups ? groups[0] with { Provider = DeploymentProvider.Fake, Capability = SharedProjectCapability.Project } : null
            : DeploymentGrouping.GroupOf(composition, provider);

    /// <summary>
    /// FR-187, FR-190 PUT checks: the composition's platform (<c>STACK_INCOMPATIBLE</c>), a real group
    /// (<c>DEPLOYMENT_GROUP_INVALID</c>), parts already set up alone are never moved (D-V5-9), then the body (<c>VALIDATION</c>).
    /// </summary>
    public static Result Validate(DeploymentGroupPut put)
    {
        ArgumentNullException.ThrowIfNull(put);
        var provider = put.Provider;
        var spec = Spec(put.Composition, provider, put.FakeMode);
        if (spec is null)
        {
            var usesPlatform = put.Composition is { } c && provider != DeploymentProvider.Fake
                && (DeploymentPlatforms.ProviderOf(c, DeploymentComponent.Frontend) == provider
                    || DeploymentPlatforms.ProviderOf(c, DeploymentComponent.Backend) == provider
                    || DeploymentGrouping.PlatformOfDatabaseHost(c.Deploy.DatabaseHost) == provider);
            return usesPlatform
                ? Result.Failure(Invalid(provider, "a group needs at least two parts on the platform"))
                : Result.Failure(StackCompatibility.IncompatibleError(
                    [new StackViolation(StackChoices.BackendHost, provider.ToWire(), "FR-187", $"The stack deploys nothing to {DeploymentPlatforms.DisplayName(provider)}.")]));
        }

        if (put.PartsSetUpAlone is { Count: > 0 } alone && (put.Create || put.Link))
        {
            return Result.Failure(Invalid(
                provider, $"the {string.Join(" and ", alone.Select(DeploymentGrouping.WireName))} {(alone.Count == 1 ? "is" : "are")} already set up on their own; remove them first, because Tevrin doesn't move platform resources"));
        }

        if (put.Link)
        {
            var named = new (DeploymentPart Part, string? Id)[] { (DeploymentPart.Frontend, put.LinkFrontend), (DeploymentPart.Backend, put.LinkBackend), (DeploymentPart.Database, put.LinkDatabase) };
            if (named.FirstOrDefault(n => n.Id is not null && !spec.Parts.Contains(n.Part)) is { Id: not null } stray)
            {
                return Result.Failure(Invalid(provider, $"the {DeploymentGrouping.WireName(stray.Part)} isn't in this group"));
            }
        }

        var problems = new List<string>();
        if (put.GitProvider == GitProvider.Local && !put.FakeMode)
        {
            problems.Add("provider: Deployment needs a hosted Git repository; this project uses Local Git.");
        }

        if (put.ConnectionId is null)
        {
            problems.Add($"connectionId: Choose one of your {DeploymentPlatforms.DisplayName(provider)} connections.");
        }

        if (put.Create && put.Link)
        {
            problems.Add("create: Create a project or link one, not both.");
        }
        else if (!put.Create && !put.Link && !put.HasGroup)
        {
            problems.Add("create: Create a project or link an existing one.");
        }

        if (put.Create && (!DeploymentTargetSetupRules.IsName(put.CreateName) || put.CreateName!.Length > NameMaxLength))
        {
            problems.Add($"create.name: Up to {NameMaxLength} lowercase letters, digits, '.', '_' or '-', without '---'.");
        }

        if (put.Link && spec.Capability == SharedProjectCapability.Project && !DeploymentTargetSetupRules.IsResourceId(put.LinkResourceId))
        {
            problems.Add("link.resourceId: Choose a project.");
        }

        if (put.Link && spec.Capability == SharedProjectCapability.SharedSetup)
        {
            // Vercel has no shared resource: every part names its own project.
            problems.AddRange(spec.Parts
                .Where(p => (p == DeploymentPart.Frontend ? put.LinkFrontend : put.LinkBackend) is null)
                .Select(p => $"link.parts.{DeploymentGrouping.WireName(p)}: Choose a project."));
        }

        problems.AddRange(new[] { ("frontend", put.LinkFrontend), ("backend", put.LinkBackend), ("database", put.LinkDatabase) }
            .Where(p => p.Item2 is not null && !DeploymentTargetSetupRules.IsResourceId(p.Item2))
            .Select(p => $"link.parts.{p.Item1}: Not a resource id."));
        return Projects.ProjectRules.ToResult(problems, "The deployment group is invalid.");
    }

    /// <summary>
    /// FR-191: a part set up again in a group (<c>groupId</c>): the group is the project's, on the part's platform, the
    /// composition keeps the part in it, and a real project already exists. <c>DEPLOYMENT_GROUP_INVALID</c> otherwise.
    /// </summary>
    public static Result CanJoin(StackComposition? composition, DeploymentPart part, DeploymentProvider partProvider, DeploymentGroup? group, bool fakeMode)
    {
        if (group is null)
        {
            return Result.Failure(Invalid(partProvider, "the group doesn't exist in this project"));
        }

        if (group.Provider != partProvider)
        {
            return Result.Failure(Invalid(partProvider, $"the group is on {DeploymentPlatforms.DisplayName(group.Provider)}"));
        }

        if (Spec(composition, group.Provider, fakeMode) is not { } spec || !spec.Parts.Contains(part))
        {
            return Result.Failure(Invalid(partProvider, $"the stack keeps the {DeploymentGrouping.WireName(part)} out of this group"));
        }

        return spec.Capability == SharedProjectCapability.Project && group.SharedResourceId is null
            ? Result.Failure(Invalid(partProvider, "the group's project doesn't exist yet"))
            : Result.Success();
    }

    /// <summary>
    /// FR-192, §4.28: a part's resource name in a group: <c>&lt;name&gt;-web</c>, <c>&lt;name&gt;-api</c>, and the
    /// database's <c>&lt;name&gt;-db</c> (Railway's service is always <see cref="ManagedDatabaseRules.RailwayServiceName"/>).
    /// </summary>
    public static string PartName(string groupName, DeploymentPart part) => part switch
    {
        DeploymentPart.Frontend => $"{groupName}-web",
        DeploymentPart.Backend => $"{groupName}-api",
        _ => $"{groupName}-db",
    };

    /// <summary>FR-190: the group's status: any part configuring, else any error, else any attention, else ready. No parts: attention.</summary>
    public static DeploymentGroupStatus StatusOf(IReadOnlyCollection<DeploymentGroupPartState> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return parts.Count == 0 ? DeploymentGroupStatus.Attention
            : parts.Contains(DeploymentGroupPartState.Configuring) ? DeploymentGroupStatus.Configuring
            : parts.Contains(DeploymentGroupPartState.Error) ? DeploymentGroupStatus.Error
            : parts.Contains(DeploymentGroupPartState.Attention) ? DeploymentGroupStatus.Attention
            : DeploymentGroupStatus.Ready;
    }

    public static DeploymentGroupPartState StateOf(DeploymentTargetStatus status) => status switch
    {
        DeploymentTargetStatus.Configuring => DeploymentGroupPartState.Configuring,
        DeploymentTargetStatus.Ready => DeploymentGroupPartState.Ready,
        DeploymentTargetStatus.Error => DeploymentGroupPartState.Error,
        _ => DeploymentGroupPartState.Attention,
    };

    public static DeploymentGroupPartState StateOf(ManagedDatabaseStatus status) => status switch
    {
        ManagedDatabaseStatus.Provisioning => DeploymentGroupPartState.Configuring,
        ManagedDatabaseStatus.Available => DeploymentGroupPartState.Ready,
        ManagedDatabaseStatus.Error => DeploymentGroupPartState.Error,
        _ => DeploymentGroupPartState.Attention,
    };

    /// <summary>FR-190 Retry: a part retries when it failed or is missing; ready ones are never touched.</summary>
    public static bool Retries(DeploymentTargetStatus? status) => status is null or DeploymentTargetStatus.Error or DeploymentTargetStatus.NotConfigured;

    /// <inheritdoc cref="Retries(DeploymentTargetStatus?)"/>
    public static bool Retries(ManagedDatabaseStatus? status) => status is null or ManagedDatabaseStatus.Error or ManagedDatabaseStatus.Removed;

    /// <summary>FR-191: the shared project's dashboard link; <c>null</c> without a shared resource.</summary>
    public static string? SharedProjectUrl(DeploymentProvider provider, string? sharedResourceId) => sharedResourceId is null ? null : provider switch
    {
        DeploymentProvider.Railway => $"https://railway.com/project/{Uri.EscapeDataString(sharedResourceId)}",
        DeploymentProvider.Render => $"https://dashboard.render.com/project/{Uri.EscapeDataString(sharedResourceId)}",
        _ => null,
    };

    /// <summary>§4.28: a Railway part in a shared project is linked as <c>project:service</c> (a part may name just the service).</summary>
    public static string LinkId(DeploymentProvider provider, string? sharedResourceId, string partResourceId)
    {
        ArgumentNullException.ThrowIfNull(partResourceId);
        return provider == DeploymentProvider.Railway && sharedResourceId is not null && RailwayRules.ParseResourceId(partResourceId) is null
            ? $"{sharedResourceId}:{partResourceId}"
            : partResourceId;
    }
}
