using System.Text.Json;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;

namespace Aide.Domain.Stack;

/// <summary>Why a release version's stack can't change (FR-117). Wire values: <c>plan_locked</c>, <c>not_in_development</c>.</summary>
public enum StackLockReason
{
    /// <summary>The version's first implementation plan was locked: start a new version to change the stack.</summary>
    PlanLocked = 0,

    /// <summary>The version isn't in development (released, awaiting merge or imported): start a new version.</summary>
    NotInDevelopment = 1,
}

/// <summary>FR-117: whether a version's stack is locked, and why.</summary>
public sealed record StackLock(bool Locked, StackLockReason? Reason)
{
    public static StackLock Unlocked { get; } = new(false, null);
}

/// <summary>
/// FR-117: the layers that differ between two compositions, and whether that is a <b>stack change</b>: layers changed on a
/// release version whose base commit already has code (an iteration or an import). The spec and plan generators read
/// <see cref="IsStackChange"/> (FR-123, FR-124: the "Stack Migration" section and a step for each changed layer's specialist).
/// </summary>
public sealed record StackChange(bool BaseHasCode, IReadOnlyList<string> LayersChanged)
{
    public bool IsStackChange => BaseHasCode && LayersChanged.Count > 0;

    public static StackChange None { get; } = new(false, []);
}

/// <summary>
/// FR-117, FR-119, §4.17 R5: the pure rules for pinning a composition on a release version and changing it.
/// </summary>
public static class StackPinning
{
    /// <summary>The VCS option of a GitHub repository (the only remote host before GitLab and Bitbucket).</summary>
    public const string GitHubVcs = "vcs-github";

    /// <summary>FR-136, §4.17 R5: the VCS option of a GitLab repository (gitlab.com or a self-managed instance).</summary>
    public const string GitLabVcs = "vcs-gitlab";

    /// <summary>FR-135, FR-136, §4.17 R5: the VCS option of a Bitbucket Cloud repository.</summary>
    public const string BitbucketVcs = "vcs-bitbucket";

    /// <summary>
    /// FR-117: the version's lock. Only a version in development whose first implementation plan isn't locked yet may
    /// change its stack. <paramref name="planWasLocked"/> is <see cref="PlanWasLocked"/> over the version's plans.
    /// </summary>
    public static StackLock Lock(ReleaseVersionStatus? status, bool planWasLocked) =>
        status != ReleaseVersionStatus.InDevelopment
            ? new StackLock(true, StackLockReason.NotInDevelopment)
            : planWasLocked ? new StackLock(true, StackLockReason.PlanLocked) : StackLock.Unlocked;

    /// <summary>FR-117: <c>STACK_LOCKED</c> unless the version's stack may change (<see cref="Lock"/>).</summary>
    public static Result CanChange(ReleaseVersionStatus? status, bool planWasLocked) =>
        Lock(status, planWasLocked) switch
        {
            { Reason: StackLockReason.NotInDevelopment } => Result.Failure(new DomainError(
                ErrorCodes.StackLocked, "No version is in development. Change the stack when you start the next version.")),
            { Reason: StackLockReason.PlanLocked } => Result.Failure(new DomainError(
                ErrorCodes.StackLocked, "The stack can't change after the implementation plan is locked. Start a new version to change it.")),
            _ => Result.Success(),
        };

    /// <summary>
    /// FR-117: whether a version's first implementation plan has been locked. A plan that was approved and later
    /// invalidated (or found corrupted) still counts: the stack shaped it. <paramref name="plans"/> are the version's
    /// <c>implementation_plan</c> artifacts (status and approval time).
    /// </summary>
    public static bool PlanWasLocked(IEnumerable<(ArtifactStatus Status, DateTimeOffset? ApprovedAt)> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        return plans.Any(p => p.Status == ArtifactStatus.Locked || p.ApprovedAt is not null);
    }

    /// <summary>
    /// §4.17 R5: the VCS option of the project's repository host, or <c>null</c> when the repository has no host (Local):
    /// then any VCS may be chosen.
    /// </summary>
    public static string? RepoVcs(GitProvider provider) => provider switch
    {
        GitProvider.GithubApp or GitProvider.GithubPat => GitHubVcs,
        GitProvider.Gitlab => GitLabVcs,
        GitProvider.Bitbucket => BitbucketVcs,
        _ => null,
    };

    /// <summary>
    /// FR-136, §4.17 R5: the VCS pick of the project's repository host, with the GitLab instance and whether it is
    /// self-managed (§4.17 restricts the hosts a self-managed GitLab can deploy to); <c>null</c> for a Local repository.
    /// </summary>
    public static StackVcsPick? RepoHost(GitProvider provider, Guid? instanceId, bool selfManaged) =>
        RepoVcs(provider) is not { } pack ? null
        : provider == GitProvider.Gitlab ? new StackVcsPick(pack, instanceId, selfManaged)
        : new StackVcsPick(pack);

    /// <summary>
    /// FR-136, §4.17 R5: as <see cref="FixVcs(StackSelection, string?)"/>, and the VCS layer takes the repository host's
    /// instance and self-managed flag (never the client's), so a self-managed GitLab's host rules apply.
    /// </summary>
    public static Result<StackSelection> FixVcs(StackSelection selection, StackVcsPick? repoHost)
    {
        var fixedVcs = FixVcs(selection, repoHost?.Pack);
        return fixedVcs.IsSuccess && repoHost is not null
            ? Result.Success(fixedVcs.Value with { Vcs = repoHost })
            : fixedVcs;
    }

    /// <summary>
    /// §4.17 R5: the selection with its VCS layer fixed to the repository's host. A left-out VCS is set from the repo (or,
    /// for a repository without a host, to <see cref="GitHubVcs"/>); a different one is <c>VCS_MISMATCH</c>.
    /// </summary>
    public static Result<StackSelection> FixVcs(StackSelection selection, string? repoVcs)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Vcs is null)
        {
            return Result.Success(selection with { Vcs = new StackVcsPick(repoVcs ?? GitHubVcs) });
        }

        if (repoVcs is not null && selection.Vcs.Pack != repoVcs)
        {
            return Result.Failure<StackSelection>(new DomainError(
                ErrorCodes.VcsMismatch, "The version control choice must match the project's repository host.")
            {
                Details = [$"vcs.pack: must be '{repoVcs}' (the repository's host), not '{selection.Vcs.Pack}'."],
            });
        }

        return Result.Success(selection);
    }

    /// <summary>
    /// The selection that re-validates <paramref name="composition"/> (FR-117 "re-pinned to the current catalog"): every
    /// layer explicit, None as <see cref="StackIds.None"/>, so no implicit default changes it.
    /// </summary>
    public static StackSelection ToSelection(StackComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        return new StackSelection
        {
            Frontend = new StackComponentPick(composition.Frontend?.Pack ?? StackIds.None, composition.Frontend?.Path),
            Backend = new StackComponentPick(composition.Backend?.Pack ?? StackIds.None, composition.Backend?.Path),
            Data = composition.Data is { } data ? new StackDataPick(data.Pack, data.Mode) : new StackDataPick(StackIds.None),
            Vcs = new StackVcsPick(composition.Vcs.Pack, composition.Vcs.InstanceId, composition.Vcs.SelfManaged),
            Deploy = new StackDeployPick(
                composition.Deploy.FrontendHost ?? StackIds.None,
                composition.Deploy.BackendHost ?? StackIds.None,
                composition.Deploy.DatabaseHost ?? StackIds.None,
                composition.Deploy.Local),
            OriginModel = composition.OriginModel,
        };
    }

    /// <summary>
    /// §5 <c>project.stack_packs</c>: the pack ids of a composition, in choice order, without duplicates (a host option's
    /// pack, e.g. <c>deploy-vercel</c>; the Compose dev loop's <c>local-docker-desktop</c>). Options without a pack (None,
    /// External) are left out. Empty for no composition.
    /// </summary>
    public static IReadOnlyList<string> PackIds(StackComposition? composition, StackCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (composition is null)
        {
            return [];
        }

        var packs = new List<string>();
        void Add(string choice, string? option)
        {
            if (option is null || option == StackIds.None)
            {
                return;
            }

            var pack = catalog.Choices.FirstOrDefault(c => c.Id == choice)?.Find(option)?.Pack;
            if (pack is not null && !packs.Contains(pack, StringComparer.Ordinal))
            {
                packs.Add(pack);
            }
        }

        Add(StackChoices.Frontend, composition.Frontend?.Pack);
        Add(StackChoices.Backend, composition.Backend?.Pack);
        Add(StackChoices.Data, composition.Data?.Pack);
        Add(StackChoices.Vcs, composition.Vcs.Pack);
        Add(StackChoices.FrontendHost, composition.Deploy.FrontendHost);
        Add(StackChoices.BackendHost, composition.Deploy.BackendHost);
        Add(StackChoices.DatabaseHost, composition.Deploy.DatabaseHost);
        Add(StackChoices.Local, composition.Deploy.Local);
        return packs;
    }

    /// <summary>
    /// FR-117 <c>STACK_CHANGED.layersChanged</c>: the catalog layers (<see cref="StackLayers.CatalogLayers"/> order) whose
    /// choices differ — the pack or path of the frontend and backend, the data pack or mode, the VCS (pack, instance), and the
    /// Deployment layer's hosts, dev loop and origin model. No composition (no stack, or a legacy preset) counts as every
    /// layer None. The catalog version and hash and the derived fields are not choices.
    /// </summary>
    public static IReadOnlyList<string> LayersChanged(StackComposition? from, StackComposition? to)
    {
        static string? Component(StackComponent? c) => c is null ? null : $"{c.Pack}@{c.Path}";
        static string? Data(StackComposition? c) => c?.Data is { } d ? $"{d.Pack}:{d.Mode}" : null;
        static string? Vcs(StackComposition? c) => c is null ? null : $"{c.Vcs.Pack}:{c.Vcs.InstanceId}:{c.Vcs.SelfManaged}";
        static string? Deploy(StackComposition? c) =>
            c is null ? null : $"{c.Deploy.FrontendHost}|{c.Deploy.BackendHost}|{c.Deploy.DatabaseHost}|{c.Deploy.Local}|{c.OriginModel}";

        var changed = new List<string>();
        if (Component(from?.Frontend) != Component(to?.Frontend))
        {
            changed.Add(StackLayers.Frontend);
        }

        if (Component(from?.Backend) != Component(to?.Backend))
        {
            changed.Add(StackLayers.Backend);
        }

        if (Data(from) != Data(to))
        {
            changed.Add(StackLayers.Data);
        }

        if (Vcs(from) != Vcs(to))
        {
            changed.Add(StackLayers.Vcs);
        }

        if (Deploy(from) != Deploy(to))
        {
            changed.Add(StackLayers.Deploy);
        }

        return changed;
    }

    /// <summary>
    /// FR-117: the stack change of a version from <paramref name="baseline"/> (the stack its base code was built with) to
    /// <paramref name="current"/>. <paramref name="baseHasCode"/>: the version builds on a commit with code (an iteration
    /// or an import). Without a baseline composition (a legacy preset, or no stack) nothing counts as changed.
    /// </summary>
    public static StackChange Change(bool baseHasCode, StackComposition? baseline, StackComposition? current) =>
        baseline is null || current is null ? new StackChange(baseHasCode, []) : new StackChange(baseHasCode, LayersChanged(baseline, current));
}

/// <summary>
/// §4.18: the normalised composition as stored on a release version (<c>release_versions.stack_composition</c>, jsonb):
/// camelCase JSON of <see cref="StackComposition"/>, with None as <c>null</c>.
/// </summary>
public static class StackCompositionJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(StackComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        return JsonSerializer.Serialize(composition, Options);
    }

    /// <summary>The stored composition, or <c>null</c> for none (or a value that isn't one: it is treated as no stack).</summary>
    public static StackComposition? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StackComposition>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
