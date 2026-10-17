namespace Aide.IntegrationTests.Support;

/// <summary>The test-only fixture preset (FR-74), embedded from <c>tests/Fixtures/Presets/</c> and added by <see cref="AideApiFactory"/>.</summary>
public static class TestPresetId
{
    /// <summary>The logical-name prefix of the fixture resources (see the csproj).</summary>
    public const string Prefix = "Aide.Tests.Presets/";

    public const string Id = "test-stack";

    public const string Version = "0.9.0";
}
