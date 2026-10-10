using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aide.Cli.Config;

/// <summary>The persisted config file (`config.json`).</summary>
internal sealed class CliConfig
{
    [JsonPropertyName("apiUrl")]
    public string? ApiUrl { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    /// <summary>The normalised API URL the saved token was saved for; it is only ever sent there (see CliSettings).</summary>
    [JsonPropertyName("tokenApiUrl")]
    public string? TokenApiUrl { get; set; }
}

/// <summary>Reads and writes the config file. On Unix the folder is 0700 and the file 0600 (it holds the token).</summary>
internal sealed class ConfigStore(string path)
{
    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true };

    public string FilePath { get; } = path;

    /// <summary>AIDE_CONFIG (a file path) wins; otherwise `&lt;user config root&gt;/aide/config.json`.</summary>
    public static string ResolvePath(Func<string, string?> getEnvironmentVariable, string userConfigRoot)
    {
        var overridePath = getEnvironmentVariable(EnvironmentVariables.Config);
        return !string.IsNullOrWhiteSpace(overridePath)
            ? Path.GetFullPath(overridePath)
            : Path.Combine(userConfigRoot, "aide", "config.json");
    }

    public CliConfig Load()
    {
        if (!File.Exists(FilePath))
        {
            return new CliConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(FilePath), FileJson) ?? new CliConfig();
        }
        catch (JsonException ex)
        {
            throw new CliException($"The config file {FilePath} is not valid JSON ({ex.Message}). Fix or delete it.");
        }
        catch (IOException ex)
        {
            throw new CliException($"Could not read the config file {FilePath}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CliException($"Could not read the config file {FilePath}: {ex.Message}");
        }
    }

    public void Save(CliConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(directory))
            {
                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(directory);
                }
                else
                {
                    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            // Write a sibling temp file (created owner-only on Unix) and swap it in, so a crash never leaves half a file.
            var tempPath = FilePath + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(tempPath, options))
            {
                JsonSerializer.Serialize(stream, config, FileJson);
            }

            File.Move(tempPath, FilePath, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (IOException ex)
        {
            throw new CliException($"Could not write the config file {FilePath}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CliException($"Could not write the config file {FilePath}: {ex.Message}");
        }
    }
}
