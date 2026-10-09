using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Infrastructure.Agents;

namespace Aide.UnitTests.Agents;

/// <summary>FR-27: reading refinement replies and checking answers.</summary>
public class RefinementRulesTests
{
    [Fact]
    public void FR27_A_reply_with_a_draft_and_three_questions_is_read()
    {
        const string reply = """
            {"projectRequest": "# Todo\n\n## Summary\nTodos.", "questions": [
              {"id": "q1", "question": "Who uses it?", "reason": "Scope"},
              {"id": "q2", "text": "Web or API?"},
              {"question": "Scale?"}]}
            """;

        var result = RefinementRules.Parse(reply);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("# Todo\n\n## Summary\nTodos.", result.Value.ProjectRequest);
        Assert.Equal(
            [new RefinementQuestion("q1", "Who uses it?", "Scope"), new RefinementQuestion("q2", "Web or API?", null), new RefinementQuestion("q3", "Scale?", null)],
            result.Value.Questions);
    }

    [Fact]
    public void FR27_A_fenced_reply_without_questions_is_read()
    {
        var result = RefinementRules.Parse("Here you go:\n```json\n{\"projectRequest\": \"# P\", \"questions\": []}\n```");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value.Questions);
    }

    [Fact]
    public void FR27_Repeated_question_ids_are_renumbered_by_position()
    {
        var result = RefinementRules.Parse("""{"projectRequest": "# P", "questions": [{"id": "a", "question": "x"}, {"id": "a", "question": "y"}]}""");

        Assert.Equal(["a", "q2"], result.Value.Questions.Select(q => q.Id));
    }

    [Theory]
    [InlineData("just prose")]
    [InlineData("{\"questions\": []}")]
    [InlineData("{\"projectRequest\": \"  \"}")]
    [InlineData("{\"projectRequest\": \"# P\", \"questions\": {}}")]
    [InlineData("{\"projectRequest\": \"# P\", \"questions\": [{\"id\": \"q1\"}]}")]
    [InlineData("{\"projectRequest\": ")]
    public void FR27_An_unreadable_reply_is_INVALID_OUTPUT(string reply) =>
        Assert.Equal(ErrorCodes.InvalidOutput, RefinementRules.Parse(reply).Error!.Code);

    [Fact]
    public void FR27_Answers_resolve_question_ids_of_the_latest_round_and_free_questions()
    {
        RefinementQuestion[] latest = [new("q1", "Who uses it?", null), new("q2", "Web or API?", null), new("q3", "Scale?", null)];

        var result = RefinementRules.ResolveAnswers(
            [new("q1", null, " Teams "), new(null, "Any deadline?", "June")],
            latest);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal([new RefinementAnswer("q1", "Who uses it?", "Teams"), new RefinementAnswer(null, "Any deadline?", "June")], result.Value);
    }

    [Fact]
    public void FR27_Bad_answers_are_VALIDATION_with_every_field()
    {
        RefinementQuestion[] latest = [new("q1", "Who?", null)];

        var result = RefinementRules.ResolveAnswers(
            [new("q9", null, "x"), new(null, null, "y"), new("q1", null, " "), new("q1", null, "again"), new("q1", null, "twice")],
            latest);

        Assert.Equal(ErrorCodes.Validation, result.Error!.Code);
        Assert.Equal(
            ["answers[0].questionId", "answers[1].questionId", "answers[2].answer", "answers[3].questionId", "answers[4].questionId"],
            result.Error.Details.Select(d => d.Split(": ", 2)[0]));
    }

    [Fact]
    public void FR27_No_answers_is_fine() =>
        Assert.Empty(RefinementRules.ResolveAnswers(null, []).Value);

    [Fact]
    public void FR27_Answers_are_formatted_for_the_prompt_and_the_run()
    {
        RefinementAnswer[] answers = [new("q1", "Who\nuses it?", "Teams"), new(null, "Deadline?", "June")];

        Assert.Equal("Q (q1): Who uses it?\nA: Teams\n\nQ: Deadline?\nA: June", RefinementRules.FormatAnswers(answers));
        Assert.Equal("Answers:\nQ (q1): Who uses it?\nA: Teams\n\nQ: Deadline?\nA: June\n\nFeedback:\nShorter", RefinementRules.RunFeedback(answers, " Shorter "));
        Assert.Null(RefinementRules.FormatAnswers([]));
        Assert.Null(RefinementRules.RunFeedback(null, null));
    }
}

/// <summary>FR-28: the eleven required specification sections.</summary>
public class SpecSectionsTests
{
    private static string Spec(params string[] skip) =>
        string.Join("\n\n", SpecSections.Required.Select((name, i) => skip.Contains(name) ? $"Text about {name}" : $"## {i + 1}. {name}\nBody."));

    [Fact]
    public void FR28_A_spec_with_all_eleven_sections_passes()
    {
        Assert.Equal(11, SpecSections.Required.Count);
        Assert.True(SpecSections.Check(Spec()).IsSuccess);
    }

    [Fact]
    public void FR28_Output_missing_Testing_Strategy_is_rejected_with_that_section()
    {
        var result = SpecSections.Check(Spec("Testing Strategy"));

        Assert.Equal(ErrorCodes.MissingSections, result.Error!.Code);
        Assert.Equal(["Testing Strategy"], result.Error.Details);
        Assert.Contains("\"Testing Strategy\"", SpecSections.CorrectiveFeedback(result.Error.Details), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("# overview")]
    [InlineData("### 1) OVERVIEW")]
    [InlineData("## **Overview** ##")]
    [InlineData("##   1.   Overview:")]
    public void FR28_Headings_match_ignoring_case_level_numbering_and_emphasis(string heading) =>
        Assert.DoesNotContain("Overview", SpecSections.Missing(heading));

    [Fact]
    public void FR28_Headings_inside_code_fences_and_plain_text_do_not_count()
    {
        Assert.Contains("Overview", SpecSections.Missing("```\n## Overview\n```"));
        Assert.Contains("Overview", SpecSections.Missing("Overview"));
        Assert.Equal(SpecSections.Required, SpecSections.Missing(string.Empty));
    }
}

/// <summary>FR-29, FR-18, D6: plan replies and the stored plan document with its canonical aide-plan block.</summary>
public class PlanDocumentTests
{
    private const string Reply = """
        {"steps": [
          {"number": 1, "title": "Skeleton", "task": "Create the `app` (FR-1).", "files": [{"path": "./src/app.ts", "action": "create"}], "dependencies": []},
          {"number": 2, "title": "API", "task": "Add routes.", "declaredFiles": [{"path": "src/app.ts", "action": "Modify"}, {"path": "src/routes.ts", "action": "create"}], "dependencies": [1], "userInstructions": "Set PORT."}
        ]}
        """;

    [Fact]
    public void FR29_A_structured_reply_is_read_validated_and_normalised()
    {
        var result = PlanDocument.ParseAgentOutput(Reply, maxFilesPerStep: 20);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(new DeclaredFile("src/app.ts", FileAction.Create), result.Value[0].Files[0]);
        Assert.Equal([new DeclaredFile("src/app.ts", FileAction.Modify), new DeclaredFile("src/routes.ts", FileAction.Create)], result.Value[1].Files);
        Assert.Equal([1], result.Value[1].Dependencies);
        Assert.Equal("Set PORT.", result.Value[1].UserInstructions);
    }

    private static readonly int[] ForwardDependency = [5];

    [Fact]
    public void FR29_Step_3_depending_on_step_5_is_INVALID_PLAN()
    {
        var steps = Enumerable.Range(1, 5).Select(n => new
        {
            number = n,
            title = $"Step {n}",
            task = "Do it",
            files = new[] { new { path = $"f{n}.txt", action = "create" } },
            dependencies = n == 3 ? ForwardDependency : Array.Empty<int>(),
        });

        var result = PlanDocument.ParseAgentOutput(JsonSerializer.Serialize(new { steps }), maxFilesPerStep: 20);

        Assert.Equal(ErrorCodes.InvalidPlan, result.Error!.Code);
        Assert.Contains("dependencies must point to earlier steps", PlanDocument.CorrectiveFeedback(result.Error), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no json here")]
    [InlineData("{\"plan\": []}")]
    [InlineData("{\"steps\": [{\"title\": \"x\"}]}")]
    [InlineData("{\"steps\": [{\"number\": 1, \"title\": \"x\", \"task\": \"y\", \"files\": [{\"path\": \"a\", \"action\": \"delete\"}]}]}")]
    [InlineData("{\"steps\": [{\"number\": 1, \"title\": \"x\", \"task\": \"y\", \"files\": [{\"path\": \"a\", \"action\": \"create\"}], \"dependencies\": [\"one\"]}]}")]
    public void FR29_Unreadable_plans_are_INVALID_PLAN(string reply) =>
        Assert.Equal(ErrorCodes.InvalidPlan, PlanDocument.ParseAgentOutput(reply, maxFilesPerStep: null).Error!.Code);

    [Fact]
    public void FR29_The_rendered_plan_round_trips_through_its_aide_plan_block()
    {
        var steps = PlanDocument.ParseAgentOutput(Reply, 20).Value;

        var markdown = PlanDocument.Render(ArtifactType.ImplementationPlan, steps);
        var parsed = PlanDocument.ParseMarkdown(markdown, 20);

        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Equal(PlanDocument.ToJson(steps), PlanDocument.ToJson(parsed.Value));
        Assert.Equal(markdown, PlanDocument.Render(ArtifactType.ImplementationPlan, parsed.Value));
        Assert.StartsWith("# Implementation plan\n", markdown, StringComparison.Ordinal);
        Assert.Contains("## Step 2: API", markdown, StringComparison.Ordinal);
        Assert.Contains("- `src/routes.ts` (create)", markdown, StringComparison.Ordinal);
        Assert.Contains("**Depends on:** step 1", markdown, StringComparison.Ordinal);
        Assert.Contains("**User instructions:** Set PORT.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void FR29_Backticks_in_step_text_never_close_the_block()
    {
        var steps = PlanDocument.ParseAgentOutput(Reply, 20).Value;
        var json = PlanDocument.ToJson(steps);

        Assert.DoesNotContain("`", json, StringComparison.Ordinal);
        Assert.Equal("Create the `app` (FR-1).", PlanDocument.ParseMarkdown(PlanDocument.Render(ArtifactType.OptimizationPlan, steps), null).Value[0].Task);
    }

    [Fact]
    public void FR18_An_edited_block_is_validated()
    {
        var markdown = PlanDocument.Render(ArtifactType.ImplementationPlan, PlanDocument.ParseAgentOutput(Reply, 20).Value)
            .Replace("\"dependencies\": [\n        1\n      ]", "\"dependencies\": [\n        2\n      ]", StringComparison.Ordinal);

        var result = PlanDocument.ParseMarkdown(markdown, 20);

        Assert.Equal(ErrorCodes.InvalidPlan, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.Contains("Step 2 depends on step 2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("# Plan\n\n1. Do things")]
    [InlineData("```aide-plan\n{\"steps\": []}\n```\n\n```aide-plan\n{\"steps\": []}\n```")]
    public void FR18_A_plan_without_exactly_one_block_is_INVALID_PLAN(string markdown) =>
        Assert.Equal(ErrorCodes.InvalidPlan, PlanDocument.ParseMarkdown(markdown, null).Error!.Code);

    [Fact]
    public void FR29_The_files_per_step_limit_applies_to_replies()
    {
        Assert.Equal(ErrorCodes.InvalidPlan, PlanDocument.ParseAgentOutput(Reply, maxFilesPerStep: 1).Error!.Code);
    }

    private static PlanStep[] OneStep(string task, string? instructions = null) =>
        [new PlanStep(1, "Docs", task, [new DeclaredFile("docs/a.md", FileAction.Create)], [], instructions)];

    [Theory]
    [InlineData("Write it:\n```aide-plan\n{\"steps\": []}\n```\nDone.")]
    [InlineData("```aide-plan\n{\"steps\": []}")]
    [InlineData("Close it early\n```\nand go on")]
    [InlineData("~~~\nTilde fence\n~~~")]
    public void FR29_A_fence_inside_a_task_never_forges_or_breaks_the_block(string task)
    {
        var steps = OneStep(task);

        var markdown = PlanDocument.RenderChecked(ArtifactType.ImplementationPlan, steps, 20);

        Assert.True(markdown.IsSuccess, markdown.Error?.Message);
        Assert.Single(markdown.Value.Split('\n'), line => line.StartsWith("```aide-plan", StringComparison.Ordinal));
        var parsed = PlanDocument.ParseMarkdown(markdown.Value, 20);
        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Equal(task, parsed.Value[0].Task);
    }

    [Fact]
    public void FR29_A_fence_inside_user_instructions_never_forges_or_breaks_the_block()
    {
        const string instructions = "Then run:\n```aide-plan\n{\"steps\": [{\"number\": 1}]}\n```";

        var markdown = PlanDocument.RenderChecked(ArtifactType.ImplementationPlan, OneStep("Task.", instructions), null);

        Assert.True(markdown.IsSuccess, markdown.Error?.Message);
        Assert.Contains("\\`\\`\\`aide-plan", markdown.Value, StringComparison.Ordinal);
        Assert.Equal(instructions, PlanDocument.ParseMarkdown(markdown.Value, null).Value[0].UserInstructions);
    }

    [Fact]
    public void FR29_Neutralise_escapes_only_fence_length_runs()
    {
        Assert.Equal("Use `code` and ``pairs``", PlanDocument.Neutralise("Use `code` and ``pairs``"));
        Assert.Equal("\\`\\`\\`\\`js", PlanDocument.Neutralise("````js"));
        Assert.Equal("\\~\\~\\~", PlanDocument.Neutralise("~~~"));
    }

    [Theory]
    [InlineData("src/a\nb.ts")]
    [InlineData("src/a`b.ts")]
    [InlineData("src/\u0001x.ts")]
    [InlineData("src/a\tb.ts")]
    public void FR29_A_path_with_a_line_break_backtick_or_control_character_is_INVALID_PLAN(string path)
    {
        var reply = JsonSerializer.Serialize(new
        {
            steps = new[] { new { number = 1, title = "A", task = "B", files = new[] { new { path, action = "create" } } } },
        });

        var result = PlanDocument.ParseAgentOutput(reply, null);

        Assert.Equal(ErrorCodes.InvalidPlan, result.Error!.Code);
        var reason = Assert.Single(result.Error.Details);
        Assert.Contains("may not contain control characters, line breaks or backticks", reason, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', reason);
    }

    [Theory]
    [InlineData(".github/workflows/x.yml")]
    [InlineData("package-lock.json")]
    [InlineData("config/.env.production")]
    [InlineData(".aide/state.json")]
    public void FR29_An_agent_plan_declaring_a_protected_path_is_INVALID_PLAN_with_the_path_as_reason(string path)
    {
        var reply = JsonSerializer.Serialize(new
        {
            steps = new[] { new { number = 1, title = "A", task = "B", files = new[] { new { path, action = "create" }, new { path = "src/ok.ts", action = "create" } } } },
        });

        var result = PlanDocument.ParseAgentOutput(reply, null);

        Assert.Equal(ErrorCodes.InvalidPlan, result.Error!.Code);
        Assert.Equal([$"Step 1 declares the protected path '{path}'; a generated plan may not create or modify protected paths (.git/, .aide/, CI workflows, lockfiles, .env files)."], result.Error.Details);
        Assert.Contains(path, PlanDocument.CorrectiveFeedback(result.Error), StringComparison.Ordinal);
    }

    [Fact]
    public void FR29_An_owner_edited_plan_declaring_a_protected_path_is_accepted()
    {
        PlanStep[] steps = [new(1, "CI", "Add CI.", [new DeclaredFile(".github/workflows/x.yml", FileAction.Create)], [])];

        var parsed = PlanDocument.ParseMarkdown(PlanDocument.Render(ArtifactType.ImplementationPlan, steps), 20);

        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Equal(".github/workflows/x.yml", parsed.Value[0].Files[0].Path);
    }
}

/// <summary>Tolerant reading of agent replies.</summary>
public class AgentOutputTests
{
    [Theory]
    [InlineData("```markdown\n## A\ntext\n```", "## A\ntext")]
    [InlineData("  ## A  ", "## A")]
    [InlineData("```\na\n```\nb\n```\nc\n```", "```\na\n```\nb\n```\nc\n```")]
    public void UnwrapFence_strips_only_a_fence_around_everything(string text, string expected) =>
        Assert.Equal(expected, AgentOutput.UnwrapFence(text));

    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("Sure! {\"a\":{\"b\":2}} Done.", "{\"a\":{\"b\":2}}")]
    [InlineData("nothing", null)]
    public void ExtractJsonObject_finds_the_object(string text, string? expected) =>
        Assert.Equal(expected, AgentOutput.ExtractJsonObject(text));
}

/// <summary>D13: the built-in Fake replies are valid for their agents (E2E and the dev seed rely on them).</summary>
public class FakeAgentResponderTests
{
    private static LlmRequest Request(AgentKind agent, string user) => new()
    {
        Model = "fake-1",
        System = "system",
        Messages = [new LlmMessage(LlmRole.User, user)],
        Agent = agent,
    };

    private const string FirstRound = "<project_name>\nDemo: Todo API\n</project_name>\n\n<idea>\nA todo list API.\n</idea>\n\n<current_draft>\n\n</current_draft>\n\n<answers>\n\n</answers>\n\n<feedback>\n\n</feedback>";

    [Fact]
    public void FR27_Fake_refinement_asks_three_questions_first_then_only_the_unanswered_ones()
    {
        var responder = new FakeIdeaRefinementResponder();

        var first = RefinementRules.Parse(responder.Respond(Request(AgentKind.IdeaRefinement, FirstRound))).Value;
        Assert.Equal(["q1", "q2", "q3"], first.Questions.Select(q => q.Id));
        Assert.StartsWith("# Demo: Todo API", first.ProjectRequest, StringComparison.Ordinal);

        var answers = RefinementRules.FormatAnswers([new("q1", first.Questions[0].Question, "Just me"), new("q2", first.Questions[1].Question, "API only")]);
        var second = RefinementRules.Parse(responder.Respond(Request(
            AgentKind.IdeaRefinement,
            FirstRound.Replace("<current_draft>\n\n</current_draft>", $"<current_draft>\n{first.ProjectRequest}\n</current_draft>", StringComparison.Ordinal)
                .Replace("<answers>\n\n</answers>", $"<answers>\n{answers}\n</answers>", StringComparison.Ordinal)))).Value;
        Assert.Equal(["q3"], second.Questions.Select(q => q.Id));
        Assert.Contains("- Clarified: " + first.Questions[0].Question + " — Just me", second.ProjectRequest, StringComparison.Ordinal);
        Assert.Contains("API only", second.ProjectRequest, StringComparison.Ordinal);

        var third = RefinementRules.Parse(responder.Respond(Request(
            AgentKind.IdeaRefinement,
            FirstRound.Replace("<current_draft>\n\n</current_draft>", $"<current_draft>\n{second.ProjectRequest}\n</current_draft>", StringComparison.Ordinal)))).Value;
        Assert.Empty(third.Questions);
        Assert.Contains("Just me", third.ProjectRequest, StringComparison.Ordinal);
    }

    [Fact]
    public void FR28_Fake_spec_has_all_eleven_sections_for_the_project()
    {
        var spec = new FakeSpecGeneratorResponder().Respond(Request(AgentKind.SpecGenerator, "<project_request>\n# Demo: Todo API\n\n## Summary\nTodos.\n</project_request>"));

        Assert.Empty(SpecSections.Missing(spec));
        Assert.Contains("GET | /api/todos", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void FR29_Fake_plan_is_a_valid_plan_for_the_project()
    {
        var reply = new FakePlanGeneratorResponder().Respond(Request(AgentKind.PlanGenerator, "<project_request>\n# Demo: Todo API\n</project_request>"));

        var plan = PlanDocument.ParseAgentOutput(reply, maxFilesPerStep: 3);

        Assert.True(plan.IsSuccess, plan.Error?.Message);
        Assert.InRange(plan.Value.Count, 3, 5);
        Assert.Contains(plan.Value.SelectMany(s => s.Files), f => f.Path == "src/routes/todos.js");
        Assert.DoesNotContain(plan.Value.SelectMany(s => s.Files), f => PathRules.IsProtected(f.Path));
    }
}
