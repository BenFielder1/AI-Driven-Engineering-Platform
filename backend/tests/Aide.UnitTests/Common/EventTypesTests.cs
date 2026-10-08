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
    public void FR50_Every_event_type_except_the_seed_marker_has_exactly_one_category()
    {
        foreach (var type in AllTypes.Where(t => t != EventTypes.DemoProjectSeeded))
        {
            var category = EventTypes.CategoryOf(type);
            Assert.NotNull(category);
            Assert.Single(Enum.GetValues<EventCategory>(), c => EventTypes.InCategory(c).Contains(type));
            Assert.Contains(type, EventTypes.InCategory(category.Value));
        }

        Assert.Null(EventTypes.CategoryOf(EventTypes.DemoProjectSeeded));
        Assert.Equal(AllTypes.Length - 1, EventTypes.AllCategorised.Count);
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
