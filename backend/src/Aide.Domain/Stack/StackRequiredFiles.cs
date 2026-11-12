namespace Aide.Domain.Stack;

/// <summary>
/// FR-241, §4.40: a deployment file a resolved composition needs: the deploy pack and its platform name, the repository
/// path (the pack's path in the folder its rule resolves to), the accepted alternatives in that folder, the specialist
/// that writes it and the required content lines.
/// </summary>
public sealed record StackRequiredFile(
    string Pack,
    string Platform,
    string Path,
    IReadOnlyList<string> Alternatives,
    string Owner,
    IReadOnlyList<string> Content)
{
    /// <summary>The path and its alternatives.</summary>
    public IEnumerable<string> AcceptedPaths => [Path, .. Alternatives];
}

/// <summary>
/// FR-241, FR-242, §4.40: the required deployment files of a resolved composition. Pure.
/// <list type="bullet">
/// <item>Deploy packs in catalog choice order (frontend host, backend host, database host), each once.</item>
/// <item>Folder <c>app</c>: each folder the pack deploys: the frontend path when it hosts the frontend, the backend path when
/// it hosts the backend (the layer paths, FR-119; <c>.</c> is the repository root). Folder <c>root</c>: the repository
/// root, once.</item>
/// <item>A pack that only hosts the database deploys no app folder, so it needs no file.</item>
/// </list>
/// </summary>
public static class StackRequiredFiles
{
    public static IReadOnlyList<StackRequiredFile> For(StackResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var composition = resolution.Composition;
        var deploy = composition.Deploy;
        var files = new List<StackRequiredFile>();
        foreach (var pack in resolution.Packs.Where(p => p.Layer == StackLayers.Deploy && p.Pack.Manifest.RequiredFiles.Count > 0))
        {
            var folders = new List<string>();
            void AddFolder(string? path)
            {
                var folder = Folder(path);
                if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    folders.Add(folder);
                }
            }

            // ResolvedPack keeps the first choice a pack was picked for; the same option on a later host choice is the same pack.
            var hostsFrontend = pack.Choice == StackChoices.FrontendHost;
            var hostsBackend = pack.Choice == StackChoices.BackendHost
                || (hostsFrontend && deploy.BackendHost is not null && deploy.BackendHost == deploy.FrontendHost);
            if (hostsFrontend && composition.Frontend is { } frontend)
            {
                AddFolder(frontend.Path);
            }

            if (hostsBackend && composition.Backend is { } backend)
            {
                AddFolder(backend.Path);
            }

            if (folders.Count == 0)
            {
                continue;
            }

            foreach (var file in pack.Pack.Manifest.RequiredFiles)
            {
                var targets = file.Folder == PackVocabulary.RequiredFileRootFolder ? [string.Empty] : folders;
                foreach (var folder in targets)
                {
                    var path = Join(folder, file.Path);
                    if (files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    files.Add(new StackRequiredFile(
                        pack.Id,
                        pack.Pack.Manifest.Name,
                        path,
                        [.. file.Alternatives.Select(a => Join(folder, a))],
                        file.Owner,
                        file.Content));
                }
            }
        }

        return files;
    }

    /// <summary>A layer path as a folder prefix: empty for the repository root.</summary>
    private static string Folder(string? path)
    {
        var folder = (path ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
        return folder is "." ? string.Empty : folder;
    }

    private static string Join(string folder, string path) => folder.Length == 0 ? path : $"{folder}/{path}";
}
