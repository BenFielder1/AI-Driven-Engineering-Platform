using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Stack;

namespace Aide.Domain.Deployments;

/// <summary>
/// FR-143, FR-144: the checks of <c>PUT …/settings/deployment/targets/:component</c> that need no platform call. Pure.
/// <list type="bullet">
/// <item>The provider must be the composition's host for the component (<c>STACK_INCOMPATIBLE</c>); v2's <c>fake</c>
/// provider is also accepted in Fake mode.</item>
/// <item>GitHub Pages needs a GitHub repository (R9, <c>STACK_INCOMPATIBLE</c>).</item>
/// <item>A Local Git project may use targets only with the Fake provider (v2 decision kept, <c>VALIDATION</c>).</item>
/// <item>A connection where the provider needs one; <c>create</c> or <c>link</c>, not both (GitHub Pages: neither needed);
/// a valid resource name or id. Neither is a <b>rebind</b> when the target already has a resource on the same provider
/// (<c>keepsResource</c>): the resource is kept and re-verified with the new connection.</item>
/// </list>
/// </summary>
public static partial class DeploymentTargetSetupRules
{
    public const int NameMaxLength = 100;

    public const int ResourceIdMaxLength = 200;

    public static Result Validate(
        DeploymentComponent component,
        DeploymentProvider provider,
        StackComposition? composition,
        GitProvider gitProvider,
        bool fakeMode,
        Guid? connectionId,
        string? createName,
        bool create,
        string? linkResourceId,
        bool keepsResource = false)
    {
        var host = DeploymentPlatforms.ProviderOf(composition, component);
        var layer = component == DeploymentComponent.Frontend ? StackChoices.FrontendHost : StackChoices.BackendHost;
        if (host != provider && !(fakeMode && provider == DeploymentProvider.Fake))
        {
            var reason = host is { } h
                ? $"The stack deploys the {component.ToWire()} to {DeploymentPlatforms.DisplayName(h)}, not {DeploymentPlatforms.DisplayName(provider)}."
                : $"The stack has no {component.ToWire()} host; choose one in Settings → Stack first.";
            return Result.Failure(StackCompatibility.IncompatibleError([new StackViolation(layer, DeploymentPlatforms.HostOption(provider) ?? provider.ToWire(), "FR-144", reason)]));
        }

        if (provider == DeploymentProvider.GithubPages && !fakeMode && gitProvider is not (GitProvider.GithubApp or GitProvider.GithubPat))
        {
            return Result.Failure(StackCompatibility.IncompatibleError(
                [new StackViolation(layer, "deploy-github-pages", "R9", "GitHub Pages only deploys GitHub repositories.")]));
        }

        var problems = new List<string>();
        if (gitProvider == GitProvider.Local && !fakeMode)
        {
            problems.Add("provider: Deployment needs a hosted Git repository; this project uses Local Git.");
        }

        if (DeploymentPlatforms.NeedsConnection(provider) && connectionId is null)
        {
            problems.Add($"connectionId: Choose one of your {DeploymentPlatforms.DisplayName(DeploymentPlatforms.ConnectionProvider(provider) is { } p ? FromPlatform(p) : provider)} connections.");
        }

        if (create && linkResourceId is not null)
        {
            problems.Add("create: Create a resource or link one, not both.");
        }
        else if (!create && linkResourceId is null && provider != DeploymentProvider.GithubPages && !keepsResource)
        {
            problems.Add("create: Create a resource or link an existing one.");
        }

        if (create && provider != DeploymentProvider.GithubPages && !IsName(createName))
        {
            problems.Add($"create.name: Up to {NameMaxLength} lowercase letters, digits, '.', '_' or '-', without '---'.");
        }

        if (linkResourceId is not null && !IsResourceId(linkResourceId))
        {
            problems.Add("link.resourceId: Not a resource id.");
        }

        return Projects.ProjectRules.ToResult(problems, "The deployment target is invalid.");
    }

    /// <summary>A resource name every platform accepts (Vercel's project-name rules, the strictest).</summary>
    public static bool IsName(string? name) => DeploymentSettingsRules.IsVercelProjectName(name);

    public static bool IsResourceId(string? id) => id is { Length: > 0 and <= ResourceIdMaxLength } && ResourceIdPattern().IsMatch(id) && id.Trim('.').Length > 0;

    private static DeploymentProvider FromPlatform(PlatformProvider platform) => platform switch
    {
        PlatformProvider.Netlify => DeploymentProvider.Netlify,
        PlatformProvider.Cloudflare => DeploymentProvider.Cloudflare,
        PlatformProvider.Render => DeploymentProvider.Render,
        PlatformProvider.Railway => DeploymentProvider.Railway,
        _ => DeploymentProvider.Vercel,
    };

    [GeneratedRegex("^[A-Za-z0-9._:/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceIdPattern();
}
