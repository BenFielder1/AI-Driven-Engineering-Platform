using System.Text.Json;
using Aide.Api.Errors;
using Aide.Domain.Common;
using Aide.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Auth;

/// <summary>§6 common errors from exceptions; §8 secrets encrypted with Data Protection.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ErrorHandlingTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task S6_Concurrency_exception_becomes_409_CONFLICT()
    {
        var (status, body) = await HandleAsync(new DbUpdateConcurrencyException("row changed"));

        Assert.Equal(409, status);
        Assert.Equal(ErrorCodes.Conflict, body.GetProperty("errorCode").GetString());
        Assert.True(body.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task S6_Unhandled_exception_becomes_500_without_details()
    {
        var (status, body) = await HandleAsync(new InvalidOperationException("secret internals: password=hunter2"));

        Assert.Equal(500, status);
        Assert.DoesNotContain("hunter2", body.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void S6_Every_endpoint_error_code_maps_to_its_status()
    {
        foreach (var (code, status) in ErrorCodes.HttpStatusCodes)
        {
            var result = ApiProblems.FromError(new DomainError(code, "x"));
            Assert.Equal(status, result.StatusCode);
            Assert.Equal(code, result.ProblemDetails.Extensions["errorCode"]);
        }
    }

    [Fact]
    public void NFR_SEC_Secret_protector_round_trips_and_rejects_tampering()
    {
        var protector = factory.Services.GetRequiredService<ISecretProtector>();

        var protectedValue = protector.Protect("ghp_example-token");

        Assert.NotEqual("ghp_example-token", protectedValue);
        Assert.Equal("ghp_example-token", protector.Unprotect(protectedValue));
        Assert.False(protector.TryUnprotect(protectedValue[..^2] + "AA", out _));
    }

    private async Task<(int Status, JsonElement Body)> HandleAsync(Exception exception)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Response.Body = new MemoryStream();
        var handler = ActivatorUtilities.CreateInstance<GlobalExceptionHandler>(scope.ServiceProvider);

        Assert.True(await handler.TryHandleAsync(context, exception, Ct));

        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: Ct);
        return (context.Response.StatusCode, doc.RootElement.Clone());
    }
}
