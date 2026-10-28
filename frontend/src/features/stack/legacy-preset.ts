/**
 * FR-129: the v2 presets are retired. A migrated release version or agent run keeps the preset id and version as
 * history; this is how the web app names them, in the server's `LegacyPresetDto.label` format
 * ("Preset nextjs-supabase@1.0.1 (retired, migrated)"). `null` when there was no preset.
 */
export function legacyPresetLabel(presetId: string | null | undefined, presetVersion: string | null | undefined): string | null {
  if (!presetId) {
    return null
  }
  return `Preset ${presetId}${presetVersion ? `@${presetVersion}` : ''} (retired, migrated)`
}

/**
 * FR-121, FR-129: the specialist a step or run ran as. A step planned for a retired preset agent records the v2 key in
 * `legacyAgentKey`, shown as "data-engineer (was db-engineer)". `null` without a specialist (v1's Code Generator).
 */
export function specialistText(agentKey: string | null | undefined, legacyAgentKey?: string | null): string | null {
  if (!agentKey) {
    return null
  }
  return legacyAgentKey && legacyAgentKey !== agentKey ? `${agentKey} (was ${legacyAgentKey})` : agentKey
}
