using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Runtime.Jobs;

namespace Tevrin.Engine.Runtime.Agents;

public sealed partial class AgentStageRunner
{
    /// <summary>
    /// FR-350: runs each <c>validate: [{ agent: &lt;key&gt; }]</c> validator agent on the attempt's output, each as its own
    /// recorded agent run keyed <c>validate-&lt;key&gt;</c> from the attempt's run (FR-310: a redelivery reuses it, so it
    /// takes the same verdict). The output reaches the validator as <c>{{OUTPUT}}</c> (and the stage output's own
    /// placeholder). Every finding is stored on the attempt's agent run; the rejecting ones are returned as the attempt's
    /// reasons, which <c>on_invalid</c> then treats exactly like failed guards'. A stage without validator agents makes no
    /// call and changes nothing (the SDLC's path).
    /// </summary>
    private async Task<IReadOnlyList<string>> RunValidatorAgentsAsync(AgentAttempt attempt, string content, CancellationToken cancellationToken)
    {
        var validators = attempt.Stage.Validate.Where(v => v.Agent is not null).Select(v => v.Agent!).ToList();
        if (validators.Count == 0 || attempt.AgentRun is not { } agentRun)
        {
            return [];
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ValidatorAgentRules.OutputPlaceholder] = content,
        };
        if (!string.IsNullOrEmpty(attempt.Stage.Output))
        {
            values[AgentStagePrompt.PlaceholderOf(attempt.Stage.Output)] = content;
        }

        var findings = new List<ValidatorFinding>();
        var reasons = new List<string>();
        foreach (var key in validators)
        {
            var outcome = await RunAsync(
                new AgentStageRequest(attempt.Run.Id, attempt.Stage.Id, JobTriggers.StageEntered)
                {
                    AgentKey = key,
                    AgentRunId = AgentRunRules.RunId(agentRun.Id, ValidatorAgentRules.RunKey(key)),
                    JobAttempt = attempt.Request.JobAttempt,
                    JobId = attempt.Request.JobId,
                    PromptValues = values,
                },
                cancellationToken);
            var verdict = (outcome.Valid ? ValidatorAgentRules.Read(key, ReadOutput(outcome.AgentRun.Output)?.Text) : null)
                ?? await FailOnInvalidValidatorAsync(agentRun, key, attempt.Stage.Id);
            findings.AddRange(verdict.Findings);
            reasons.AddRange(ValidatorAgentRules.Reasons(verdict, key));
        }

        agentRun.ValidatorFindings = ValidatorAgentRules.StoredFindings(findings);
        return reasons;
    }

    /// <summary>FR-350: a validator that didn't answer the verdict schema fails the attempt (<c>VALIDATOR_INVALID_OUTPUT</c>).</summary>
    private async Task<ValidatorVerdict> FailOnInvalidValidatorAsync(AgentRun agentRun, string key, string stageId)
    {
        var message = $"The {key} validator of {stageId} must answer {{ \"verdict\": \"pass\" | \"reject\", \"findings\": [...] }}.";
        agentRun.Status = AgentRunStatus.Failed;
        agentRun.Error = $"{EngineErrorCodes.ValidatorInvalidOutput}: {message}";
        await store.SaveChangesAsync(CancellationToken.None);
        throw new JobFailedException(message, transient: false, EngineErrorCodes.ValidatorInvalidOutput);
    }
}
