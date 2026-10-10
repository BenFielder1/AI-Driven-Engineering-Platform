namespace Aide.Cli;

/// <summary>A failure the CLI reports as one stderr message and an exit code (no stack trace).</summary>
internal class CliException(string message, int exitCode = ExitCodes.Error) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>Invalid or missing arguments that the parser could not catch (exit code 2).</summary>
internal sealed class UsageException(string message) : CliException(message, ExitCodes.Usage);
