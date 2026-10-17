import type { ReactNode } from 'react'

import { ErrorState, LoadingState } from '../../components/ui/states'
import { usePreset, type PresetDetail } from './presets-api'

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section aria-label={title} className="flex flex-col gap-1.5">
      <h4 className="text-sm font-semibold">{title}</h4>
      {children}
    </section>
  )
}

const muted = 'text-sm text-muted-foreground'

/**
 * A preset's content (FR-74, FR-76): its specialist agents and extra spec sections; `full` adds the review checklist,
 * deterministic rules, protected paths and required environment variables (admin detail).
 */
export function PresetContent({ preset, full = false }: { preset: PresetDetail; full?: boolean }) {
  return (
    <div className="flex flex-col gap-4">
      <Section title={`Specialist agents (${String(preset.agents.length)})`}>
        <ul className="flex flex-col gap-2">
          {preset.agents.map((a) => (
            <li key={a.key} className="text-sm">
              <span className="font-medium">{a.name}</span> <code className="text-xs text-muted-foreground">{a.key}</code>
              <p className={muted}>{a.description}</p>
              {full && a.owns.length > 0 && (
                <p className="text-xs break-all text-muted-foreground">
                  Owns: {a.owns.map((g) => <code key={g} className="mr-1">{g}</code>)}
                </p>
              )}
              {full && a.skills.length > 0 && <p className="text-xs text-muted-foreground">Skills: {a.skills.join(', ')}</p>}
            </li>
          ))}
        </ul>
      </Section>
      <Section title="Extra specification sections">
        {preset.specSections.length === 0 ? (
          <p className={muted}>None.</p>
        ) : (
          <ul className="flex flex-col gap-1.5">
            {preset.specSections.map((s) => (
              <li key={s.title} className="text-sm">
                <span className="font-medium">{s.title}</span>
                {full && <p className={muted}>{s.guidance}</p>}
              </li>
            ))}
          </ul>
        )}
      </Section>
      {full && (
        <>
          <Section title="Review checklist">
            {preset.review.checklist.length === 0 ? (
              <p className={muted}>None.</p>
            ) : (
              <ul className="list-disc pl-5 text-sm">
                {preset.review.checklist.map((c) => (
                  <li key={c.item}>
                    {c.item} <span className="text-xs text-muted-foreground">({c.category.replaceAll('_', ' ')}, {c.severity})</span>
                  </li>
                ))}
              </ul>
            )}
            {preset.review.deterministic.length > 0 && (
              <p className="text-xs text-muted-foreground">Deterministic rules: {preset.review.deterministic.join(', ')}</p>
            )}
          </Section>
          <Section title="Protected paths">
            {preset.protectedPaths.length === 0 ? (
              <p className={muted}>None.</p>
            ) : (
              <ul className="flex flex-col gap-1 text-sm">
                {preset.protectedPaths.map((p) => (
                  <li key={p.glob}>
                    <code className="text-xs">{p.glob}</code>
                    {p.existingOnBase && <span className="text-xs text-muted-foreground"> (existing files only)</span>}
                    {p.except.length > 0 && <span className="text-xs text-muted-foreground"> except {p.except.join(', ')}</span>}
                    {p.reason && <p className={muted}>{p.reason}</p>}
                  </li>
                ))}
              </ul>
            )}
          </Section>
          <Section title="Required environment variables">
            {preset.requiredEnv.length === 0 ? (
              <p className={muted}>None.</p>
            ) : (
              <ul className="flex flex-col gap-1 text-sm">
                {preset.requiredEnv.map((e) => (
                  <li key={e.key}>
                    <code className="text-xs">{e.key}</code>
                    <span className="text-xs text-muted-foreground">
                      {' '}
                      ({e.targets.join(', ')}
                      {e.sensitive ? ', sensitive' : ''})
                    </span>
                    {e.description && <p className={muted}>{e.description}</p>}
                  </li>
                ))}
              </ul>
            )}
          </Section>
        </>
      )}
    </div>
  )
}

/** Loads a preset and shows its content, with loading and error states. */
export function PresetDetails({ presetId, full = false }: { presetId: string; full?: boolean }) {
  const preset = usePreset(presetId)
  if (preset.isPending) {
    return <LoadingState label="Loading the preset…" />
  }
  if (preset.isError) {
    return <ErrorState error={preset.error} title="We couldn't load the preset" onRetry={() => void preset.refetch()} />
  }
  return <PresetContent preset={preset.data} full={full} />
}
