namespace Aide.Domain.Stack;

/// <summary>
/// FR-119, §4.18: the rules between the frontend and backend layer paths (each already normalised by
/// <see cref="StackCompatibility.NormalisePath"/>: relative, no <c>..</c>, <c>.</c> for the repo root). Specialists' owned
/// paths are expanded from them (<c>{fe}/**</c>, <c>{be}/**</c>), so the two may not overlap:
/// <list type="bullet">
/// <item>they must differ;</item>
/// <item>the repo root <c>.</c> is only for a layer that is alone (a Next.js full-stack app, a frontend-only repo, or an
/// API-only repo): with both a frontend and a backend, neither may be <c>.</c>;</item>
/// <item>neither may be inside the other (<c>web</c> and <c>web/api</c>).</item>
/// </list>
/// A <c>null</c> path is a layer that is None.
/// </summary>
public static class StackPaths
{
    /// <summary>The repository root.</summary>
    public const string Root = ".";

    /// <summary>The input errors of a frontend and backend path pair (empty when they are fine).</summary>
    public static IReadOnlyList<StackInputError> Check(string? frontendPath, string? backendPath)
    {
        if (frontendPath is null || backendPath is null)
        {
            return [];
        }

        if (frontendPath == backendPath)
        {
            return [new StackInputError("backend.path", $"must differ from the frontend path ('{frontendPath}').")];
        }

        if (frontendPath == Root)
        {
            return [new StackInputError("frontend.path", "can be the repository root ('.') only when there's no backend.")];
        }

        if (backendPath == Root)
        {
            return [new StackInputError("backend.path", "can be the repository root ('.') only when there's no frontend.")];
        }

        if (IsInside(backendPath, frontendPath))
        {
            return [new StackInputError("backend.path", $"may not be inside the frontend path ('{frontendPath}').")];
        }

        if (IsInside(frontendPath, backendPath))
        {
            return [new StackInputError("frontend.path", $"may not be inside the backend path ('{backendPath}').")];
        }

        return [];
    }

    private static bool IsInside(string path, string folder) => path.StartsWith(folder + "/", StringComparison.Ordinal);
}
