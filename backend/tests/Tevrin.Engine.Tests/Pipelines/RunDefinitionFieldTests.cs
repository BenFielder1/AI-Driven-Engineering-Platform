using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-287 (D-0.7.0-27 and the M58 message fields): <c>enqueue_on_enter</c>, <c>status_changes.*.reason</c> and
/// <c>error_message</c>, and <c>loop.title</c> load, validate and count in the hash.
/// </summary>
public class RunDefinitionFieldTests
{
    private static PipelineDefinition Parse(string yaml)
    {
        var result = PipelineDefinitionLoader.Parse(yaml);
        Assert.Empty(result.Errors);
        return result.Definition!;
    }

    [Fact]
    public void FR287_enqueue_on_enter_loads_and_is_absent_by_default()
    {
        var definition = Parse(Edit("    title: Summarise\n", "    title: Summarise\n    enqueue_on_enter: agent.run\n"));

        Assert.Equal("agent.run", definition.FindStage("summarise")!.EnqueueOnEnter);
        Assert.Null(definition.FindStage("check")!.EnqueueOnEnter);
        Assert.Empty(Errors(Edit("    title: Summarise\n", "    title: Summarise\n    enqueue_on_enter: agent.run\n")));
    }

    [Fact]
    public void FR287_DEF_UNKNOWN_JOB_TYPE_an_unregistered_enqueue_on_enter() =>
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownJobType, "stages[2].enqueue_on_enter")],
            Errors(Edit("    title: Summarise\n", "    title: Summarise\n    enqueue_on_enter: nobody.handles\n")));

    [Fact]
    public void FR287_enqueue_on_enter_changes_the_hash() =>
        Assert.NotEqual(
            DefinitionHash.Compute(Parse(Minimal)),
            DefinitionHash.Compute(Parse(Edit("    title: Summarise\n", "    title: Summarise\n    enqueue_on_enter: agent.run\n"))));

    [Fact]
    public void FR287_status_change_reason_and_error_message_load_with_defaults()
    {
        var defaulted = Parse(Minimal).StatusChanges[0];
        var custom = Parse(Edit("error_code: NOT_WORKING }", "error_code: NOT_WORKING, reason: Done at last, error_message: \"Not now ({status}).\" }")).StatusChanges[0];

        Assert.Equal(("Finish", StatusChangeDefinition.DefaultErrorMessage), (defaulted.EffectiveReason, defaulted.EffectiveErrorMessage));
        Assert.Equal(("Done at last", "Not now ({status})."), (custom.EffectiveReason, custom.EffectiveErrorMessage));
    }

    [Fact]
    public void FR287_writing_the_default_reason_or_loop_title_out_does_not_change_the_hash()
    {
        var loop = "    loop: { to: draft, on: redo, max_iterations: 2 }\n";
        var withLoop = Edit("    on_invalid: { retry_with_feedback: true, max_attempts: 3, then: escalate }\n",
            "    on_invalid: { retry_with_feedback: true, max_attempts: 3, then: escalate }\n" + loop);
        var explicitDefaults = Edit("error_code: NOT_WORKING }", "error_code: NOT_WORKING, reason: Finish }", withLoop)
            .Replace("{ to: draft,", "{ title: Loop, to: draft,", StringComparison.Ordinal);
        var titled = withLoop.Replace("{ to: draft,", "{ title: Redraft, to: draft,", StringComparison.Ordinal);

        Assert.Equal(DefinitionHash.Compute(Parse(withLoop)), DefinitionHash.Compute(Parse(explicitDefaults)));
        Assert.NotEqual(DefinitionHash.Compute(Parse(withLoop)), DefinitionHash.Compute(Parse(titled)));
        Assert.Equal("Redraft", Parse(titled).FindStage("check")!.Loop!.EffectiveTitle);
        Assert.Equal(LoopDefinition.DefaultTitle, Parse(withLoop).FindStage("check")!.Loop!.EffectiveTitle);
    }

    [Fact]
    public void FR287_the_new_fields_round_trip_through_canonical_json()
    {
        var definition = Parse(Edit("error_code: NOT_WORKING }", "error_code: NOT_WORKING, reason: Done, error_message: \"No.\" }",
            Edit("    title: Summarise\n", "    title: Summarise\n    enqueue_on_enter: agent.run\n")));

        var reread = PipelineDefinitionLoader.Parse(DefinitionHash.CanonicalJson(definition));

        Assert.Empty(reread.Errors);
        Assert.Equal(DefinitionHash.Compute(definition), DefinitionHash.Compute(reread.Definition!));
        Assert.Equal("agent.run", reread.Definition!.FindStage("summarise")!.EnqueueOnEnter);
        Assert.Equal(("Done", "No."), (reread.Definition.StatusChanges[0].Reason, reread.Definition.StatusChanges[0].ErrorMessage));
    }

    [Fact]
    public void FR287_humanise_turns_a_key_into_words() =>
        Assert.Equal(("Release completed", "Merge poll"), (KeyWords.Humanise("release_completed"), KeyWords.Humanise("merge-poll")));
}
