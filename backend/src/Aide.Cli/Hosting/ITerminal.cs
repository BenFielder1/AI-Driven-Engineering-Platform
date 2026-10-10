using System.Text;

namespace Aide.Cli.Hosting;

/// <summary>Standard input, abstracted so prompts and piped input can be faked in tests.</summary>
internal interface ITerminal
{
    /// <summary>True when stdin is an interactive terminal (prompts are allowed).</summary>
    bool IsInteractive { get; }

    /// <summary>Prints <paramref name="prompt"/> to stderr and reads a line; null at end of input.</summary>
    string? Prompt(string prompt);

    /// <summary>Like <see cref="Prompt"/> but does not echo what is typed (tokens).</summary>
    string? PromptSecret(string prompt);

    /// <summary>Reads all of stdin (for `--file -` and piped tokens).</summary>
    string ReadAllInput();
}

internal sealed class SystemTerminal : ITerminal
{
    public bool IsInteractive => !Console.IsInputRedirected;

    public string? Prompt(string prompt)
    {
        Console.Error.Write(prompt);
        return Console.ReadLine();
    }

    public string? PromptSecret(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            return Prompt(prompt);
        }

        Console.Error.Write(prompt);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return buffer.ToString();
    }

    public string ReadAllInput() => Console.In.ReadToEnd();
}
