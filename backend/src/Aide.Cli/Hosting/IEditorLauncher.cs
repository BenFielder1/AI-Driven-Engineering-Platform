using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Aide.Cli.Hosting;

/// <summary>Opens a file in the user's editor and waits for it to close.</summary>
internal interface IEditorLauncher
{
    Task EditAsync(string filePath, CancellationToken cancellationToken);
}

/// <summary>Runs $VISUAL, then $EDITOR, else notepad (Windows) or vi.</summary>
internal sealed class ProcessEditorLauncher(Func<string, string?> getEnvironmentVariable) : IEditorLauncher
{
    public async Task EditAsync(string filePath, CancellationToken cancellationToken)
    {
        var parts = SplitCommandLine(ResolveEditorCommand(getEnvironmentVariable));
        if (parts.Count == 0)
        {
            throw new CliException("No editor configured; set VISUAL or EDITOR, or pass --file.");
        }

        var startInfo = new ProcessStartInfo(parts[0]) { UseShellExecute = false };
        foreach (var argument in parts.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(filePath);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            throw new CliException($"Could not start editor '{parts[0]}': {ex.Message}. Set VISUAL or EDITOR, or pass --file.");
        }

        if (process is null)
        {
            throw new CliException($"Could not start editor '{parts[0]}'.");
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new CliException($"Editor '{parts[0]}' exited with code {process.ExitCode}; nothing was saved.");
            }
        }
    }

    internal static string ResolveEditorCommand(Func<string, string?> getEnvironmentVariable)
    {
        foreach (var name in new[] { "VISUAL", "EDITOR" })
        {
            var value = getEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return OperatingSystem.IsWindows() ? "notepad" : "vi";
    }

    /// <summary>Splits `code --wait` style editor commands on whitespace, honouring double quotes.</summary>
    internal static List<string> SplitCommandLine(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in command)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }
}
