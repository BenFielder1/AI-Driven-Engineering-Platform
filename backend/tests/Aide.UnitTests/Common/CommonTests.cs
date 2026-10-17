using System.Reflection;
using Aide.Domain;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Projects;
using Aide.Domain.Review;

namespace Aide.UnitTests.Common;

/// <summary>§5 enum value sets as stored strings, and the §6 error table.</summary>
public class EnumStringsTests
{
    [Fact]
    public void S5_Phase_values() =>
        Assert.Equal(["refinement", "specification", "planning", "execution", "review", "release"], EnumStrings.Values<Phase>());

    [Fact]
    public void S5_FR65_Project_status_values() =>
        Assert.Equal(["active", "building", "reviewing", "releasing", "released", "rolled_back", "awaiting_merge"], EnumStrings.Values<ProjectStatus>());

    [Fact]
    public void S5_Gate_values()
    {
        Assert.Equal(["spec_approval", "plan_approval", "pre_release", "escalation"], EnumStrings.Values<GateType>());
        Assert.Equal(["pending", "approved", "rejected", "cancelled"], EnumStrings.Values<GateStatus>());
        Assert.Equal(["continue", "rollback", "accept"], EnumStrings.Values<EscalationDecision>());
        Assert.Equal(["strict", "advisory", "autonomous"], EnumStrings.Values<ApprovalMode>());
    }

    [Fact]
    public void S5_Artifact_values()
    {
        // v2 SPEC §5 amends the type list: + codebase_summary (FR-70).
        Assert.Equal(["project_request", "technical_specification", "implementation_plan", "optimization_plan", "codebase_summary"], EnumStrings.Values<ArtifactType>());
        Assert.Equal(["draft", "locked", "rejected", "superseded", "invalidated", "corrupted"], EnumStrings.Values<ArtifactStatus>());
    }

    [Fact]
    public void S5_Step_job_review_and_role_values()
    {
        Assert.Equal(["pending", "in_progress", "completed", "rejected", "blocked", "invalidated"], EnumStrings.Values<StepStatus>());
        Assert.Equal(["queued", "running", "succeeded", "failed", "dead", "cancelled", "retried"], EnumStrings.Values<JobStatus>());
        Assert.Equal(["release_eligible", "review_required", "escalation_required"], EnumStrings.Values<ReviewDecision>());
        Assert.Equal(["none", "low", "moderate", "severe"], EnumStrings.Values<DriftLevel>());
        Assert.Equal(["minor", "major", "critical"], EnumStrings.Values<Severity>());
        Assert.Equal(["feature_coverage", "api_contracts", "data_model", "architecture", "security", "testing"], EnumStrings.Values<ReviewCategory>());
        Assert.Equal(["viewer", "contributor", "owner"], EnumStrings.Values<ProjectRole>());
        Assert.Equal(["user", "admin"], EnumStrings.Values<SystemRole>());
        Assert.Equal(["idea_refinement", "spec_generator", "plan_generator", "code_generator", "code_review", "repository_analyst"], EnumStrings.Values<AgentKind>());
        Assert.Equal(["create", "modify"], EnumStrings.Values<FileAction>());
    }

    [Fact]
    public void Wire_values_round_trip_for_every_domain_enum()
    {
        var enums = typeof(DomainAssembly).Assembly.GetTypes().Where(t => t.IsEnum && t.IsPublic).ToList();
        var roundTrip = typeof(EnumStringsTests).GetMethod(nameof(RoundTrip), BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.NotEmpty(enums);
        foreach (var type in enums)
        {
            roundTrip.MakeGenericMethod(type).Invoke(null, null);
        }
    }

    [Fact]
    public void Unknown_wire_values_do_not_parse()
    {
        Assert.False(EnumStrings.TryParse<Phase>("Review", out _));
        Assert.False(EnumStrings.TryParse<Phase>(null, out _));
        Assert.Throws<FormatException>(() => EnumStrings.Parse<StepStatus>("inprogress"));
        Assert.Equal(StepStatus.InProgress, EnumStrings.Parse<StepStatus>("in_progress"));
    }

    private static void RoundTrip<TEnum>()
        where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            var wire = value.ToWire();
            Assert.Matches("^[a-z]+(_[a-z]+)*$", wire);
            Assert.Equal(value, EnumStrings.Parse<TEnum>(wire));
        }
    }
}

public class ErrorCodesTests
{
    private static List<string> AllCodes() =>
        [.. typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)];

    [Fact]
    public void S6_Every_endpoint_error_code_has_its_status()
    {
        var expected = new Dictionary<string, int>
        {
            ["VALIDATION"] = 400,
            ["UNAUTHENTICATED"] = 401,
            ["FORBIDDEN"] = 403,
            ["NOT_FOUND"] = 404,
            ["CONFLICT"] = 409,
            ["ANTIFORGERY"] = 403,
            ["EMAIL_TAKEN"] = 409,
            ["INVALID_CREDENTIALS"] = 401,
            ["GITHUB_AUTH_FAILED"] = 400,
            ["USER_NOT_FOUND"] = 404,
            ["LAST_OWNER"] = 409,
            ["PHASE_PRECONDITION_FAILED"] = 409,
            ["GATE_PENDING"] = 409,
            ["BUDGET_EXCEEDED"] = 409,
            ["WRONG_PHASE"] = 409,
            ["ARTIFACT_NOT_DRAFT"] = 409,
            ["ARTIFACT_LOCKED"] = 409,
            ["GATE_NOT_PENDING"] = 409,
            ["INVALID_DECISION"] = 400,
            ["REVIEW_IN_PROGRESS"] = 409,
            ["NOT_RELEASING"] = 409,
            ["RELEASE_IN_PROGRESS"] = 409,
            ["INVALID_TARGET_VERSION"] = 400,
            ["ROLLBACK_BLOCKED"] = 409,
            ["RATE_LIMITED"] = 429,
            ["LINK_EXPIRED"] = 410,
            ["LINK_USED"] = 409,
            ["MERGE_CONFLICT"] = 409,
            ["REFINEMENT_IN_PROGRESS"] = 409,
            ["INVALID_PLAN"] = 400,
            ["CREDENTIAL_INVALID"] = 400,
            ["CREDENTIAL_NOT_OWNER"] = 403,
            ["GITHUB_NOT_CONNECTED"] = 409,
            ["GITHUB_APP_NOT_INSTALLED"] = 409,
            ["REPO_NAME_TAKEN"] = 409,
            ["GITHUB_RATE_LIMITED"] = 429,
        };

        foreach (var (code, status) in expected)
        {
            Assert.Equal(status, ErrorCodes.StatusCodeFor(code));
        }
    }

    [Fact]
    public void Codes_are_unique_and_upper_snake_case()
    {
        var codes = AllCodes().Concat(ErrorCodes.Guardian.All).ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, c => Assert.Matches("^[A-Z]+(_[A-Z]+)*$", c));
    }

    [Fact]
    public void Run_and_guardian_codes_are_not_http_errors()
    {
        Assert.Null(ErrorCodes.StatusCodeFor(ErrorCodes.MissingInput));
        Assert.Null(ErrorCodes.StatusCodeFor(ErrorCodes.MissingSections));
        Assert.Null(ErrorCodes.StatusCodeFor(ErrorCodes.InvalidOutput));
        Assert.All(ErrorCodes.Guardian.All, c => Assert.Null(ErrorCodes.StatusCodeFor(c)));
    }

    [Fact]
    public void FR65_MERGE_TIMEOUT_and_PR_CLOSED_are_release_failure_codes_not_http_errors()
    {
        Assert.Equal(("MERGE_TIMEOUT", "PR_CLOSED"), (ErrorCodes.MergeTimeout, ErrorCodes.PrClosed));
        Assert.Null(ErrorCodes.StatusCodeFor(ErrorCodes.MergeTimeout));
        Assert.Null(ErrorCodes.StatusCodeFor(ErrorCodes.PrClosed));
    }
}

public class ResultTests
{
    [Fact]
    public void Failure_has_no_value()
    {
        var result = Result.Failure<int>(new DomainError(ErrorCodes.Conflict, "x"));

        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Success_carries_the_value()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(42, result.Value);
    }
}

public class ProjectDefaultsTests
{
    [Fact]
    public void FR5_Defaults_are_advisory_090_20_files_3_cycles_version1()
    {
        Assert.Equal(ApprovalMode.Advisory, ProjectDefaults.Mode);
        Assert.Equal(0.90m, ProjectDefaults.ComplianceThreshold);
        Assert.Equal(20, ProjectDefaults.MaxFilesPerStep);
        Assert.Equal(3, ProjectDefaults.MaxReviewCycles);
        Assert.Equal(new LifecycleState(Phase.Refinement, ProjectStatus.Active, ProjectDefaults.InitialVersion), LifecycleState.Initial);
    }
}
