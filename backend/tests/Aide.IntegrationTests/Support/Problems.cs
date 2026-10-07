using System.Net;
using System.Text.Json;

namespace Aide.IntegrationTests.Support;

public static class Problems
{
    /// <summary>Asserts a ProblemDetails response with the status, <c>errorCode</c> and a <c>traceId</c>; returns the body.</summary>
    public static async Task<JsonElement> AssertAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(status == response.StatusCode, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal(errorCode, body.GetProperty("errorCode").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        return body;
    }
}
