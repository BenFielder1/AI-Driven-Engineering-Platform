using Aide.Domain.Common;
using Tevrin.Engine.Domain.Common;

namespace Aide.UnitTests.Common;

/// <summary>
/// FR-262: the engine's error codes are the same wire values as the host's error table (ErrorCodes.cs keeps literal values:
/// the frontend's copy test reads them from the source).
/// </summary>
public class EngineErrorCodesTests
{
    [Fact]
    public void FR262_engine_error_codes_equal_the_host_error_table()
    {
        Assert.Equal(ErrorCodes.Validation, EngineErrorCodes.Validation);
        Assert.Equal(ErrorCodes.Conflict, EngineErrorCodes.Conflict);
    }
}
