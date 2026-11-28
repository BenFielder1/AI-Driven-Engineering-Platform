import type { TimelineItem } from './api/types'
import { formatCost, formatDuration, formatNumber, formatPromptVersion, humanise } from './format'
import { agentRunStatusLabels, artifactStatusLabels, branchLabel, followUpLabel, gateStatusText, itemLabel, type DefinitionLabels } from './labels'

/** One timeline entry in words: a one-line title and the facts under it. Labels come from the definition (FR-305). */
export interface EntryText {
  title: string
  facts: string[]
  /** A reason, comment or error to show in full. */
  note: string | null
  /** FR-363: a labelled construct transition (a route back or a veto), shown as a badge. */
  flag?: 'routed' | 'vetoed'
}

/** FR-363: the engine's route outcome and transition kind (FR-347, FR-344 `on_veto.to`). */
const ROUTE = 'route'

/** FR-363: the construct labels an entry carries: branch, variant, map item, follow-up and conversation turn. */
function constructFacts(item: TimelineItem): string[] {
  return compact([
    item.branch != null && item.branch !== item.variant && branchLabel(item.branch),
    item.variant != null && item.variant !== item.followUp && `variant ${item.variant}`,
    item.itemIndex != null && itemLabel(item.itemIndex),
    item.followUp != null && followUpLabel(item.followUp),
    item.turnIndex != null && `turn ${String(item.turnIndex)}${item.turnRole ? ` (${item.turnRole})` : ''}`,
  ])
}

const compact = (values: (string | null | undefined | false)[]) => values.filter((v): v is string => Boolean(v))

export function describeEntry(item: TimelineItem, labels: DefinitionLabels): EntryText {
  switch (item.kind) {
    case 'transition':
      return {
        title: `${labels.stage(item.fromStage)} → ${labels.stage(item.toStage)}`,
        facts: compact([
          item.transitionKind && item.transitionKind !== ROUTE && humanise(item.transitionKind),
          item.checkpoint != null && `checkpoint ${String(item.checkpoint)}`,
        ]),
        note: item.reason ?? null,
        ...(item.transitionKind === ROUTE ? { flag: 'routed' as const } : {}),
      }
    case 'stage_entered': {
      const vetoed = (item.vetoFindings?.length ?? 0) > 0
      return {
        title: `Entered ${labels.stage(item.stageId)}`,
        facts: compact([
          ...constructFacts(item),
          item.iteration != null && `entry ${String(item.iteration)}`,
          item.checkpoint != null && `checkpoint ${String(item.checkpoint)}`,
          item.outcome && item.outcome !== ROUTE && humanise(item.outcome),
        ]),
        note: vetoed ? (item.vetoFindings ?? []).join('\n') : null,
        ...(vetoed ? { flag: 'vetoed' as const } : item.outcome === ROUTE ? { flag: 'routed' as const } : {}),
      }
    }
    case 'agent_run': {
      const tokens =
        item.inputTokens != null && item.outputTokens != null
          ? `${formatNumber(item.inputTokens)} in / ${formatNumber(item.outputTokens)} out tokens`
          : null
      return {
        title: item.turnIndex != null
          ? `${item.agentKey ?? 'Agent'} turn in ${labels.stage(item.stageId)}`
          : `${item.agentKey ?? 'Agent'} run in ${labels.stage(item.stageId)}`,
        facts: compact([
          ...constructFacts(item),
          item.agentRunStatus && agentRunStatusLabels[item.agentRunStatus],
          item.model,
          item.promptVersion && `prompt ${formatPromptVersion(item.promptVersion)}`,
          tokens,
          item.cost != null && formatCost(item.cost),
          item.durationMs != null && formatDuration(item.durationMs),
        ]),
        note: item.error ?? null,
      }
    }
    case 'gate_created':
      return {
        title: `${labels.gate(item.gateId, item.gateType)} raised`,
        facts: compact([gateStatusText(item.gateStatus)]),
        note: null,
      }
    case 'gate_decided':
      return {
        title: `${labels.gate(item.gateId, item.gateType)} decided`,
        facts: compact([gateStatusText(item.gateStatus), item.decision && humanise(item.decision)]),
        note: item.comment ?? null,
      }
    case 'artifact_version':
      return {
        title: `${labels.artifact(item.artifactKey)} v${String(item.artifactVersion ?? '?')}`,
        facts: compact([
          ...constructFacts(item),
          item.artifactStatus && artifactStatusLabels[item.artifactStatus],
          item.contentHash?.slice(0, 12),
        ]),
        note: null,
      }
  }
}
