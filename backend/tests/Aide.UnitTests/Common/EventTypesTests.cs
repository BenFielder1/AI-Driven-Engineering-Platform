using System.Reflection;
using System.Text.RegularExpressions;
using Aide.Domain.Common;

namespace Aide.UnitTests.Common;

/// <summary>FR-50: event types and the activity-feed category filter.</summary>
public partial class EventTypesTests
{
    private static readonly string[] AllTypes = [.. typeof(EventTypes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)];

    [Fact]
    public void FR50_Event_category_wire_values() =>
        Assert.Equal(
            ["transition", "gate", "artifact", "run", "step", "review", "release", "rollback", "project", "integrity"],
            EnumStrings.Values<EventCategory>());

    [Fact]
    public void FR50_Event_types_are_unique_screaming_snake()
    {
        Assert.Equal(AllTypes.Length, AllTypes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(AllTypes, t => Assert.Matches(ScreamingSnake(), t));
    }

    [Fact]
    public void FR50_Every_category_has_event_types() =>
        Assert.All(Enum.GetValues<EventCategory>(), c => Assert.NotEmpty(EventTypes.InCategory(c)));

    [Fact]
    public void FR50_Every_event_type_except_the_operations_markers_has_exactly_one_category()
    {
        string[] uncategorised = [EventTypes.DemoProjectSeeded, EventTypes.DemoPresetProjectSeeded, EventTypes.DemoStackProjectSeeded, EventTypes.DemoProjectReleaseUpgraded, EventTypes.DeadLetterRetried, EventTypes.MergeReadinessChecked];
        foreach (var type in AllTypes.Except(uncategorised))
        {
            var category = EventTypes.CategoryOf(type);
            Assert.NotNull(category);
            Assert.Single(Enum.GetValues<EventCategory>(), c => EventTypes.InCategory(c).Contains(type));
            Assert.Contains(type, EventTypes.InCategory(category.Value));
        }

        Assert.All(uncategorised, t => Assert.Null(EventTypes.CategoryOf(t)));
        Assert.Equal(AllTypes.Length - uncategorised.Length, EventTypes.AllCategorised.Count);
    }

    [Fact]
    public void FR51_Event_outcome_wire_values() =>
        Assert.Equal(["ok", "failure"], EnumStrings.Values<EventOutcome>());

    [Theory]
    [InlineData(EventTypes.RunFailed)]
    [InlineData(EventTypes.BudgetExceeded)]
    [InlineData(EventTypes.StepRejected)]
    [InlineData(EventTypes.StepBlocked)]
    [InlineData(EventTypes.GitPushFailed)]
    [InlineData(EventTypes.PullRequestFailed)]
    [InlineData(EventTypes.ReviewFailed)]
    [InlineData(EventTypes.ReleaseFailed)]
    [InlineData(EventTypes.ArtifactCorrupted)]
    [InlineData(EventTypes.IntegrityAlert)]
    [InlineData(EventTypes.ConnectionInvalid)]
    [InlineData(EventTypes.LlmBindingCleared)]
    [InlineData(EventTypes.GitBindingCleared)]
    [InlineData(EventTypes.DeploymentFailed)]
    [InlineData(EventTypes.MigrationRunFailed)]
    public void FR51_Failure_and_alert_events_have_outcome_failure(string type) =>
        Assert.Equal(EventOutcome.Failure, EventTypes.OutcomeOf(type));

    [Fact]
    public void FR51_Every_other_event_type_has_outcome_ok()
    {
        Assert.Equal(15, EventTypes.Failures.Count);
        Assert.All(AllTypes.Except(EventTypes.Failures), t => Assert.Equal(EventOutcome.Ok, EventTypes.OutcomeOf(t)));
        Assert.Equal(EventOutcome.Ok, EventTypes.OutcomeOf("NOT_AN_EVENT"));
    }

    [Theory]
    [InlineData(EventTypes.PhaseChanged, EventCategory.Transition)]
    [InlineData(EventTypes.GateApproved, EventCategory.Gate)]
    [InlineData(EventTypes.ArtifactInvalidated, EventCategory.Artifact)]
    [InlineData(EventTypes.RunFailed, EventCategory.Run)]
    [InlineData(EventTypes.StepCompleted, EventCategory.Step)]
    [InlineData(EventTypes.ReviewCompleted, EventCategory.Review)]
    [InlineData(EventTypes.ReleaseCompleted, EventCategory.Release)]
    [InlineData(EventTypes.RollbackCompleted, EventCategory.Rollback)]
    [InlineData(EventTypes.MemberAdded, EventCategory.Project)]
    [InlineData(EventTypes.IntegrityAlert, EventCategory.Integrity)]
    public void FR50_Event_type_maps_to_its_category(string type, EventCategory expected) =>
        Assert.Equal(expected, EventTypes.CategoryOf(type));

    [Fact]
    public void FR50_Unknown_event_type_has_no_category() => Assert.Null(EventTypes.CategoryOf("NOT_AN_EVENT"));

    [GeneratedRegex("^[A-Z]+(_[A-Z]+)*$")]
    private static partial Regex ScreamingSnake();
}
