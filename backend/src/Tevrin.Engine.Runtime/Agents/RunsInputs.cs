using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>FR-357: the <c>from_runs</c> list inputs of a stage, read from the project's earlier runs.</summary>
public static class RunsInputs
{
    /// <summary>
    /// One list per <c>from_runs</c> input of <paramref name="stage"/> (in <c>inputs:</c> order), as of
    /// <paramref name="now"/> (<see cref="RunsInputRules.Select"/>); <paramref name="only"/>, when given, limits the
    /// inputs read to those whose placeholder it holds. Empty when the stage has none. A run whose final version fails its
    /// integrity check (FR-20: corrupted, or content no longer matching its hash) drops out; it is never fed to an agent or
    /// handler, and no older version stands in for it.
    /// </summary>
    public static async Task<IReadOnlyList<RunsInputList>> LoadAsync(
        IAgentStageStore store, PipelineRun run, StageDefinition stage, DateTimeOffset now, CancellationToken cancellationToken,
        IReadOnlySet<string>? only = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(stage);
        var lists = new List<RunsInputList>();
        foreach (var input in stage.FromRuns.OrderBy(r => r.Position))
        {
            var placeholder = RunsInputRules.Placeholder(input);
            if ((only is not null && !only.Contains(placeholder)) || RunsInputRules.Since(input, now) is not { } since)
            {
                continue;
            }

            var candidates = (await store.RunsOutputsAsync(run, input, since, cancellationToken))
                .Where(c => ArtifactRules.IntegrityOf(c.ArtifactStatus, c.Content, c.ContentHash) == ArtifactIntegrity.Ok);
            lists.Add(new RunsInputList(input, placeholder, RunsInputRules.Select(input, candidates, run.Id, now)));
        }

        return lists;
    }

    /// <summary><paramref name="facts"/> with the stage's lists (<see cref="RunsInputFact"/>) when it has <c>from_runs</c> inputs.</summary>
    public static async Task<FactBag> WithFactAsync(
        IAgentStageStore store, PipelineRun run, StageDefinition stage, DateTimeOffset now, FactBag facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(facts);
        return stage.FromRuns.Count == 0
            ? facts
            : facts.With(new RunsInputFact(await LoadAsync(store, run, stage, now, cancellationToken)));
    }
}
