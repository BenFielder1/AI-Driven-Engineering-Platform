using Tevrin.Engine.Domain.Jobs;

namespace Tevrin.Engine.Domain.Pipelines;

public static partial class PipelineDefinitionValidator
{
    private sealed partial class Run
    {
        /// <summary>
        /// D-0.7.0-42: agent keys and job types are unique across the pipeline (<see cref="DefinitionErrorCodes.DuplicateId"/>).
        /// Primary agents first, at <c>stages[i].agent.key</c> and <c>.agent.job_type</c>; primaries may share the engine's
        /// generic <see cref="EngineJobTypes.AgentRun"/> job (its job names the stage). Then each <c>agents:</c> entry at
        /// <c>stages[i].agents[j]</c>: its key is required and unique (no primary and no other binding has it); its job type
        /// is required, registered and unique (no primary's, <c>agent.run</c> included, and no other binding's); its inputs
        /// and output are declared artifacts; its tools are registered; and its prompt (<c>prompt</c>, else the key) is one
        /// the pack registered and can be rendered from the binding's inputs, as for <c>agent.prompt</c>.
        /// </summary>
        private void ValidateAgentBindings()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var jobTypes = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < definition.Stages.Count; i++)
            {
                if (definition.Stages[i].Agent is not { } agent)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(agent.Key) && !keys.Add(agent.Key))
                {
                    Add(DefinitionErrorCodes.DuplicateId, $"stages[{i}].agent.key", $"Agent '{agent.Key}' is already bound in this pipeline; agent keys are unique.");
                }

                if (!string.IsNullOrEmpty(agent.JobType) && !jobTypes.Add(agent.JobType) && agent.JobType != EngineJobTypes.AgentRun)
                {
                    Add(DefinitionErrorCodes.DuplicateId, $"stages[{i}].agent.job_type", $"Job type '{agent.JobType}' already runs another agent in this pipeline.");
                }
            }

            for (var i = 0; i < definition.Stages.Count; i++)
            {
                var stage = definition.Stages[i];
                for (var j = 0; j < stage.Agents.Count; j++)
                {
                    ValidateBinding(stage, stage.Agents[j], $"stages[{i}].agents[{j}]", keys, jobTypes);
                }
            }
        }

        private void ValidateBinding(StageDefinition stage, AgentBindingDefinition binding, string path, HashSet<string> keys, HashSet<string> jobTypes)
        {
            var keyValid = false;
            if (string.IsNullOrEmpty(binding.Key))
            {
                Add(DefinitionErrorCodes.Parse, path + ".key", "'key' is required.");
            }
            else if (!keys.Add(binding.Key))
            {
                Add(DefinitionErrorCodes.DuplicateId, path + ".key", $"Agent '{binding.Key}' is already bound in this pipeline; agent keys are unique.");
            }
            else
            {
                keyValid = true;
            }

            if (!string.IsNullOrEmpty(binding.JobType) && !jobTypes.Add(binding.JobType))
            {
                Add(DefinitionErrorCodes.DuplicateId, path + ".job_type", $"Job type '{binding.JobType}' already runs another agent in this pipeline.");
            }
            else
            {
                CheckJobType(binding.JobType, path + ".job_type");
            }

            for (var k = 0; k < binding.Inputs.Count; k++)
            {
                CheckArtifactRef(binding.Inputs[k], $"{path}.inputs[{k}]");
            }

            if (binding.Output is { } output)
            {
                CheckArtifactRef(output, path + ".output");
                CheckNotProjectScoped(output, path + ".output");
            }

            for (var t = 0; t < binding.Tools.Count; t++)
            {
                var tool = binding.Tools[t];
                if (string.IsNullOrEmpty(tool))
                {
                    Add(DefinitionErrorCodes.Parse, $"{path}.tools[{t}]", "A tool name is required.");
                }
                else if (!_tools.Contains(tool))
                {
                    Add(DefinitionErrorCodes.UnknownTool, $"{path}.tools[{t}]", $"No pack registered tool '{tool}'.");
                }
            }

            // A duplicate key's prompt is checked at its first binding only.
            if (keyValid && stage.ForAgent(binding.Key) is { Agent: { } agent } view)
            {
                CheckPrompt(view, agent, path);

                // D-0.7.0-44: the binding's own validate: and on_invalid:, checked as a stage's.
                ValidateGuards(view, path);
            }
        }
    }
}
