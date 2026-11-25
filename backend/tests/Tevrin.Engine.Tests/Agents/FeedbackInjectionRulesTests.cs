using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>FR-351, §4.75: which feedback entries an agent's prompt gets, in what order, and how they are rendered.</summary>
public class FeedbackInjectionRulesTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);

    private static FeedbackEntry Entry(int day, string text, string source = "human", bool deleted = false) =>
        new(Guid.CreateVersion7(), source, text, Day.AddDays(day), deleted);

    [Fact]
    public void FR351_each_entry_is_rendered_with_its_date_and_source() =>
        Assert.Equal("- [2026-11-21 stage] Cite sources.", FeedbackInjectionRules.Format(Entry(1, "Cite sources.", "stage")));

    [Fact]
    public void FR351_entries_are_newest_first()
    {
        var old = Entry(0, "Old.");
        var mid = Entry(1, "Mid.");
        var recent = Entry(2, "New.");

        var selection = FeedbackInjectionRules.Select([mid, old, recent], 10);

        Assert.Equal([recent.Id, mid.Id, old.Id], selection.Ids);
        Assert.Equal("- [2026-11-22 human] New.\n- [2026-11-21 human] Mid.\n- [2026-11-20 human] Old.", selection.Rendered);
    }

    [Fact]
    public void FR351_at_most_limit_entries_are_taken_the_newest()
    {
        var entries = Enumerable.Range(0, 12).Select(i => Entry(i, $"Lesson {i}.")).ToList();

        var selection = FeedbackInjectionRules.Select(entries, FeedbackInjectionRules.DefaultLimit);

        Assert.Equal(10, selection.Ids.Count);
        Assert.Equal(entries[11].Id, selection.Ids[0]);
        Assert.DoesNotContain(entries[0].Id, selection.Ids);
        Assert.DoesNotContain(entries[1].Id, selection.Ids);
        Assert.Single(FeedbackInjectionRules.Select(entries, 1).Ids);
    }

    [Fact]
    public void FR351_the_limit_is_capped_at_50()
    {
        var entries = Enumerable.Range(0, 60).Select(i => Entry(i, "x")).ToList();

        Assert.Equal(50, FeedbackInjectionRules.Select(entries, 99).Ids.Count);
    }

    [Fact]
    public void FR351_deleted_entries_are_never_injected()
    {
        var kept = Entry(0, "Kept.");
        var deleted = Entry(1, "Deleted.", deleted: true);

        var selection = FeedbackInjectionRules.Select([kept, deleted], 10);

        Assert.Equal([kept.Id], selection.Ids);
        Assert.DoesNotContain("Deleted.", selection.Rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FR351_the_total_is_capped_at_4000_characters_dropping_the_oldest_first()
    {
        // Three entries of 1,900 characters each (plus the 21-character prefix): two fit in 4,000, the oldest is dropped.
        var text = new string('a', 1900);
        var oldest = Entry(0, text);
        var middle = Entry(1, text);
        var newest = Entry(2, text);

        var selection = FeedbackInjectionRules.Select([oldest, middle, newest], 10);

        Assert.Equal([newest.Id, middle.Id], selection.Ids);
        Assert.True(selection.Rendered.Length <= FeedbackInjectionRules.MaxTotalCharacters);
    }

    [Fact]
    public void FR351_entries_that_fill_exactly_4000_characters_are_all_kept()
    {
        // Lines of 1,999 and 2,000 characters plus the newline: 1,999 + 1 + 2,000 = 4,000.
        const int Prefix = 21; // "- [2026-11-20 human] "
        var newer = Entry(1, new string('b', 1999 - Prefix));
        var older = Entry(0, new string('c', 2000 - Prefix));

        var selection = FeedbackInjectionRules.Select([older, newer], 10);

        Assert.Equal([newer.Id, older.Id], selection.Ids);
        Assert.Equal(FeedbackInjectionRules.MaxTotalCharacters, selection.Rendered.Length);
    }

    [Fact]
    public void FR351_no_entries_render_nothing()
    {
        var selection = FeedbackInjectionRules.Select([], 10);

        Assert.Empty(selection.Ids);
        Assert.Equal(string.Empty, selection.Rendered);
    }

    [Fact]
    public void FR351_stage_texts_skip_blank_items_and_are_cut_to_2000_characters()
    {
        var texts = FeedbackInjectionRules.Texts(["  Cite sources. ", null, " ", new string('x', 2100)]);

        Assert.Equal(2, texts.Count);
        Assert.Equal("Cite sources.", texts[0]);
        Assert.Equal(FeedbackInjectionRules.MaxTextLength, texts[1].Length);
    }
}
