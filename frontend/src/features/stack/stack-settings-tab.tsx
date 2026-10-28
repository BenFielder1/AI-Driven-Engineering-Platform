import { useState } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage, isApiError, messageFor } from '../../lib/errors'
import type { Project } from '../projects/projects-api'
import { useProjectStack, useStackCatalog, useUpdateProjectStack, type ProjectStack, type StackCatalog } from './stack-api'
import type { StackFieldErrors } from './stack-composer'
import { StackEditor } from './stack-editor'
import { selectionFromComposition, stackFieldOfKey, stackLayerLabels, toStackRequest } from './stack-model'
import { CompositionRows, DerivedRows } from './stack-summary'

/** FR-117: why the stack can't change, per `lockedReason`. */
export const stackLockedMessages = {
  plan_locked: messageFor('STACK_LOCKED'),
  not_in_development: 'No version is in development. Change the stack when you start the next version.',
} as const

const versionStatusLabels: Readonly<Record<string, string>> = {
  in_development: 'in development',
  awaiting_merge: 'awaiting merge',
  released: 'released',
  imported: 'imported',
}

const shortHash = (hash: string | null) => (hash ? hash.replace(/^sha256:/, '').slice(0, 12) : '')

/** The pinned pack versions of the composition ("ASP.NET Core 1.0.0+aide.1"), from the catalog. */
function PackVersions({ catalog, stack }: { catalog: StackCatalog | undefined; stack: ProjectStack }) {
  const c = stack.composition
  if (!c) {
    return null
  }
  const entries: [string, string | null | undefined][] = [
    ['frontend', c.frontend?.pack],
    ['backend', c.backend?.pack],
    ['data', c.data?.pack],
    ['vcs', c.vcs.pack],
  ]
  const items = entries.flatMap(([choice, option]) => {
    const card = option ? catalog?.choices.find((ch) => ch.id === choice)?.options.find((o) => o.id === option) : undefined
    return card?.pack ? [{ pack: card.pack, name: card.name, version: card.packVersion }] : []
  })
  if (items.length === 0) {
    return null
  }
  return (
    <ul aria-label="Pack versions" className="flex flex-wrap gap-2 text-xs">
      {items.map((i) => (
        <li key={i.pack} className="rounded border border-border/60 px-2 py-0.5">
          {i.name} <span className="font-mono tabular-nums text-muted-foreground">{i.version ?? ''}</span>
        </li>
      ))}
    </ul>
  )
}

/**
 * /projects/:id/settings/stack (FR-117, SPEC §7.2): the composition pinned on the current release version per layer,
 * with its pack and catalog versions. Owners change it with the same five sub-steps while it is unlocked; the locked
 * state says why. A stack-change warning shows when the version builds on existing code, and a migrated project's
 * legacy preset is named. Contributors and Viewers read it.
 */
export function StackSettingsTab({ project, canChange }: { project: Project; canChange: boolean }) {
  const stack = useProjectStack(project.id)
  const catalog = useStackCatalog()
  const update = useUpdateProjectStack(project.id)
  const [editing, setEditing] = useState(false)
  const [serverError, setServerError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<StackFieldErrors>({})

  if (stack.isPending || catalog.isPending) {
    return <LoadingState label="Loading the stack…" />
  }
  if (stack.isError) {
    return <ErrorState error={stack.error} title="The stack couldn't load" onRetry={() => void stack.refetch()} />
  }
  if (catalog.isError) {
    return <ErrorState error={catalog.error} title="The stack catalog couldn't load" onRetry={() => void catalog.refetch()} />
  }
  const s = stack.data
  const selection = selectionFromComposition(s.composition)
  const lockedReason = s.locked ? (s.lockedReason ?? 'not_in_development') : null

  const save = (value: typeof selection) => {
    setServerError(null)
    setFieldErrors({})
    update.mutate(toStackRequest(value, { omitVcs: s.repoVcs !== null }), {
      onSuccess: () => {
        setEditing(false)
        toast.success('Stack saved.')
      },
      onError: (error) => {
        if (isApiError(error, 'VALIDATION')) {
          const mapped: StackFieldErrors = {}
          const unmatched: string[] = []
          for (const [key, messages] of Object.entries(error.fieldErrors)) {
            const field = stackFieldOfKey(key)
            if (field) {
              mapped[field] = messages.join(' ')
            } else {
              unmatched.push(messages.join(' '))
            }
          }
          setFieldErrors(mapped)
          setServerError(unmatched.length > 0 ? unmatched.join(' ') : errorMessage(error))
          return
        }
        if (isApiError(error, 'STACK_LOCKED')) {
          setEditing(false)
          void stack.refetch()
        }
        setServerError(errorMessage(error, 'changeStack'))
      },
    })
  }

  return (
    <div className="flex flex-col gap-4">
      <div>
        <h2 className="text-xl">Stack</h2>
        <p className="text-sm text-muted-foreground">
          Version {s.version} ({versionStatusLabels[s.versionStatus] ?? s.versionStatus}). The stack decides which
          specialists write the plan, the specification sections and the review checks.
        </p>
      </div>

      {serverError && (
        <Alert tone="error" role="alert" title="We couldn't change the stack">
          <p>{serverError}</p>
        </Alert>
      )}

      {lockedReason && (
        <Alert tone="info" title="The stack is locked">
          <p>{stackLockedMessages[lockedReason]}</p>
        </Alert>
      )}

      {s.stackChange.baseHasCode && (
        <Alert tone="warning" title="This version builds on existing code">
          <p>
            Changing a layer is a stack change: the specification gets a Stack Migration section, and the plan includes
            work for each changed layer&apos;s specialist.
          </p>
          {s.stackChange.layersChanged.length > 0 && (
            <p className="mt-1">
              Changed from the base code: {s.stackChange.layersChanged.map((l) => stackLayerLabels[l] ?? l).join(', ')}.
            </p>
          )}
        </Alert>
      )}

      {s.legacyPreset && (
        <p className="text-sm" data-testid="legacy-preset">
          Preset {s.legacyPreset.presetName}
          {s.legacyPreset.presetVersion ? `@${s.legacyPreset.presetVersion}` : ''} (retired, migrated)
        </p>
      )}

      {editing ? (
        <StackEditor
          catalog={catalog.data}
          initial={selection}
          vcsFixed={s.repoVcs}
          submitLabel="Save stack"
          pending={update.isPending}
          serverErrors={fieldErrors}
          onSubmit={save}
          onCancel={() => {
            setEditing(false)
            setServerError(null)
            setFieldErrors({})
          }}
        />
      ) : (
        <>
          <section aria-label="Current stack" className="rounded-lg border border-border/60 bg-background/40 p-4">
            {s.composition ? (
              <>
                <dl className="divide-y divide-border/60">
                  <CompositionRows catalog={catalog.data} value={selection} />
                  <DerivedRows catalog={catalog.data} composition={s.composition} />
                </dl>
                <div className="mt-3">
                  <PackVersions catalog={catalog.data} stack={s} />
                </div>
              </>
            ) : (
              <p className="text-sm">
                {s.legacyPreset ? 'This version uses a legacy preset instead of a stack composition.' : 'No stack (general): the generalist agent writes every plan step.'}
              </p>
            )}
            <p className="mt-3 text-xs text-muted-foreground">
              {s.catalogVersion ? (
                <>
                  Catalog {s.catalogVersion} <code className="font-mono">{shortHash(s.catalogHash)}</code>
                  {s.catalogVersion !== s.currentCatalogVersion && ` · the current catalog is ${s.currentCatalogVersion}; a new version re-pins to it`}
                </>
              ) : (
                <>Current catalog {s.currentCatalogVersion} <code className="font-mono">{shortHash(s.currentCatalogHash)}</code></>
              )}
            </p>
          </section>
          {canChange && !s.locked && (
            <p>
              <Button
                variant="secondary"
                onClick={() => {
                  setServerError(null)
                  setEditing(true)
                }}
              >
                Change stack
              </Button>
            </p>
          )}
        </>
      )}
    </div>
  )
}
