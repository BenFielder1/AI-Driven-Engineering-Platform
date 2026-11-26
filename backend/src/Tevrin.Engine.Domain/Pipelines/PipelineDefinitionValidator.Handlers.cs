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
            foreach (var (stage, at) in _allStages)
            {
                if (!string.IsNullOrEmpty(stage.Handler) && !names.Add(stage.Handler))
                {
                    Add(DefinitionErrorCodes.DuplicateId, $"{at}.handler", DuplicateHandler(stage.Handler));
                }

                for (var j = 0; j < stage.Handlers.Count; j++)
                {
                    var handler = stage.Handlers[j];
                    var path = $"{at}.handlers[{j}]";
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

                // FR-355: a map or reduce handler is a handler name too (registration is checked with the map).
                if (stage.Map?.Handler is { Length: > 0 } mapHandler && !names.Add(mapHandler))
                {
                    Add(DefinitionErrorCodes.DuplicateId, $"{at}.map.handler", DuplicateHandler(mapHandler));
                }

                if (stage.Reduce?.Handler is { Length: > 0 } reduceHandler && !names.Add(reduceHandler))
                {
                    Add(DefinitionErrorCodes.DuplicateId, $"{at}.reduce.handler", DuplicateHandler(reduceHandler));
                }
            }
        }

        private static string DuplicateHandler(string handler) =>
            $"Stage handler '{handler}' is already declared in this pipeline; handler names are unique.";
    }
}
