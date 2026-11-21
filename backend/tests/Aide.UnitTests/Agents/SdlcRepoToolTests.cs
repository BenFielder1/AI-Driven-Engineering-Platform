using System.Text.Json;
using Aide.Api.Agents;
using Aide.Api.Pipelines;
using Aide.Infrastructure.RepoTools;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-317, FR-71: the SDLC repo tools as engine <see cref="IAgentTool"/>s have today's specs (what reaches the model) and,
/// on a fake repository, give the results of <see cref="RepoToolSession"/>, with the same call log.
/// </summary>
public sealed class SdlcRepoToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aide-sdlc-tools-tests", Guid.NewGuid().ToString("N"));

    public SdlcRepoToolTests()
    {
        Write("README.md", "# Demo\n\nHello from the demo repo.\n");
        Write("src/app.ts", "export const answer = 42;\n// TODO: more\n");
        Write("src/lib/util.ts", "export const twice = (n: number) => n * 2; // TODO\n");
        Write(".git/config", "[core]\n\tTODO-in-git\n");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Calls covering every tool, an unsafe path, a hidden path, a missing file and bad arguments.</summary>
    private static readonly (string Tool, string Args)[] Calls =
    [
        ("list_files", """{"path":"."}"""),
        ("list_files", """{"path":"src","depth":2}"""),
        ("read_file", """{"path":"README.md"}"""),
        ("read_file", """{"path":"src/app.ts","startLine":2,"endLine":2}"""),
        ("search_code", """{"query":"TODO"}"""),
        ("search_code", """{"query":"twice|answer","regex":true,"pathGlob":"*.ts"}"""),
        ("read_file", """{"path":"../outside.txt"}"""),
        ("read_file", """{"path":".git/config"}"""),
        ("read_file", """{"path":"missing.md"}"""),
        ("list_files", """{"depth":"deep"}"""),
    ];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static List<IAgentTool> Tools(SdlcRepoToolScope scope) =>
        [new SdlcListFilesTool(scope), new SdlcReadFileTool(scope), new SdlcSearchCodeTool(scope)];

    [Fact]
    public void FR317_tool_specs_equal_todays_definitions_in_order()
    {
        using var scope = new SdlcRepoToolScope(new FakeOpener());

        var specs = Tools(scope).Select(t => t.Spec).ToList();

        Assert.Equal(RepoToolDefinitions.All.Count, specs.Count);
        foreach (var (spec, definition) in specs.Zip(RepoToolDefinitions.All))
        {
            Assert.Equal(definition.Name, spec.Name);
            Assert.Equal(definition.Description, spec.Description);
            Assert.Equal(definition.InputSchema.GetRawText(), spec.InputSchema.GetRawText());
        }
    }

    [Fact]
    public void FR317_the_engine_executor_offers_the_model_todays_definitions()
    {
        using var scope = new SdlcRepoToolScope(new FakeOpener());

        var offered = new AgentToolExecutor(Tools(scope)).Definitions;

        Assert.Equal(
            RepoToolDefinitions.All.Select(d => (d.Name, d.Description, d.InputSchema.GetRawText())),
            offered.Select(d => (d.Name, d.Description, d.InputSchema.GetRawText())));
    }

    [Fact]
    public async Task FR317_tool_calls_give_the_session_results_and_log()
    {
        var today = new RepoToolSession(_root);
        using var scope = new SdlcRepoToolScope(new FakeOpener());
        scope.Use(new RepoToolSession(_root));
        var tools = Tools(scope).ToDictionary(t => t.Spec.Name, StringComparer.Ordinal);

        foreach (var (tool, args) in Calls)
        {
            var expected = await today.ExecuteAsync(new LlmToolCall("c1", tool, args), Ct);
            using var parsed = JsonDocument.Parse(args);

            var actual = await tools[tool].InvokeAsync(parsed.RootElement.Clone(), Ct);

            Assert.Equal((expected.Content, expected.IsError), (actual.Content, actual.IsError));
        }

        Assert.Equal(today.ToJson(), scope.ToolCallsJson());
        Assert.Contains(today.Calls, c => c.Error is not null);
    }

    [Fact]
    public async Task FR317_tool_calls_through_the_engine_executor_give_the_session_results()
    {
        var today = new RepoToolSession(_root);
        using var scope = new SdlcRepoToolScope(new FakeOpener());
        scope.Use(new RepoToolSession(_root));
        var executor = new AgentToolExecutor(Tools(scope));

        foreach (var (tool, args) in Calls)
        {
            var expected = await today.ExecuteAsync(new LlmToolCall("c1", tool, args), Ct);

            var actual = await executor.ExecuteAsync(new LlmToolCall("c1", tool, args), Ct);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task FR317_without_a_snapshot_a_call_is_an_error_result()
    {
        var opener = new FakeOpener();
        using var scope = new SdlcRepoToolScope(opener);
        scope.Prepare(Guid.NewGuid(), "unknown_agent");
        using var args = JsonDocument.Parse("""{"path":"."}""");

        var result = await new SdlcListFilesTool(scope).InvokeAsync(args.RootElement.Clone(), Ct);

        Assert.Equal(new ToolResult(SdlcRepoTool.NoSnapshotMessage, IsError: true), result);
        Assert.Null(scope.ToolCallsJson());
        Assert.Equal(0, opener.Opened);
    }

    [Fact]
    public async Task FR317_preparing_an_attempt_opens_no_snapshot()
    {
        var opener = new FakeOpener();
        using var scope = new SdlcRepoToolScope(opener);
        var provider = new SdlcRepoContextProvider(scope);

        var sections = await provider.GetContextAsync(new AgentContextRequest(Guid.NewGuid(), Guid.NewGuid(), SdlcPack.PipelineId, "execution", SdlcAgents.CodeGenerator), Ct);

        Assert.Empty(sections);
        Assert.Equal(0, opener.Opened);
        Assert.False(scope.IsOpen);
        Assert.Null(scope.Warning);
    }

    [Fact]
    public async Task FR317_the_snapshot_opens_once_on_first_use_with_the_agents_scope_and_keeps_its_warning()
    {
        var opener = new FakeOpener();
        var projectId = Guid.NewGuid();
        using var scope = new SdlcRepoToolScope(opener);
        scope.Prepare(projectId, SdlcAgents.PlanGenerator);
        using var args = JsonDocument.Parse("""{"path":"."}""");

        var first = await new SdlcListFilesTool(scope).InvokeAsync(args.RootElement.Clone(), Ct);
        var second = await new SdlcReadFileTool(scope).InvokeAsync(args.RootElement.Clone(), Ct);
        await scope.OpenAsync(Ct);

        Assert.Equal(1, opener.Opened);
        Assert.Equal((projectId, RepoScope.BaseCommit), opener.Last);
        Assert.Equal(AgentRunWarnings.SnapshotUnavailable, scope.Warning);
        Assert.True(first.IsError && second.IsError);
    }

    [Fact]
    public async Task FR317_release_keeps_the_log_and_a_new_attempt_starts_clean()
    {
        var opener = new FakeOpener();
        using var scope = new SdlcRepoToolScope(opener);
        var session = new RepoToolSession(_root);
        scope.Use(session);
        using var args = JsonDocument.Parse("""{"path":"README.md"}""");
        await new SdlcReadFileTool(scope).InvokeAsync(args.RootElement.Clone(), Ct);

        scope.Release();

        Assert.False(scope.IsOpen);
        Assert.Equal(session.ToJson(), scope.ToolCallsJson());
        Assert.True((await new SdlcReadFileTool(scope).InvokeAsync(args.RootElement.Clone(), Ct)).IsError);
        Assert.Single(session.Calls);

        // The caller's session isn't owned: it still works after the scope let it go.
        Assert.False((await session.ExecuteAsync(new LlmToolCall("c2", "read_file", """{"path":"README.md"}"""), Ct)).IsError);

        scope.Prepare(Guid.NewGuid(), SdlcAgents.BuildFix);
        Assert.Null(scope.ToolCallsJson());
        Assert.Equal(0, opener.Opened);
    }

    [Theory]
    [InlineData(SdlcAgents.RepositoryAnalyst, RepoScope.BaseCommit)]
    [InlineData(SdlcAgents.StackAnalyst, RepoScope.BaseCommit)]
    [InlineData(SdlcAgents.IdeaRefinement, RepoScope.BaseCommit)]
    [InlineData(SdlcAgents.SpecGenerator, RepoScope.BaseCommit)]
    [InlineData(SdlcAgents.PlanGenerator, RepoScope.BaseCommit)]
    [InlineData(SdlcAgents.CodeGenerator, RepoScope.WorkingTree)]
    [InlineData(SdlcAgents.CodeReview, RepoScope.WorkingTree)]
    [InlineData(SdlcAgents.BuildFix, RepoScope.WorkingTree)]
    [InlineData("unknown_agent", RepoScope.None)]
    public void FR317_each_agent_sees_the_repository_its_handler_chose(string agent, RepoScope scope) =>
        Assert.Equal(scope, SdlcRepoToolScope.ScopeOf(agent));

    [Fact]
    public void FR317_the_tools_and_context_providers_are_registered_for_the_engine()
    {
        var services = new ServiceCollection().AddSdlcPipeline().AddSdlcAgentExtensions();
        services.AddScoped<IRepoToolOpener>(_ => new FakeOpener());
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<PipelineRegistry>();

        Assert.Equal(
            [typeof(SdlcListFilesTool), typeof(SdlcReadFileTool), typeof(SdlcSearchCodeTool)],
            SdlcToolNames.All.Select(name => registry.ImplementationOf(PipelineComponentKind.Tool, name)));
        using var scope = provider.CreateScope();
        Assert.Equal(
            RepoToolDefinitions.All.Select(d => d.Name),
            SdlcToolNames.All.Select(name => scope.ServiceProvider.GetPipelineTool(name)!.Spec.Name));
        Assert.Equal(
            [typeof(SdlcProjectContextProvider), typeof(SdlcIterationContextProvider), typeof(SdlcRepoContextProvider)],
            services.Where(d => d.ServiceType == typeof(IAgentContextProvider)).Select(d => d.ImplementationType));
    }

    /// <summary>Counts opens; every snapshot is unavailable (a real one needs a Git working copy).</summary>
    private sealed class FakeOpener : IRepoToolOpener
    {
        public int Opened { get; private set; }

        public (Guid ProjectId, RepoScope Scope)? Last { get; private set; }

        public Task<RepoToolContext> OpenAsync(Guid projectId, RepoScope scope, CancellationToken cancellationToken)
        {
            Opened++;
            Last = (projectId, scope);
            return Task.FromResult(new RepoToolContext(null, AgentRunWarnings.SnapshotUnavailable));
        }
    }
}
