import {
  agentKindLabels,
  agentKinds,
  artifactTypeLabels,
  artifactTypes,
  escalationDecisionLabels,
  escalationDecisions,
  formatDuration,
  formatNumber,
  gateTypeLabels,
  gateTypes,
  phaseLabels,
  phases,
} from '../projects/labels'
import type { ActivityItem } from './activity-api'

type Payload = ActivityItem['payload']

/** Narrows a free-form payload value to one of `allowed` (the contract's wire values), or `null`. */
function oneOf<T extends string>(payload: Payload, key: string, allowed: readonly T[]): T | null {
  const value = payload[key]
  return typeof value === 'string' ? (allowed.find((a) => a === value) ?? null) : null
}

/** A whole, non-negative number (versions), or `null`. */
function version(payload: Payload, key: string): number | null {
  const value = payload[key]
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : null
}

/** `ARTIFACT_APPROVED` → "Artifact approved". */
function humanizeType(type: string): string {
  const words = type.toLowerCase().replaceAll('_', ' ')
  return words.length === 0 ? type : words.charAt(0).toUpperCase() + words.slice(1)
}

function describePhaseChange(payload: Payload): string | null {
  const from = oneOf(payload, 'from', phases)
  const to = oneOf(payload, 'to', phases)
  if (!from || !to) {
    return null
  }
  const v = version(payload, 'projectVersion')
  const text = `Phase changed from ${phaseLabels[from]} to ${phaseLabels[to]}`
  return v === null ? text : `${text} (version ${String(v)})`
}

function describeGate(type: string, payload: Payload): string | null {
  const gateType = oneOf(payload, 'type', gateTypes)
  if (!gateType) {
    return null
  }
  const gate = `${gateTypeLabels[gateType]} gate`
  switch (type) {
    case 'GATE_CREATED':
      return `${gate} created`
    case 'GATE_APPROVED': {
      if (payload.decision === undefined || payload.decision === null) {
        return `${gate} approved`
      }
      const decision = oneOf(payload, 'decision', escalationDecisions)
      if (!decision) {
        return null
      }
      const target = version(payload, 'targetVersion')
      const label = escalationDecisionLabels[decision]
      return decision === 'rollback' && target !== null
        ? `${gate} decided: ${label} to version ${String(target)}`
        : `${gate} decided: ${label}`
    }
    case 'GATE_REJECTED':
      return `${gate} rejected`
    case 'GATE_CANCELLED':
      return `${gate} cancelled`
    case 'GATE_OVERDUE':
      return `${gate} is overdue`
    default:
      return null
  }
}

function describeRollback(payload: Payload): string | null {
  const from = version(payload, 'fromVersion')
  const target = version(payload, 'targetVersion')
  return from === null || target === null
    ? null
    : `Rolled back from version ${String(from)} to version ${String(target)}`
}

/** A whole, non-negative count (tokens, attempts, milliseconds), or `null`. */
function count(payload: Payload, key: string): number | null {
  return version(payload, key)
}

/** Agent run events (FR-23, FR-25, FR-26); payloads from AgentRunner. */
function describeRun(type: string, payload: Payload): string | null {
  const agent = oneOf(payload, 'agent', agentKinds)
  if (!agent) {
    return null
  }
  const run = `${agentKindLabels[agent]} run`
  switch (type) {
    case 'RUN_STARTED': {
      const model = typeof payload.model === 'string' && payload.model ? payload.model : null
      return model ? `${run} started on ${model}` : `${run} started`
    }
    case 'RUN_COMPLETED': {
      const input = count(payload, 'inputTokens')
      const output = count(payload, 'outputTokens')
      const ms = count(payload, 'durationMs')
      if (input === null || output === null) {
        return `${run} completed`
      }
      const tokens = `${formatNumber(input + output)} tokens`
      return ms === null ? `${run} completed: ${tokens}` : `${run} completed: ${tokens} in ${formatDuration(ms)}`
    }
    case 'RUN_FAILED': {
      const attempts = count(payload, 'attempts')
      return attempts === null || attempts === 0
        ? `${run} failed`
        : `${run} failed after ${String(attempts)} attempt${attempts === 1 ? '' : 's'}`
    }
    case 'BUDGET_EXCEEDED': {
      const used = count(payload, 'tokensUsed')
      const budget = count(payload, 'tokenBudget')
      const detail = used !== null && budget !== null ? ` (${formatNumber(used)} of ${formatNumber(budget)} tokens used)` : ''
      return `${run} refused: the token budget is used up${detail}`
    }
    default:
      return null
  }
}

/** Events whose payload names an artifact type and version (e.g. `ARTIFACT_APPROVED`). */
function describeArtifact(type: string, payload: Payload): string | null {
  const artifactType = oneOf(payload, 'type', artifactTypes)
  const v = version(payload, 'version')
  return artifactType && v !== null ? `${humanizeType(type)}: ${artifactTypeLabels[artifactType]} v${String(v)}` : null
}

/**
 * FR-50: the one-line description shown in the activity feed, built from the event's type and payload with the same
 * labels the rest of the UI uses. Unknown types and payloads that don't match the expected shape fall back to the
 * server's `summary`.
 */
export function describeActivity(item: Pick<ActivityItem, 'type' | 'payload' | 'summary'>): string {
  const { type, payload } = item
  let text: string | null
  switch (type) {
    case 'PHASE_CHANGED':
      text = describePhaseChange(payload)
      break
    case 'GATE_CREATED':
    case 'GATE_APPROVED':
    case 'GATE_REJECTED':
    case 'GATE_CANCELLED':
    case 'GATE_OVERDUE':
      text = describeGate(type, payload)
      break
    case 'ROLLBACK_COMPLETED':
      text = describeRollback(payload)
      break
    case 'RUN_STARTED':
    case 'RUN_COMPLETED':
    case 'RUN_FAILED':
    case 'BUDGET_EXCEEDED':
      text = describeRun(type, payload)
      break
    default:
      text = describeArtifact(type, payload)
  }
  return text ?? item.summary
}
