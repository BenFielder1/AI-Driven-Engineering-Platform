using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Tevrin.Infrastructure.Configuration;

/// <summary>One renamed configuration key: <paramref name="NewKey"/> is read first, <paramref name="OldKey"/> is the
/// pre-0.8.0 name read as a fallback (FR-325, §4.77). Keys use the <c>:</c> form; the env var form uses <c>__</c>.</summary>
public sealed record LegacyConfigName(string NewKey, string OldKey)
{
    public string NewEnvName => NewKey.Replace(":", "__", StringComparison.Ordinal);

    public string OldEnvName => OldKey.Replace(":", "__", StringComparison.Ordinal);
}

/// <summary>How a renamed key resolved in a configuration.</summary>
public enum LegacyConfigUse
{
    /// <summary>Only the new key is set, or neither is: nothing to warn about.</summary>
    None,

    /// <summary>Only the old key is set: its value is used, with a startup warning.</summary>
    OldUsed,

    /// <summary>Both are set: the new key wins, with a startup warning that the old one is ignored.</summary>
    OldIgnored,
}

/// <summary>
/// FR-325 (§4.77, D-0.8.0-7): the one table of config names renamed from Aide to Tevrin, and the helper every reader of
/// them goes through. The old names are read until 0.9.0. Rule when both are set: the new name wins and the old one is
/// ignored, with a warning so the leftover gets removed. An empty value counts as unset.
/// </summary>
public static partial class LegacyConfigNames
{
    /// <summary>The release that stops reading the old names.</summary>
    public const string RemovedIn = "0.9.0";

    public static readonly LegacyConfigName ConnectionString = new(
        $"ConnectionStrings:{DependencyInjection.ConnectionStringName}",
        $"ConnectionStrings:{DependencyInjection.LegacyConnectionStringName}");

    /// <summary>Every renamed key the backend reads. Env vars read only by shell scripts (e.g. <c>TEVRIN_DATA_DIR</c> in
    /// <c>entrypoint.sh</c>) do their own fallback.</summary>
    public static readonly IReadOnlyList<LegacyConfigName> All = [ConnectionString];

    /// <summary>The value of <paramref name="name"/>: the new key if set, else the old key, else <c>null</c>.</summary>
    public static string? Get(IConfiguration configuration, LegacyConfigName name)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(name);
        return NonEmpty(configuration[name.NewKey]) ?? NonEmpty(configuration[name.OldKey]);
    }

    public static LegacyConfigUse Classify(IConfiguration configuration, LegacyConfigName name)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(name);
        if (NonEmpty(configuration[name.OldKey]) is null)
        {
            return LegacyConfigUse.None;
        }

        return NonEmpty(configuration[name.NewKey]) is null ? LegacyConfigUse.OldUsed : LegacyConfigUse.OldIgnored;
    }

    /// <summary>Logs one warning per old name that is set (used or ignored). Called once at startup.</summary>
    /// <returns>The number of warnings logged.</returns>
    public static int LogWarnings(IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var count = 0;
        foreach (var name in All)
        {
            switch (Classify(configuration, name))
            {
                case LegacyConfigUse.OldUsed:
                    LogOldUsed(logger, name.OldKey, name.OldEnvName, name.NewKey, name.NewEnvName, RemovedIn);
                    count++;
                    break;
                case LegacyConfigUse.OldIgnored:
                    LogOldIgnored(logger, name.OldKey, name.NewKey);
                    count++;
                    break;
                case LegacyConfigUse.None:
                default:
                    break;
            }
        }

        return count;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Config name {OldName} ({OldEnvName}) is deprecated; use {NewName} ({NewEnvName}). It is read until {RemovedIn}")]
    private static partial void LogOldUsed(ILogger logger, string oldName, string oldEnvName, string newName, string newEnvName, string removedIn);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Config name {OldName} is ignored because {NewName} is set; remove the old name")]
    private static partial void LogOldIgnored(ILogger logger, string oldName, string newName);
}
