namespace Tevrin.Pack.Sdlc;

/// <summary>
/// FR-262: the SDLC pack, today's software lifecycle on the engine. From M56 it holds the SDLC key constants
/// (<see cref="SdlcAgents"/>, <see cref="SdlcArtifacts"/>, <see cref="SdlcGates"/>; FR-269–FR-271, D-0.7.0-23); the
/// definition, guards and <c>IPipelinePack</c> registration arrive in M57. It references only the engine, and
/// <c>Aide.*</c> references it.
/// </summary>
public static class SdlcPack;
