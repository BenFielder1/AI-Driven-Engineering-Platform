using System.Globalization;
using Aide.Domain.Entities;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Agents;

/// <summary>
/// FR-317: the SDLC context values (<see cref="SdlcContextNames"/> → value) of a project and of its version's
/// <see cref="IterationContext"/>, as 0.6.1's job handlers put them into the prompt. Pure; the context providers read the
/// project and the iteration context and pass these to <see cref="SdlcAgentContext.Sections"/>.
/// </summary>
public static class SdlcContextValues
{
    /// <summary>The project's name, idea and max files per step.</summary>
    public static Dictionary<string, string?> FromProject(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SdlcContextNames.ProjectName] = project.Name,
            [SdlcContextNames.Idea] = project.Idea,
            [SdlcContextNames.MaxFilesPerStep] = project.MaxFilesPerStep.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// The codebase summary, the baseline spec and project request, the spec mode (§4.13) and, with a version, its request
    /// and base (<see cref="BaseText"/>; none without a base commit, where the analysts don't run).
    /// </summary>
    public static Dictionary<string, string?> FromIteration(IterationContext iteration)
    {
        ArgumentNullException.ThrowIfNull(iteration);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SdlcContextNames.CodebaseSummary] = iteration.CodebaseSummary?.Content,
            [SdlcContextNames.SpecMode] = iteration.SpecContext.Mode,
            [SdlcContextNames.BaselineSpecification] = iteration.BaselineSpecification?.Content,
            [SdlcContextNames.PreviousProjectRequest] = iteration.BaselineProjectRequest?.Content,
            [SdlcContextNames.PreviousSpecification] = iteration.BaselineSpecification?.Content,
            [SdlcContextNames.VersionRequest] = iteration.Version?.Request,
            [SdlcContextNames.Base] = iteration.Version is { } version ? BaseText(version) : null,
        };
    }

    /// <summary>
    /// <c>{{BASE}}</c> of the Repository and Stack Analysts: <c>&lt;base tag&gt; (&lt;sha&gt;)</c>, else
    /// <c>commit &lt;sha&gt;</c>; <c>null</c> without a base commit.
    /// </summary>
    public static string? BaseText(ReleaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (string.IsNullOrWhiteSpace(version.BaseCommitSha))
        {
            return null;
        }

        return version.BaseTag is { } tag ? $"{tag} ({version.BaseCommitSha})" : $"commit {version.BaseCommitSha}";
    }
}
