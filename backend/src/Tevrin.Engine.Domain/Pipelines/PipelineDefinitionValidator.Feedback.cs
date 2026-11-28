namespace Tevrin.Engine.Domain.Pipelines;

public static partial class PipelineDefinitionValidator
{
    /// <summary>FR-351 (0.8.0): the feedback store's stage settings.</summary>
    private sealed partial class Run
    {
        /// <summary>
        /// FR-351: <c>feedback</c> needs an agent stage (<see cref="DefinitionErrorCodes.StageKind"/>) and a limit of 1–50;
        /// <c>writes_feedback</c> needs an output and a <c>from</c> field, and its agent must be one the pipeline declares
        /// (a stage's agent or an <c>agents:</c> binding, <see cref="DefinitionErrorCodes.FanOutAgent"/>).
        /// </summary>
        private void ValidateFeedback(StageDefinition stage, string path)
        {
            if (stage.Feedback is { } feedback)
            {
                if (stage.Agent is null)
                {
                    Add(DefinitionErrorCodes.StageKind, path + ".feedback", "'feedback' needs an agent stage.");
                }

                if (feedback.Limit is < FeedbackInjectionRules.MinLimit or > FeedbackInjectionRules.MaxLimit)
                {
                    Add(DefinitionErrorCodes.Parse, path + ".feedback.limit",
                        $"'limit' must be {FeedbackInjectionRules.MinLimit}–{FeedbackInjectionRules.MaxLimit}.");
                }
            }

            if (stage.WritesFeedback is not { } writes)
            {
                return;
            }

            if (string.IsNullOrEmpty(stage.Output))
            {
                Add(DefinitionErrorCodes.Parse, path + ".writes_feedback", "'writes_feedback' needs a stage with an output.");
            }

            if (string.IsNullOrEmpty(writes.From))
            {
                Add(DefinitionErrorCodes.Parse, path + ".writes_feedback.from", "'from' is required.");
            }

            var at = path + ".writes_feedback.agent";
            if (string.IsNullOrEmpty(writes.Agent))
            {
                Add(DefinitionErrorCodes.Parse, at, "An agent key is required.");
            }
            else if (!FeedbackInjectionRules.DeclaresAgent(definition, writes.Agent))
            {
                Add(DefinitionErrorCodes.FanOutAgent, at, $"Agent '{writes.Agent}' is not declared by any stage of the pipeline.");
            }
        }
    }
}
