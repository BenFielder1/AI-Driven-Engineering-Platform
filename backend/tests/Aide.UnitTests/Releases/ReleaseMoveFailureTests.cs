using Aide.Api.Features.Release;
using Aide.Api.Pipelines;
using Aide.Domain.Common;
using Tevrin.Engine.Domain.Common;

namespace Aide.UnitTests.Releases;

/// <summary>FR-277: a release move refused by the pinned definition dead-letters the job at once; other refusals retry.</summary>
public class ReleaseMoveFailureTests
{
    [Fact]
    public void FR277_DEFINITION_UNAVAILABLE_fails_a_release_job_permanently_naming_the_missing_names()
    {
        var error = new DomainError(ErrorCodes.DefinitionUnavailable, SdlcLifecycle.DefinitionUnavailableMessage) { Details = ["guard:sdlc.gone"] };

        var failure = ReleaseMoveFailure.Of(error);

        Assert.False(failure.Transient);
        Assert.Equal(ErrorCodes.DefinitionUnavailable, failure.ErrorCode);
        Assert.Contains("guard:sdlc.gone", failure.Message, StringComparison.Ordinal);
        Assert.Contains("retry the dead letter", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR277_other_refusals_of_a_release_move_stay_transient_as_in_0_6_1()
    {
        var failure = ReleaseMoveFailure.Of(new DomainError(ErrorCodes.NotReleasing, "The project is not releasing."));

        Assert.True(failure.Transient);
        Assert.Equal(("The project is not releasing.", ErrorCodes.NotReleasing), (failure.Message, failure.ErrorCode));
    }
}
