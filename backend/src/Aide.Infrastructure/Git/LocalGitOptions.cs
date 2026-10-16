namespace Aide.Infrastructure.Git;

/// <summary>
/// Local Git provider settings (section <c>Git:Local</c>). <see cref="ImportRoot"/> enables the Development/E2E-only Local
/// import (FR-73, §8 Testing): <c>git.localSourcePath</c> must resolve under it. Empty (the default) disables Local import.
/// </summary>
public sealed class LocalGitOptions
{
    public const string SectionName = "Git:Local";

    public string? ImportRoot { get; set; }
}
