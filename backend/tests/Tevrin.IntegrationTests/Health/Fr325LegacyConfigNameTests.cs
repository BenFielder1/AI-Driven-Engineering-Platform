using System.Net;
using Microsoft.Extensions.Logging;
using Tevrin.Infrastructure.Configuration;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Health;

/// <summary>FR-325 (§4.77): an API configured only with the pre-0.8.0 connection string name still starts and connects,
/// and logs one warning for that old name.</summary>
[Collection(ApiTestGroup.Name)]
public class Fr325LegacyConfigNameTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR325_Only_ConnectionStrings_Aide_set_the_API_starts_connects_and_warns_once()
    {
        var name = LegacyConfigNames.ConnectionString;
        // The log sink is shared by the "Api" collection, so count only the warnings this test adds.
        bool IsOldNameWarning(CapturedLog e) => e.Level == LogLevel.Warning
            && e.State.Any(kv => kv.Key == "OldName" && Equals(kv.Value, name.OldKey));
        var before = factory.Logs.Entries.Count(IsOldNameWarning);
        await using var legacy = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting(name.NewKey, string.Empty); // unset the factory's new name
            b.UseSetting(name.OldKey, factory.ConnectionString);
        });
        using var client = legacy.CreateClient();

        var response = await client.GetAsync(new Uri("/api/health/ready", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // connected, migrations applied
        var added = factory.Logs.Entries.Where(IsOldNameWarning).Skip(before).ToList();
        var warning = Assert.Single(added);
        Assert.Equal("Tevrin.Api.Configuration", warning.Category);
        Assert.DoesNotContain(factory.ConnectionString, warning.AllText, StringComparison.Ordinal);
    }
}
