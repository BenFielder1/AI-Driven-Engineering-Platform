import type { TimelineItem } from './api/types'
import { formatCost, formatDuration, formatNumber, formatPromptVersion, humanise } from './format'
import { agentRunStatusLabels, artifactStatusLabels, gateStatusText, type DefinitionLabels } from './labels'

/** One timeline entry in words: a one-line title and the facts under it. Labels come from the definition (FR-305). */
export interface EntryText {
  title: string
  facts: string[]
  /** A reason, comment or error to show in full. */
  note: string | null
}

const compact = (values: (string | null | undefined | false)[]) => values.filter((v): v is string => Boolean(v))

export function describeEntry(item: TimelineItem, labels: DefinitionLabels): EntryText {
  switch (item.kind) {
    case 'transition':
      return {
        title: `${labels.stage(item.fromStage)} → ${labels.stage(item.toStage)}`,
        facts: compact([
          item.transitionKind && humanise(item.transitionKind),
          item.checkpoint != null && `checkpoint ${String(item.checkpoint)}`,
        ]),
        note: item.reason ?? null,
      }
    case 'stage_entered':
      return {
        title: `Entered ${labels.stage(item.stageId)}`,
        facts: compact([
          item.iteration != null && `entry ${String(item.iteration)}`,
          item.checkpoint != null && `checkpoint ${String(item.checkpoint)}`,
          item.outcome && humanise(item.outcome),
        ]),
        note: null,
      }
    case 'agent_run': {
      const tokens =
        item.inputTokens != null && item.outputTokens != null
          ? `${formatNumber(item.inputTokens)} in / ${formatNumber(item.outputTokens)} out tokens`
          : null
      return {
        title: `${item.agentKey ?? 'Agent'} run in ${labels.stage(item.stageId)}`,
        facts: compact([
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
        facts: compact([item.artifactStatus && artifactStatusLabels[item.artifactStatus], item.contentHash?.slice(0, 12)]),
        note: null,
      }
  }
}
