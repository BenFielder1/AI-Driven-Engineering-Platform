using Tevrin.Domain.Notifications;
using Tevrin.Engine.Runtime.Notifications;

namespace Tevrin.Api.Features.Notifications;

/// <summary>FR-48: the SDLC's spec, plan and pre-release gates get one-click approval links
/// (<see cref="NotificationRules.OffersApprovalLinks"/>).</summary>
public sealed class SdlcApprovalLinkPolicy : IApprovalLinkPolicy
{
    public bool OffersApprovalLinks(string gateType) => NotificationRules.OffersApprovalLinks(gateType);
}
