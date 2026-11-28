import { ErrorState, LoadingState } from '../../components/ui/states'
import { cn } from '../../lib/utils'
import { useRunArtifacts, useRunArtifactVersion } from '../api/hooks'
import type { StageDefinition, TranscriptTurn } from '../api/types'
import type { DefinitionLabels } from '../labels'
import { JsonBlock } from './json-block'

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)

/** FR-356: a conversation stage's output (`{ turns: [{ role, agentKey, text, agentRunId }] }`), or `null` if it isn't one. */
export function parseTranscript(content: string): TranscriptTurn[] | null {
  let parsed: unknown
  try {
    parsed = JSON.parse(content)
  } catch {
    return null
  }
  if (!isRecord(parsed) || !Array.isArray(parsed.turns)) {
    return null
  }
  const turns: TranscriptTurn[] = []
  for (const t of parsed.turns) {
    if (!isRecord(t) || typeof t.text !== 'string') {
      return null
    }
    turns.push({
      role: typeof t.role === 'string' ? t.role : '',
      agentKey: typeof t.agentKey === 'string' ? t.agentKey : '',
      text: t.text,
      agentRunId: typeof t.agentRunId === 'string' ? t.agentRunId : null,
    })
  }
  return turns
}

/**
 * FR-356, FR-363: turns as a chat: each turn's speaker (role, then agent key) and text, alternating sides by participant
 * order so the two sides of a conversation are easy to follow. Colour and side are decorative; the speaker is text.
 */
export function TranscriptView({ turns, label, participants }: { turns: readonly TranscriptTurn[]; label: string; participants: readonly string[] }) {
  return (
    <ol aria-label={label} className="flex flex-col gap-2" data-testid="transcript">
      {turns.map((turn, i) => {
        const side = Math.max(0, participants.indexOf(turn.agentKey)) % 2
        const speaker = turn.role || turn.agentKey || 'Participant'
        return (
          <li
            key={turn.agentRunId ?? i}
            className={cn(
              'flex max-w-[85%] flex-col gap-1 rounded-lg border border-border/60 p-3 text-sm',
              side === 0 ? 'self-start bg-muted' : 'self-end bg-card',
            )}
          >
            <p className="text-xs text-muted-foreground">
              <span className="font-medium text-foreground">{speaker}</span>
              {turn.role && turn.agentKey && <span> · {turn.agentKey}</span>}
              <span className="sr-only">, turn {String(i + 1)}</span>
            </p>
            <p className="whitespace-pre-wrap break-words">{turn.text}</p>
          </li>
        )
      })}
    </ol>
  )
}

/** FR-363: one version of a conversation's transcript artifact as a chat (as JSON when it isn't a transcript). */
export function TranscriptVersion({
  runId,
  artifactKey,
  version,
  title,
  participants,
}: {
  runId: string
  artifactKey: string
  version: number
  title: string
  participants: readonly string[]
}) {
  const shown = useRunArtifactVersion(runId, artifactKey, version)
  const label = `${title} v${String(version)}`
  if (shown.isPending) {
    return <LoadingState label={`Loading ${label}…`} />
  }
  if (shown.isError) {
    return <ErrorState error={shown.error} onRetry={() => void shown.refetch()} title={`${label} couldn't load`} />
  }
  const turns = parseTranscript(shown.data.content)
  if (!turns) {
    return <JsonBlock label={`${label} (JSON)`} text={shown.data.content} />
  }
  if (turns.length === 0) {
    return <p className="text-sm text-muted-foreground">The conversation has no turns.</p>
  }
  return <TranscriptView turns={turns} label={`Transcript: ${label}`} participants={participants} />
}

/**
 * FR-356, FR-363: a conversation stage's transcript: its output artifact's newest plain version as a chat. Before the
 * conversation stops there is no transcript yet (turns show in the timeline and replay as they run).
 */
export function StageTranscript({ runId, stage, labels }: { runId: string; stage: StageDefinition; labels: DefinitionLabels }) {
  const artifacts = useRunArtifacts(runId)
  const key = stage.output
  if (!stage.conversation || !key) {
    return null
  }
  const title = labels.artifact(key)
  if (artifacts.isPending) {
    return <LoadingState label={`Loading ${title}…`} />
  }
  if (artifacts.isError) {
    return <ErrorState error={artifacts.error} onRetry={() => void artifacts.refetch()} title={`${title} couldn't load`} />
  }
  const latest = artifacts.data.find((a) => a.key === key)?.versions.find((v) => v.variant == null && v.itemIndex == null)
  if (!latest) {
    return (
      <p className="text-sm text-muted-foreground">
        The transcript appears here when the conversation ends. Its turns show in the timeline as they run.
      </p>
    )
  }
  return (
    <TranscriptVersion
      runId={runId}
      artifactKey={key}
      version={latest.version}
      title={title}
      participants={stage.conversation.participants.map((p) => p.agent)}
    />
  )
}
