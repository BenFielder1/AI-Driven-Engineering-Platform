import type { Schemas } from '../api/client'
import type { BadgeTone } from '../components/ui/badge'
import type { GenericRunStatus, PipelineDefinition, RunGate, StageState, TimelineKind } from './api/types'
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
  const stages = new Map(definition?.stages.map((s) => [s.id, s.title]) ?? [])
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
