using Aide.Api.Features.Review;
using Aide.Domain.Review;

namespace Aide.IntegrationTests.Review;

/// <summary>FR-249, FR-250: the report finding DTO's handling fields and the waivers stored in <c>external_checks</c> (no database).</summary>
public sealed class FindingHandlingMappingTests
{
    private const string Reason = "zod is the spec's 'schema validation library'";

    [Fact]
    public void FR250_A_stepped_a_waived_and_a_refused_finding_map_their_handling_to_the_dto()
    {
        var stepped = new ReviewFinding(ReviewCategory.Architecture, Severity.Major, "lodash", "frontend/package.json")
        {
            FindingId = "finding:dependency:lodash",
            Handling = FindingHandling.Step,
            StepNumber = 2,
        };
        var waived = new ReviewFinding(ReviewCategory.Architecture, Severity.Major, "zod") { FindingId = "finding:dependency:zod", Handling = FindingHandling.Waived, WaivedReason = Reason };
        var refused = new ReviewFinding(ReviewCategory.Security, Severity.Critical, "secret", "frontend/src/config.ts")
        {
            FindingId = "finding:secret:frontend-src-config-ts-12",
            Handling = FindingHandling.WaiverRefused,
            StepNumber = 3,
        };

        var dtos = new[] { stepped, waived, refused }.Select(ReviewReader.ToDto).ToList();

        Assert.Equal(
            [
                ("finding:dependency:lodash", (FindingHandling?)FindingHandling.Step, (int?)2, (string?)null),
                ("finding:dependency:zod", FindingHandling.Waived, null, Reason),
                ("finding:secret:frontend-src-config-ts-12", FindingHandling.WaiverRefused, 3, null),
            ],
            dtos.Select(d => (d.FindingId!, d.Handling, d.StepNumber, d.WaivedReason)));
    }

    [Fact]
    public void FR250_A_finding_stored_before_part_2_maps_with_no_handling_and_a_failure_shows_its_failure_id()
    {
        var legacy = ReviewReader.ToDto(new ReviewFinding(ReviewCategory.Testing, Severity.Minor, "m"));
        var failure = ReviewReader.ToDto(new ReviewFinding(ReviewCategory.Architecture, Severity.Critical, "Vercel preview build failed") { FailureId = "failure:preview:vercel" });

        Assert.Equal(((string?)null, (FindingHandling?)null, (int?)null, (string?)null), (legacy.FindingId, legacy.Handling, legacy.StepNumber, legacy.WaivedReason));
        Assert.Equal("failure:preview:vercel", failure.FindingId);
    }

    [Fact]
    public void FR249_Waivers_round_trip_through_external_checks_and_older_reports_have_none()
    {
        FindingWaiver[] waivers = [new("finding:dependency:zod", "Dependency 'zod' is not in the specification's architecture.", Reason, 1)];

        var onlyWaivers = CollectedChecks.Compose(null, null, waivers: waivers);
        var withChecks = CollectedChecks.Compose(new CollectedChecks([], null, false), null, waivers: waivers);

        Assert.Equal(waivers, CollectedChecks.StoredWaivers(onlyWaivers));
        Assert.Equal(waivers, CollectedChecks.StoredWaivers(withChecks));
        Assert.Null(ReviewReader.ReadExternalChecks(onlyWaivers));
        Assert.NotNull(ReviewReader.ReadExternalChecks(withChecks));
        Assert.Null(CollectedChecks.Compose(null, null));
        Assert.Empty(CollectedChecks.StoredWaivers("""{"checks":[],"checksTimedOut":false}"""));
        Assert.Empty(CollectedChecks.StoredWaivers(null));
    }

    [Fact]
    public void FR249_A_plan_revision_replaces_the_stored_waivers_and_keeps_the_rest_of_external_checks()
    {
        FindingWaiver[] first = [new("finding:dependency:zod", "zod", Reason, 1)];
        FindingWaiver[] revised = [new("finding:dependency:lodash", "lodash", Reason, 1)];
        var withChecks = CollectedChecks.Compose(new CollectedChecks([], 30, false), null, waivers: first);

        var replaced = CollectedChecks.ReplaceWaivers(withChecks, revised);

        Assert.Equal(revised, CollectedChecks.StoredWaivers(replaced));
        Assert.Equal(30, ReviewReader.ReadExternalChecks(replaced)!.ChecksWaitedSeconds);
        Assert.Equal(revised, CollectedChecks.StoredWaivers(CollectedChecks.ReplaceWaivers(null, revised)));
        Assert.Null(CollectedChecks.ReplaceWaivers(null, []));
        Assert.Empty(CollectedChecks.StoredWaivers(CollectedChecks.ReplaceWaivers(withChecks, [])));
    }
}
