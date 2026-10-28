using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Specs;

namespace Aide.UnitTests.Specs;

/// <summary>FR-75 (requirement IDs and Given/When/Then criteria, D-V2-13), §4.13 (required sections, iteration tags).</summary>
public class SpecValidationTests
{
    private const string Login = "### FR-1 Sign in\nUsers sign in with email.\n- Given a registered user, when they submit valid credentials, then they see their dashboard.";
    private const string Export = "### FR-2 Export\n- Given a list, when the user clicks Export,\n  then a CSV downloads.";

    /// <summary>A spec with every section of <paramref name="sections"/>; "Functional Requirements" holds <paramref name="requirements"/>.</summary>
    private static string Spec(string requirements, IEnumerable<string>? sections = null, params string[] skip) =>
        string.Join("\n\n", (sections ?? SpecSections.Required)
            .Where(s => !skip.Contains(s))
            .Select((name, i) => name == "Functional Requirements" ? $"## {i + 1}. {name}\n\n{requirements}" : $"## {i + 1}. {name}\nBody of {name}."));

    private static string Iteration(string requirements, params string[] skip) =>
        Spec(requirements, SpecSections.RequiredFor(isIteration: true), skip);

    // ---------- Required sections (§4.13) ----------

    [Fact]
    public void S4_13_Required_sections_are_v1_eleven_plus_preset_sections_plus_changes_for_iterations()
    {
        Assert.Equal(SpecSections.Required, SpecSections.RequiredFor(isIteration: false));
        Assert.Equal([.. SpecSections.Required, "Changes in this version"], SpecSections.RequiredFor(isIteration: true));
        Assert.Equal(
            [.. SpecSections.Required, "Data Model & RLS Policies", "Environment Variables", "Changes in this version"],
            SpecSections.RequiredFor(isIteration: true, ["Data Model & RLS Policies", "Environment Variables", "  ", "overview"]));
    }

    [Fact]
    public void S4_13_A_first_version_spec_with_ids_and_criteria_is_valid_and_needs_no_tags()
    {
        var result = SpecValidator.Validate(Spec(Login + "\n\n" + Export), SpecValidationContext.FirstVersion);

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Equal(["FR-1", "FR-2"], result.Scan.Ids);
        Assert.Equal("Sign in", result.Requirements[0].Title);
        Assert.All(result.Requirements, r => Assert.True(r.HasAcceptanceCriterion));
    }

    [Fact]
    public void S4_13_An_iteration_without_Changes_in_this_version_is_MISSING_SECTIONS_and_lists_every_problem()
    {
        var spec = Iteration("### FR-1 [new] Sign in\nNo criterion here.", "Changes in this version");

        var result = SpecValidator.Validate(spec, SpecValidationContext.ImportFirstVersion());

        Assert.Equal(["Changes in this version"], result.MissingSections);
        Assert.Equal(ErrorCodes.MissingSections, result.Error!.Code);
        Assert.Contains("Missing section: \"Changes in this version\".", result.Error.Details);
        Assert.Contains("FR-1 has no Given/When/Then acceptance criterion.", result.Error.Details);
    }

    [Fact]
    public void FR75_A_preset_section_is_required_and_named_in_the_corrective_feedback()
    {
        var context = new SpecValidationContext { StackSections = ["Data Model & RLS Policies"] };

        var result = SpecValidator.Validate(Spec(Login), context);

        Assert.Equal(ErrorCodes.MissingSections, result.Error!.Code);
        var feedback = SpecValidator.CorrectiveFeedback(result.Error, context);
        Assert.Contains("Missing section: \"Data Model & RLS Policies\".", feedback, StringComparison.Ordinal);
        Assert.Contains("\"Data Model & RLS Policies\"", feedback, StringComparison.Ordinal);
    }

    // ---------- Requirement IDs and criteria (FR-75) ----------

    [Theory]
    [InlineData("### FR-3 Export\nGiven a list, when exported, then a CSV downloads.")]
    [InlineData("- **FR-3** Export: given a list, when exported, then a CSV downloads.")]
    [InlineData("1. FR-3 — Export\n   - Given a list\n   - When exported\n   - Then a CSV downloads")]
    [InlineData("**FR-3** Export\n\nGIVEN a list WHEN exported THEN a CSV downloads.")]
    [InlineData("### 3.1 FR-3 Export\n#### Acceptance criteria\n- Given a list, when exported, then a CSV downloads.")]
    [InlineData("### FR-3 Export\n```gherkin\nGiven a list\nWhen exported\nThen a CSV downloads\n```")]
    public void FR75_Requirements_are_found_on_heading_list_and_paragraph_lines_with_criteria_spanning_lines(string requirement)
    {
        var scan = RequirementParser.Parse(Spec(requirement));

        var found = Assert.Single(scan.Requirements);
        Assert.Equal(("FR-3", 3), (found.Id, found.Number));
        Assert.StartsWith("Export", found.Title, StringComparison.Ordinal);
        Assert.True(found.HasAcceptanceCriterion);
        Assert.Empty(scan.EntriesWithoutId);
    }

    [Fact]
    public void FR75_A_requirement_without_an_id_or_criterion_is_MISSING_REQUIREMENT_IDS_listing_offenders()
    {
        var requirements = """
            Intro paragraph about the requirements.

            - **FR-1** Sign in: given a user, when they sign in, then they see the dashboard.
              - Given a locked account, when they sign in, then they see an error.
            - Users can reset their password.
            - **FR-2** Export to CSV.
            """;

        var result = SpecValidator.Validate(Spec(requirements), SpecValidationContext.FirstVersion);

        Assert.Equal(ErrorCodes.MissingRequirementIds, result.Error!.Code);
        Assert.Equal(
            ["Requirement without an FR-n ID: \"Users can reset their password.\".", "FR-2 has no Given/When/Then acceptance criterion."],
            result.RequirementProblems);
        Assert.Equal(result.RequirementProblems, result.Error.Details);
        var feedback = SpecValidator.CorrectiveFeedback(result.Error, SpecValidationContext.FirstVersion);
        Assert.Contains("MISSING_REQUIREMENT_IDS", feedback, StringComparison.Ordinal);
        Assert.Contains("Users can reset their password.", feedback, StringComparison.Ordinal);
        Assert.Contains("FR-n", feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void FR75_A_sub_heading_with_text_but_no_id_is_an_offender_while_a_grouping_heading_is_not()
    {
        var requirements = """
            ### Accounts
            Account features.
            - FR-1 Sign in: given a user, when they sign in, then they are in.

            ### Reporting
            Users see weekly reports by email.
            """;

        var result = SpecValidator.Validate(Spec(requirements), SpecValidationContext.FirstVersion);

        Assert.Equal(["Requirement without an FR-n ID: \"Reporting\"."], result.RequirementProblems);
    }

    [Fact]
    public void FR75_Duplicate_ids_and_an_empty_section_are_problems()
    {
        var duplicate = SpecValidator.Validate(Spec(Login + "\n\n" + Login), SpecValidationContext.FirstVersion);
        Assert.Equal(["FR-1 is used by 2 requirements; each ID must be unique."], duplicate.RequirementProblems);

        var none = SpecValidator.Validate(Spec("The system does useful things."), SpecValidationContext.FirstVersion);
        Assert.Equal(["No functional requirements with an FR-n ID were found under \"Functional Requirements\"."], none.RequirementProblems);
    }

    [Fact]
    public void FR75_Only_the_functional_requirements_section_is_scanned_when_it_exists()
    {
        var spec = Spec(Login) + "\n\n## Notes\n- FR-9 is mentioned here: no criterion.";

        var scan = RequirementParser.Parse(spec);

        Assert.True(scan.SectionFound);
        Assert.Equal(["FR-1"], scan.Ids);
        Assert.Equal(["FR-1", "FR-9"], RequirementParser.Ids("- FR-1 a\n- FR-9 b"));
    }

    [Theory]
    [InlineData("fr-07", "FR-7")]
    [InlineData(" FR-12 ", "FR-12")]
    [InlineData("FR7", null)]
    [InlineData("REQ-1", null)]
    [InlineData(null, null)]
    public void FR75_Requirement_ids_normalise(string? text, string? expected) =>
        Assert.Equal(expected, RequirementParser.NormaliseId(text));

    // ---------- Iteration tags (§4.13) ----------

    [Fact]
    public void S4_13_A_valid_iteration_spec_has_one_tag_per_requirement_and_something_new_or_changed()
    {
        var requirements = """
            ### FR-1 [unchanged] Sign in
            - Given a user, when they sign in, then they see the dashboard.
            ### FR-2 [changed] Export
            - Given a list, when exported, then a CSV with headers downloads.
            ### FR-3 [removed] Fax export
            ### FR-4 [new] Import
            - Given a CSV, when imported, then the rows appear.
            """;

        var result = SpecValidator.Validate(Iteration(requirements), SpecValidationContext.NewVersion(["FR-1", "fr-2", "FR-3"]));

        Assert.True(result.IsValid, string.Join("\n", result.Error?.Details ?? []));
        Assert.Equal(
            [RequirementTag.Unchanged, RequirementTag.Changed, RequirementTag.Removed, RequirementTag.New],
            result.Requirements.Select(r => r.Tag!.Value));
        Assert.Equal(["FR-2", "FR-4"], result.Scan.NewOrChangedIds);
        Assert.Equal("Export", result.Requirements[1].Title);
    }

    [Fact]
    public void S4_13_Missing_or_double_tags_are_INVALID_REQUIREMENT_TAGS()
    {
        var requirements = """
            ### FR-1 Sign in
            - Given a user, when they sign in, then they see the dashboard.
            ### FR-2 [new] [changed] Export
            - Given a list, when exported, then a CSV downloads.
            """;

        var result = SpecValidator.Validate(Iteration(requirements), SpecValidationContext.NewVersion(["FR-1", "FR-2"]));

        Assert.Equal(ErrorCodes.InvalidRequirementTags, result.Error!.Code);
        Assert.Equal(
            [
                "FR-1 has no tag; tag its line with exactly one of [new], [changed], [unchanged] or [removed].",
                "FR-2 has 2 tags ([new] [changed]); use exactly one.",
            ],
            result.TagProblems);
        Assert.Contains("[unchanged]", SpecValidator.CorrectiveFeedback(result.Error, SpecValidationContext.NewVersion([])), StringComparison.Ordinal);
    }

    [Fact]
    public void S4_13_An_iteration_with_nothing_new_or_changed_is_invalid()
    {
        var result = SpecValidator.Validate(Iteration("### FR-1 [unchanged] Sign in\nGiven a, when b, then c."), SpecValidationContext.NewVersion(["FR-1"]));

        Assert.Equal(["No requirement is tagged [new] or [changed]; a new version must add or change at least one."], result.TagProblems);
    }

    [Fact]
    public void S4_13_Changed_or_removed_ids_must_exist_in_the_baseline_except_on_an_imports_first_version()
    {
        var requirements = """
            ### FR-5 [changed] Export
            Given a list, when exported, then a CSV downloads.
            ### FR-6 [removed] Fax
            """;

        var iteration = SpecValidator.Validate(Iteration(requirements), SpecValidationContext.NewVersion(["FR-1"]));
        Assert.Equal(
            [
                "FR-5 is tagged [changed] but isn't in the previous version's specification; use [new] for a new requirement.",
                "FR-6 is tagged [removed] but isn't in the previous version's specification; use [new] for a new requirement.",
            ],
            iteration.TagProblems);

        Assert.True(SpecValidator.Validate(Iteration(requirements), SpecValidationContext.ImportFirstVersion()).IsValid);
    }

    [Fact]
    public void S4_13_Tags_are_not_checked_outside_iterations_and_Check_returns_the_spec()
    {
        var spec = Spec(Login);

        Assert.Equal(spec, SpecValidator.Check(spec, SpecValidationContext.FirstVersion).Value);
        Assert.Equal(ErrorCodes.InvalidRequirementTags, SpecValidator.Check(Iteration(Login), SpecValidationContext.NewVersion([])).Error!.Code);
    }

    [Fact]
    public void S4_13_A_removed_requirement_needs_no_criterion()
    {
        var result = SpecValidator.Validate(
            Iteration("### FR-1 [removed] Fax\n### FR-2 [new] Export\nGiven a, when b, then c."),
            SpecValidationContext.NewVersion(["FR-1"]));

        Assert.True(result.IsValid);
    }
}
