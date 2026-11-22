namespace Tevrin.Engine.Domain.Pipelines;

public static partial class PipelineDefinitionValidator
{
    private sealed partial class Run
    {
        /// <summary>
        /// D-0.7.0-43: each <c>handlers:</c> entry at <c>stages[i].handlers[j]</c> is a registered stage handler
        /// (<see cref="DefinitionErrorCodes.UnknownHandler"/>), and every handler name (<c>handler:</c> and <c>handlers:</c>)
        /// is declared once across the pipeline (<see cref="DefinitionErrorCodes.DuplicateId"/>), so a request naming a
        /// handler finds one stage. A stage may declare handlers next to <c>agents:</c> bindings; <c>handlers:</c> never makes
        /// a stage a code stage, so it may sit on an agent stage too.
        /// </summary>
        private void ValidateStageHandlers()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < definition.Stages.Count; i++)
            {
                var stage = definition.Stages[i];
                if (!string.IsNullOrEmpty(stage.Handler) && !names.Add(stage.Handler))
                {
                    Add(DefinitionErrorCodes.DuplicateId, $"stages[{i}].handler", DuplicateHandler(stage.Handler));
                }

                for (var j = 0; j < stage.Handlers.Count; j++)
                {
                    var handler = stage.Handlers[j];
                    var path = $"stages[{i}].handlers[{j}]";
                    if (string.IsNullOrEmpty(handler))
                    {
                        Add(DefinitionErrorCodes.Parse, path, "A handler name is required.");
                    }
                    else if (!names.Add(handler))
                    {
                        Add(DefinitionErrorCodes.DuplicateId, path, DuplicateHandler(handler));
                    }
                    else if (!_handlers.Contains(handler))
                    {
                        Add(DefinitionErrorCodes.UnknownHandler, path, $"No pack registered stage handler '{handler}'.");
                    }
                }
            }
        }

        private static string DuplicateHandler(string handler) =>
            $"Stage handler '{handler}' is already declared in this pipeline; handler names are unique.";
    }
}
