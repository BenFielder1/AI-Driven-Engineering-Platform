using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.Infrastructure.Security;

/// <summary>§8/§9: Data Protection keys persisted to the data volume, shared application name.</summary>
public static class DataProtectionSetup
{
    public const string ApplicationNameKey = "DataProtection:ApplicationName";
    public const string KeysPathKey = "DataProtection:KeysPath";
    public const string DefaultApplicationName = "aide";

    /// <summary>Default key directory inside containers (the <c>aide-data</c> volume is mounted at <c>/data</c>).</summary>
    public const string ContainerKeysPath = "/data/keys";

    public static IServiceCollection AddAideDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDataProtection()
            .SetApplicationName(configuration[ApplicationNameKey] is { Length: > 0 } name ? name : DefaultApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(ResolveKeysPath(configuration)));

        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        return services;
    }

    /// <summary><c>DataProtection:KeysPath</c> if set; otherwise <c>/data/keys</c> in a container, or
    /// <c>%LOCALAPPDATA%/aide/keys</c> (<c>~/.local/share/aide/keys</c>) for the dev loop, outside the repo.</summary>
    public static string ResolveKeysPath(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration[KeysPathKey] is { Length: > 0 } configured)
        {
            return configured;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return ContainerKeysPath;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aide", "keys");
    }
}
