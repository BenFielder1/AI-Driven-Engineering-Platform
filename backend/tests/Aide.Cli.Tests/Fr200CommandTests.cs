using System.Net;
using Aide.Cli.Config;

namespace Aide.Cli.Tests;

/// <summary>
/// FR-200, §4.29: the CLI is <c>tevrin</c>; <c>aide</c> is a deprecated alias that prints a notice to stderr and then behaves
/// identically; <c>TEVRIN_*</c> variables are read first, then <c>AIDE_*</c> (empty counts as unset); tokens keep <c>aide_pat_</c>.
/// </summary>
public sealed class Fr200CommandTests : IDisposable
{
    private const string Unset = "<unset>";

    private readonly CliTestHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    private static string? Value(string value) => value == Unset ? null : value;

    // §4.29 worked example: value = TEVRIN_X ?? AIDE_X ?? config file ?? default.
    [Theory]
    [InlineData("aide_pat_new", "aide_pat_old", Unset, "aide_pat_new")]
    [InlineData(Unset, "aide_pat_old", "aide_pat_cfg", "aide_pat_old")]
    [InlineData(Unset, Unset, "aide_pat_cfg", "aide_pat_cfg")]
    [InlineData("", "aide_pat_old", Unset, "aide_pat_old")]
    public void FR200_Token_resolution_follows_the_rename_table(string tevrinToken, string aideToken, string configToken, string expected)
    {
        var environment = new Dictionary<string, string?> { ["TEVRIN_TOKEN"] = Value(tevrinToken), ["AIDE_TOKEN"] = Value(aideToken) };
        var config = new CliConfig { Token = Value(configToken), TokenApiUrl = Value(configToken) is null ? null : CliSettings.DefaultApiUrl };

        var settings = CliSettings.Resolve(null, name => environment.GetValueOrDefault(name), config);

        Assert.Equal(expected, settings.Token);
        Assert.Equal(configToken == expected ? SettingSource.File : SettingSource.Environment, settings.TokenSource);
    }

    [Fact]
    public async Task FR200_Only_AIDE_TOKEN_set_still_authenticates_tevrin_projects_list()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.ProjectsPage));
        _cli.Environment["AIDE_TOKEN"] = "aide_pat_old_000000000000000000";

        var run = await _cli.RunAsync("projects", "list", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal("Bearer aide_pat_old_000000000000000000", Assert.Single(_cli.Requests).Authorization);
    }

    [Fact]
    public async Task FR200_TEVRIN_TOKEN_wins_over_AIDE_TOKEN()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.ProjectsPage));
        _cli.Environment["TEVRIN_TOKEN"] = "aide_pat_new_111111111111111111";
        _cli.Environment["AIDE_TOKEN"] = "aide_pat_old_000000000000000000";

        var run = await _cli.RunAsync("projects", "list", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal("Bearer aide_pat_new_111111111111111111", Assert.Single(_cli.Requests).Authorization);
    }

    [Theory]
    [InlineData("https://tevrin.test", "https://aide.test", "tevrin.test")]
    [InlineData(null, "https://aide.test", "aide.test")]
    [InlineData("", "https://aide.test", "aide.test")]
    public async Task FR200_TEVRIN_API_URL_is_read_before_AIDE_API_URL(string? tevrinUrl, string aideUrl, string expectedAuthority)
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.ProjectsPage));
        _cli.Environment["TEVRIN_TOKEN"] = CliTestHarness.Token;
        _cli.Environment["TEVRIN_API_URL"] = tevrinUrl;
        _cli.Environment["AIDE_API_URL"] = aideUrl;

        var run = await _cli.RunAsync("projects", "list", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(expectedAuthority, Assert.Single(_cli.Requests).Uri.Authority);
    }

    [Fact]
    public async Task FR200_TEVRIN_CONFIG_is_read_before_AIDE_CONFIG_and_the_default_location_is_unchanged()
    {
        var tevrin = Path.Combine(_cli.ConfigRoot, "tevrin.json");
        var aide = Path.Combine(_cli.ConfigRoot, "aide.json");

        var byDefault = await _cli.RunAsync("config", "path");
        _cli.Environment["AIDE_CONFIG"] = aide;
        var legacy = await _cli.RunAsync("config", "path");
        _cli.Environment["TEVRIN_CONFIG"] = tevrin;
        var current = await _cli.RunAsync("config", "path");
        _cli.Environment["TEVRIN_CONFIG"] = string.Empty;
        var empty = await _cli.RunAsync("config", "path");

        Assert.Equal(Path.Combine(_cli.ConfigRoot, "aide", "config.json"), byDefault.Out.Trim());
        Assert.Equal((aide, tevrin, aide), (legacy.Out.Trim(), current.Out.Trim(), empty.Out.Trim()));
    }

    [Fact]
    public void FR200_Every_CLI_variable_reads_TEVRIN_first_then_AIDE()
    {
        Assert.Equal(
            [("TEVRIN_CONFIG", "AIDE_CONFIG"), ("TEVRIN_API_URL", "AIDE_API_URL"), ("TEVRIN_TOKEN", "AIDE_TOKEN"), ("TEVRIN_GIT_TOKEN", "AIDE_GIT_TOKEN")],
            new[] { EnvironmentVariables.Config, EnvironmentVariables.ApiUrl, EnvironmentVariables.Token, EnvironmentVariables.GitToken }
                .Select(v => (v.Name, v.LegacyName)));

        var environment = new Dictionary<string, string?> { ["TEVRIN_GIT_TOKEN"] = " ", ["AIDE_GIT_TOKEN"] = "ghp_old" };
        string? Get(string name) => environment.GetValueOrDefault(name);
        Assert.Equal(("ghp_old", "AIDE_GIT_TOKEN"), (EnvironmentVariables.GitToken.Read(Get), EnvironmentVariables.GitToken.SetName(Get)));
    }

    [Fact]
    public async Task FR200_The_aide_alias_prints_the_deprecation_notice_to_stderr_then_behaves_identically()
    {
        _cli.LoggedIn().Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.ProjectsPage));
        var tevrin = await _cli.RunAsync("projects", "list");

        _cli.AsAlias = true;
        var aide = await _cli.RunAsync("projects", "list");

        Assert.Equal((ExitCodes.Success, tevrin.Out), (aide.ExitCode, aide.Out));
        Assert.Equal("aide is now tevrin; this alias will be removed in a later version." + Environment.NewLine + tevrin.Error, aide.Error);
        Assert.Equal(2, _cli.Requests.Count);
    }

    [Fact]
    public async Task FR200_The_alias_keeps_exit_codes_and_prints_the_notice_on_usage_errors_too()
    {
        _cli.AsAlias = true;

        var run = await _cli.RunAsync("status", "not-a-guid");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.StartsWith(CliApp.AliasDeprecationNotice, run.Error, StringComparison.Ordinal);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR200_Usage_errors_and_hints_name_the_tevrin_command()
    {
        var usage = await _cli.RunAsync("status", "not-a-guid");
        var notLoggedIn = await _cli.RunAsync("projects", "list");

        Assert.Contains("Run 'tevrin status --help' for usage.", usage.Error, StringComparison.Ordinal);
        Assert.Contains("Run 'tevrin login --token <token>'", notLoggedIn.Error, StringComparison.Ordinal);
        Assert.Contains("TEVRIN_TOKEN", notLoggedIn.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("AIDE", usage.Error + notLoggedIn.Error, StringComparison.Ordinal);
    }
}
