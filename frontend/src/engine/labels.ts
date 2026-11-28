import type { Schemas } from '../api/client'
import type { BadgeTone } from '../components/ui/badge'
import type { GenericRunStatus, MapItemStatus, PipelineDefinition, RunGate, StageState, TimelineKind } from './api/types'
import { humanise } from './format'

/**
 * FR-305: the engine shows no stage, artifact or gate names of its own. Every label comes from the run's definition
 * (`title`s) or the run data; this module holds only the engine's generic vocabulary (run status, stage state,
 * timeline kinds), which is the same for every pipeline.
 */

export const runStatusLabels: Record<GenericRunStatus, string> = {
  active: 'Active',
  running: 'Running',
  waiting_on_gate: 'Waiting on a gate',
  waiting_on_input: 'Waiting on an input',
  completed: 'Completed',
  failed: 'Failed',
  cancelled: 'Cancelled',
}

export const runStatusTones: Record<GenericRunStatus, BadgeTone> = {
  active: 'info',
  running: 'info',
  waiting_on_gate: 'warning',
  waiting_on_input: 'warning',
  completed: 'success',
  failed: 'danger',
  cancelled: 'neutral',
}

export const stageStateLabels: Record<StageState, string> = {
  pending: 'Pending',
  active: 'Active',
  completed: 'Completed',
  skipped: 'Skipped',
}

export const stageStateTones: Record<StageState, BadgeTone> = {
  pending: 'neutral',
  active: 'info',
  completed: 'success',
  skipped: 'neutral',
}

export const timelineKindLabels: Record<TimelineKind, string> = {
  transition: 'Transition',
  stage_entered: 'Stage entered',
  agent_run: 'Agent run',
  gate_created: 'Gate raised',
  gate_decided: 'Gate decided',
  artifact_version: 'Artifact version',
}

export const agentRunStatusLabels: Record<Schemas['AgentRunStatus'], string> = {
  queued: 'Queued',
  running: 'Running',
  succeeded: 'Succeeded',
  failed: 'Failed',
  cancelled: 'Cancelled',
}

export const agentRunStatusTones: Record<Schemas['AgentRunStatus'], BadgeTone> = {
  queued: 'neutral',
  running: 'info',
  succeeded: 'success',
  failed: 'danger',
  cancelled: 'warning',
}

export const gateStatusLabels: Record<RunGate['status'], string> = {
  pending: 'Pending',
  approved: 'Approved',
  rejected: 'Rejected',
  cancelled: 'Cancelled',
}

export const artifactStatusLabels: Record<NonNullable<Schemas['ArtifactStatus']>, string> = {
  draft: 'Draft',
  locked: 'Locked',
  rejected: 'Rejected',
  superseded: 'Superseded',
  invalidated: 'Invalidated',
  corrupted: 'Corrupted',
}

/** Status labels for the generic gate status values a timeline entry carries. */
export function gateStatusText(status: string | null | undefined): string | null {
  return status && Object.hasOwn(gateStatusLabels, status) ? gateStatusLabels[status as RunGate['status']] : (status ?? null)
}

/** Titles from a definition (and the run's gates), with a readable fallback for keys the definition doesn't name. */
export interface DefinitionLabels {
  stage: (id: string | null | undefined) => string
  artifact: (key: string | null | undefined) => string
  gate: (gateId: string | null | undefined, gateType: string | null | undefined) => string
}

export function definitionLabels(definition: PipelineDefinition | undefined, gates: readonly RunGate[] = []): DefinitionLabels {
  // FR-343: a parallel group's branch stages are nested in it; their entries carry their own ids.
  const stages = new Map(
    definition?.stages.flatMap((s) => [[s.id, s.title] as const, ...(s.branches ?? []).flatMap((b) => b.stages.map((x) => [x.id, x.title] as const))]) ?? [],
  )
  const artifacts = new Map(definition?.artifacts.map((a) => [a.key, a.title]) ?? [])
  const gateTitles = new Map(gates.map((g) => [g.id, g.title]))
  const gateTypeTitles = new Map(gates.map((g) => [g.type, g.title]))
  return {
    stage: (id) => (id ? (stages.get(id) ?? humanise(id)) : '—'),
    artifact: (key) => (key ? (artifacts.get(key) ?? humanise(key)) : '—'),
    gate: (gateId, gateType) =>
      (gateId ? gateTitles.get(gateId) : undefined) ??
      (gateType ? (gateTypeTitles.get(gateType) ?? humanise(gateType)) : 'Gate'),
  }
}

/** FR-363: a map item's status. */
export const mapItemStatusLabels: Record<MapItemStatus, string> = {
  open: 'Open',
  completed: 'Completed',
  failed: 'Failed',
}

export const mapItemStatusTones: Record<MapItemStatus, BadgeTone> = {
  open: 'info',
  completed: 'success',
  failed: 'danger',
}

/** FR-355, FR-363: map items are 0-based in the data and counted from 1 on screen ("Item 1"). */
export const itemLabel = (index: number) => `Item ${String(index + 1)}`

/**
 * FR-343, FR-345, FR-363: a branch as a lane label. A parallel group's branches are 0-based indexes ("Branch 1"); a
 * fan-out's are its agent keys or `#n`, shown as they are.
 */
export const branchLabel = (branch: string) => (/^\d+$/.test(branch) ? `Branch ${String(Number(branch) + 1)}` : `Branch ${branch}`)

/** FR-354, FR-363: a follow-up label (`followup:7d`) as words ("Follow-up after 7d"); other labels as they are. */
export function followUpLabel(label: string): string {
  const after = /^followup:(.+)$/.exec(label)?.[1]
  return after ? `Follow-up after ${after}` : label
}

/** FR-364: what a gate payload asks for, by its `kind`. */
export const gatePayloadKindLabels: Record<string, string> = {
  variant_select: 'Pick a variant',
  tool_call: 'Tool approval',
  external_handler: 'External action',
}
