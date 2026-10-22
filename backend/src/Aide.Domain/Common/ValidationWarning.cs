namespace Aide.Domain.Common;

/// <summary>
/// A non-blocking finding of a validation (e.g. <see cref="ErrorCodes.UncoveredRequirements"/>): shown to people and
/// recorded, never a failure. <see cref="Details"/> lists the individual items (e.g. the uncovered requirement IDs).
/// </summary>
public sealed record ValidationWarning(string Code, string Message, IReadOnlyList<string> Details);
