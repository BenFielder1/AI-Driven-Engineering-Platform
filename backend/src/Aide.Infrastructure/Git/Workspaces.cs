using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.Git;

/// <summary>
/// Where Git working copies live (§5, §9): <c>Workspaces:Root</c> if set; otherwise <c>/data/workspaces</c> in a
/// container (the <c>aide-data</c> volume), or <c>%LOCALAPPDATA%/aide/workspaces</c> for the dev loop.
/// A project's working copy is <c>{root}/{projectId}</c>.
/// </summary>
public interface IWorkspaceStore
{
    string Root { get; }

    string PathFor(Guid projectId);

    /// <summary>Deletes a project's working copy if there is one. Best-effort: failures are logged, never thrown.</summary>
    bool TryDelete(Guid projectId);
}

internal sealed partial class WorkspaceStore(IConfiguration configuration, ILogger<WorkspaceStore> logger) : IWorkspaceStore
{
    public const string RootKey = "Workspaces:Root";
    public const string ContainerRoot = "/data/workspaces";

    public string Root { get; } = ResolveRoot(configuration);

    public static string ResolveRoot(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration[RootKey] is { Length: > 0 } configured)
        {
            return Path.GetFullPath(configured);
        }

        if (string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return ContainerRoot;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aide", "workspaces");
    }

    public string PathFor(Guid projectId) => Path.Combine(Root, projectId.ToString("D"));

    public bool TryDelete(Guid projectId)
    {
        var path = PathFor(projectId);
        try
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            // Git marks object files read-only, which blocks deletion on Windows.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
            LogDeleted(logger, projectId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(logger, ex, projectId);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted the working copy of project {ProjectId}")]
    private static partial void LogDeleted(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the working copy of project {ProjectId}; remove it manually")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, Guid projectId);
}

