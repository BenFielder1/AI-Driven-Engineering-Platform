using Aide.Domain.Deployments;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Deployments;
using Microsoft.Extensions.Options;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// §9: <c>Deployments:Provider=Fake</c> and <c>Databases:Provider=Fake</c> are for Development, Testing and explicitly
/// opted-in E2E only. Elsewhere startup fails unless <c>Deployments:AllowFake=true</c> is also set, and then a warning is
/// logged at every boot.
/// </summary>
public sealed partial class FakeDeploymentProviderGuard(
    IOptions<DeploymentOptions> options,
    IOptions<DatabaseOptions> databases,
    IHostEnvironment environment,
    ILogger<FakeDeploymentProviderGuard> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        switch (FakeProviderPolicy.Decide(settings.UseFake, environment.EnvironmentName, settings.AllowFake))
        {
            case FakeProviderDecision.Refused:
                throw new InvalidOperationException(
                    $"Deployments:Provider=Fake is only allowed in Development or Testing (this is {environment.EnvironmentName}). "
                    + "Set Deployments:AllowFake=true as well to use it for E2E, or use the Vercel provider.");
            case FakeProviderDecision.AllowedWithWarning:
                LogFakeEnabled(logger, environment.EnvironmentName);
                break;
            default:
                break;
        }

        switch (FakeProviderPolicy.Decide(databases.Value.UseFake, environment.EnvironmentName, settings.AllowFake))
        {
            case FakeProviderDecision.Refused:
                throw new InvalidOperationException(
                    $"Databases:Provider=Fake is only allowed in Development or Testing (this is {environment.EnvironmentName}). "
                    + "Set Deployments:AllowFake=true as well to use it for E2E, or leave Databases:Provider unset.");
            case FakeProviderDecision.AllowedWithWarning:
                LogFakeDatabasesEnabled(logger, environment.EnvironmentName);
                break;
            default:
                break;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fake deployment provider enabled in {Environment} (Deployments:AllowFake) — not for real use")]
    private static partial void LogFakeEnabled(ILogger logger, string environment);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fake database provider enabled in {Environment} (Deployments:AllowFake) — not for real use")]
    private static partial void LogFakeDatabasesEnabled(ILogger logger, string environment);
}
