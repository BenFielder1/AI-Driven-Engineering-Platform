using System.Text.Json;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-356, §4.69: conversation turn order, stop checks, replies, the transcript and placeholders, on <c>fixture-callsim</c>.</summary>
public class ConversationRulesTests
{
    private static PipelineDefinition CallSim
    {
        get
        {
            var registry = new PipelineRegistry([new FlowFixturePack(FlowFixturePack.CallSim)], [], Array.Empty<JobHandlerRegistration>());
            Assert.True(registry.TryGet(FlowFixturePack.CallSim, out var loaded), string.Join("; ", registry.Errors.Select(e => e.Error.ToString())));
            return loaded.Definition!;
        }
    }

    private static StageDefinition Call => CallSim.FindStage("call")!;

    private static readonly string[] Roles = ["customer", "agent", "manager", "a4", "a5", "a6"];

    private static ConversationDefinition Conversation(string opener = "customer", int maxTurns = 6, ConversationStop? stop = null, int participants = 2) =>
        new([.. Roles.Take(participants).Select(r => new ConversationParticipant(r + "-sim", r))], opener)
        {
            TurnLimit = maxTurns,
            Stop = stop,
        };

    private static ConversationTurn Turn(string role, string text) => new(role, role + "-sim", text, Guid.CreateVersion7());

    // ---- turn order ----

    [Fact]
    public void FR356_turns_start_with_the_opener_then_cycle_through_the_participants_in_declared_order()
    {
        var conversation = Conversation(participants: 3);

        Assert.Equal(
            ["customer", "agent", "manager", "customer", "agent", "manager", "customer"],
            Enumerable.Range(1, 7).Select(t => ConversationRules.SpeakerOf(conversation, t).Role));
    }

    [Fact]
    public void FR356_an_opener_declared_later_starts_and_the_order_wraps_around_from_it()
    {
        var conversation = Conversation(opener: "agent", participants: 3);

        Assert.Equal(
            ["agent", "manager", "customer", "agent", "manager", "customer"],
            Enumerable.Range(1, 6).Select(t => ConversationRules.SpeakerOf(conversation, t).Role));
        Assert.Equal("agent-sim", ConversationRules.SpeakerOf(conversation, 1).Agent);
    }

    [Fact]
    public void FR356_the_fixture_alternates_customer_and_agent_from_the_customer()
    {
        var conversation = Call.Conversation!;

        Assert.Equal(
            ["customer-sim", "support-sim", "customer-sim", "support-sim"],
            Enumerable.Range(1, 4).Select(t => ConversationRules.SpeakerOf(conversation, t).Agent));
    }

    [Fact]
    public void FR356_turns_are_1_based() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ConversationRules.SpeakerOf(Conversation(), 0));

    // ---- stop checks ----

    [Theory]
    [InlineData("Goodbye, and thanks!", true)]
    [InlineData("OK. GOODBYE.", true)]
    [InlineData("good bye", false)]
    [InlineData("Is there anything else?", false)]
    public void FR356_the_stop_phrase_is_found_case_insensitively(string reply, bool found) =>
        Assert.Equal(found, ConversationRules.PhraseIn(new ConversationStop(null, "goodbye"), reply));

    [Fact]
    public void FR356_without_a_phrase_no_reply_stops_by_phrase() =>
        Assert.False(ConversationRules.PhraseIn(new ConversationStop("flow.call-resolved", null), "goodbye"));

    [Fact]
    public void FR356_section_4_69_the_checks_run_phrase_then_guard_then_max_turns()
    {
        var conversation = Conversation(maxTurns: 6, stop: new ConversationStop("flow.call-resolved", null));

        // All three hold at turn 6: the phrase wins, then the guard, then the limit.
        Assert.Equal(ConversationStopReason.Phrase, ConversationRules.StopAfter(conversation, 6, phraseFound: true, guardPassed: true));
        Assert.Equal(ConversationStopReason.Guard, ConversationRules.StopAfter(conversation, 6, phraseFound: false, guardPassed: true));
        Assert.Equal(ConversationStopReason.MaxTurns, ConversationRules.StopAfter(conversation, 6, phraseFound: false, guardPassed: false));
        Assert.Null(ConversationRules.StopAfter(conversation, 5, phraseFound: false, guardPassed: false));
    }

    [Fact]
    public void FR356_the_guard_is_asked_only_when_declared_and_the_phrase_did_not_stop_the_conversation()
    {
        var guarded = Conversation(stop: new ConversationStop("flow.call-resolved", null));

        Assert.True(ConversationRules.NeedsGuard(guarded, phraseFound: false));
        Assert.False(ConversationRules.NeedsGuard(guarded, phraseFound: true));
        Assert.False(ConversationRules.NeedsGuard(Conversation(stop: new ConversationStop(null, "goodbye")), phraseFound: false));
        Assert.False(ConversationRules.NeedsGuard(Conversation(), phraseFound: false));

        // A guard result without a declared guard never stops the conversation.
        Assert.Null(ConversationRules.StopAfter(Conversation(), 2, phraseFound: false, guardPassed: true));
    }

    [Fact]
    public void FR356_section_4_69_example_two_participants_max_6_phrase_goodbye_stops_at_turn_4()
    {
        var conversation = Call.Conversation!;
        string[] replies = ["My router is down.", "Have you restarted it?", "Yes, it works now.", "Great. Goodbye!", "unused", "unused"];

        var stoppedAt = 0;
        ConversationStopReason? reason = null;
        for (var turn = 1; turn <= conversation.TurnLimit && reason is null; turn++)
        {
            var phrase = ConversationRules.PhraseIn(conversation.Stop, replies[turn - 1]);
            reason = ConversationRules.StopAfter(conversation, turn, phrase, guardPassed: false);
            stoppedAt = turn;
        }

        Assert.Equal((4, ConversationStopReason.Phrase), (stoppedAt, reason));
        Assert.Equal("agent", ConversationRules.SpeakerOf(conversation, 4).Role);
    }

    [Fact]
    public void FR356_without_the_phrase_the_fixture_stops_at_max_turns_6()
    {
        var conversation = Call.Conversation!;

        Assert.Null(ConversationRules.StopAfter(conversation, 5, phraseFound: false, guardPassed: false));
        Assert.Equal(ConversationStopReason.MaxTurns, ConversationRules.StopAfter(conversation, 6, phraseFound: false, guardPassed: false));
    }

    // ---- replies and the transcript ----

    [Theory]
    [InlineData("  Hello there.  ", "Hello there.")]
    [InlineData("{\"text\": \" Hi, how can I help? \"}", "Hi, how can I help?")]
    [InlineData("{\"reply\": \"no text field\"}", "{\"reply\": \"no text field\"}")]
    [InlineData("{not json", "{not json")]
    [InlineData(null, "")]
    public void FR356_a_reply_is_the_text_as_is_or_the_text_field_of_a_json_object(string? output, string reply) =>
        Assert.Equal(reply, ConversationRules.ReadReply(output));

    [Fact]
    public void FR356_the_transcript_artifact_is_turns_of_role_agentKey_text_and_agentRunId()
    {
        var turns = new[] { Turn("customer", "Hello"), Turn("agent", "Hi") };

        using var doc = JsonDocument.Parse(ConversationRules.TranscriptJson(turns));
        var items = doc.RootElement.GetProperty("turns").EnumerateArray().ToList();

        Assert.Equal(["turns"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, items.Count);
        Assert.Equal(["role", "agentKey", "text", "agentRunId"], items[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("customer", items[0].GetProperty("role").GetString());
        Assert.Equal("agent-sim", items[1].GetProperty("agentKey").GetString());
        Assert.Equal("Hi", items[1].GetProperty("text").GetString());
        Assert.Equal(turns[1].AgentRunId, items[1].GetProperty("agentRunId").GetGuid());
    }

    [Fact]
    public void FR356_the_prompt_transcript_heads_each_turn_with_its_number_and_role()
    {
        Assert.Equal(ConversationRules.EmptyTranscript, ConversationRules.RenderTranscript([]));
        Assert.Equal(
            "### turn 1 (customer)\nHello\n\n### turn 2 (agent)\nHi",
            ConversationRules.RenderTranscript([Turn("customer", "Hello"), Turn("agent", "Hi")]));
    }

    // ---- placeholders and validation ----

    [Fact]
    public void FR356_participants_get_TRANSCRIPT_ROLE_and_the_stage_inputs_and_no_one_else_does()
    {
        string[] expected = [AgentPromptPlaceholders.Transcript, AgentPromptPlaceholders.Role, "SCENARIO"];

        Assert.Equal(expected, ConversationRules.SuppliedPlaceholders(Call, "customer-sim"));
        Assert.Equal(expected, ConversationRules.SuppliedPlaceholders(Call, "support-sim"));
        Assert.Empty(ConversationRules.SuppliedPlaceholders(Call, "someone-else"));
        Assert.Empty(ConversationRules.SuppliedPlaceholders(Call with { Conversation = null }, "customer-sim"));
    }

    [Fact]
    public void FR356_a_participant_is_matched_by_agent_and_role()
    {
        Assert.Equal("customer", ConversationRules.ParticipantOf(Call, "customer-sim", "customer")?.Role);
        Assert.Null(ConversationRules.ParticipantOf(Call, "customer-sim", "agent"));
        Assert.Null(ConversationRules.ParticipantOf(Call, "nobody", "customer"));
    }

    [Fact]
    public void FR356_the_driver_runs_a_conversation_stage_after_its_entry_checks()
    {
        Assert.IsType<DriverStep.RunConversation>(RunDriverRules.Plan(Call, new StageEntryFacts()));
        Assert.IsType<DriverStep.WaitOnInput>(RunDriverRules.Plan(Call, new StageEntryFacts { MissingInputs = ["scenario"] }));
    }

    private static PackCatalog PromptCatalog(string customerPrompt) => FlowFixturePack.Catalog with
    {
        Prompts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["flow.callsim-customer"] = customerPrompt,
            ["flow.callsim-support"] = FlowFixturePack.CallSimSupportPrompt,
        },
    };

    [Fact]
    public void FR356_the_fixture_and_its_guard_stop_edit_validate_with_the_real_prompts()
    {
        var catalog = PromptCatalog(FlowFixturePack.CallSimCustomerPrompt);
        var yaml = FlowFixturePack.Yaml(FlowFixturePack.CallSim);
        var guard = DefinitionYaml.Edit("stop: { phrase: goodbye }", $"stop: {{ guard: {FlowFixturePack.CallResolvedGuard} }}", yaml);

        Assert.Empty(DefinitionYaml.Errors(yaml, catalog));
        Assert.Empty(DefinitionYaml.Errors(guard, catalog));
    }

    [Fact]
    public void FR356_TRANSCRIPT_is_refused_for_an_agent_that_is_not_a_participant()
    {
        var catalog = FlowFixturePack.Catalog with
        {
            Prompts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["flow.callsim-customer"] = FlowFixturePack.CallSimCustomerPrompt,
                ["flow.callsim-support"] = FlowFixturePack.CallSimSupportPrompt,
                ["flow.qa-score"] = "---\nversion: flow.qa-score-1\n---\nYou score calls.\n<!-- user -->\n{{TRANSCRIPT}}\n",
            },
        };
        var yaml = DefinitionYaml.Edit(
            "      - { key: support-sim, job_type: flow.callsim-support, prompt: flow.callsim-support }\n",
            "      - { key: support-sim, job_type: flow.callsim-support, prompt: flow.callsim-support }\n      - { key: scorer, job_type: flow.qa-score, prompt: flow.qa-score }\n",
            FlowFixturePack.Yaml(FlowFixturePack.CallSim));

        Assert.Equal([(DefinitionErrorCodes.UnknownPrompt, "stages[0].agents[2].prompt")], DefinitionYaml.Errors(yaml, catalog));
    }
}
