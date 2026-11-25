namespace Tevrin.Engine.Domain.Pipelines;

public static partial class PipelineDefinitionValidator
{
    private sealed partial class Run
    {
        /// <summary>
        /// FR-354: each <c>followups:</c> entry names a top-level stage once, its durations parse, are positive and
        /// distinct, and no part of the main flow reaches the stage (<c>DEF_FOLLOWUP_STAGE</c>).
        /// </summary>
        private void ValidateFollowUps()
        {
            var listed = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < definition.FollowUps.Count; i++)
            {
                var followUp = definition.FollowUps[i];
                var path = $"followups[{i}]";
                if (definition.FindStage(followUp.Stage) is null)
                {
                    Add(DefinitionErrorCodes.FollowUpStage, $"{path}.stage", $"Follow-up stage '{followUp.Stage}' is not a top-level stage.");
                }
                else if (!listed.Add(followUp.Stage))
                {
                    Add(DefinitionErrorCodes.FollowUpStage, $"{path}.stage", $"Stage '{followUp.Stage}' is listed in 'followups' more than once.");
                }
                else if (ReachedBy(followUp.Stage) is { } by)
                {
                    Add(DefinitionErrorCodes.FollowUpStage, $"{path}.stage", $"Follow-up stage '{followUp.Stage}' is reached by the main flow ({by}).");
                }
                else if (!IsPlainAgentStage(definition.FindStage(followUp.Stage)!))
                {
                    Add(
                        DefinitionErrorCodes.FollowUpStage,
                        $"{path}.stage",
                        $"Follow-up stage '{followUp.Stage}' must be an agent stage with an output and no gate, parallel, fan_out, variants, veto or escalation.");
                }

                if (followUp.After.Count == 0)
                {
                    Add(DefinitionErrorCodes.FollowUpStage, $"{path}.after", "'after' needs at least one duration.");
                }

                var durations = new HashSet<string>(StringComparer.Ordinal);
                for (var j = 0; j < followUp.After.Count; j++)
                {
                    var after = followUp.After[j];
                    if (FollowUpRules.ParseDuration(after) is null)
                    {
                        Add(DefinitionErrorCodes.FollowUpStage, $"{path}.after[{j}]", $"'{after}' is not a duration like 1d, 12h or 30m greater than zero.");
                    }
                    else if (!durations.Add(after))
                    {
                        Add(DefinitionErrorCodes.FollowUpStage, $"{path}.after[{j}]", $"Duration '{after}' is listed more than once.");
                    }
                }
            }
        }

        /// <summary>A follow-up runs after the run completed, so it can't wait on a gate or escalation or open branches.</summary>
        private static bool IsPlainAgentStage(StageDefinition stage) =>
            stage is { Agent: not null, Output: not null, Gate: null, Parallel: null, FanOut: null, Variants: null, Veto.Count: 0 }
            && stage.OnInvalid?.Then != InvalidOutcome.Escalate;

        /// <summary>What in the main flow reaches <paramref name="stageId"/>, or <c>null</c>.</summary>
        private string? ReachedBy(string stageId)
        {
            if (definition.Start.Contains(stageId, StringComparer.Ordinal))
            {
                return "start";
            }

            foreach (var stage in definition.AllStages.Where(s => !string.Equals(s.Id, stageId, StringComparison.Ordinal)))
            {
                string?[] targets = [stage.OnSuccess, stage.OnReject?.To, stage.Loop?.To, stage.OnVeto?.To, .. stage.Routes];
                if (targets.Contains(stageId, StringComparer.Ordinal))
                {
                    return $"stage '{stage.Id}'";
                }
            }

            return null;
        }
    }
}
