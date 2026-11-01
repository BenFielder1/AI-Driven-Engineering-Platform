import { useEffect, useState, type ReactNode } from 'react'

import { Alert } from '../../components/ui/alert'
import { errorMessage, isApiError } from '../../lib/errors'
import { useValidateStack, type StackCatalog, type StackComposition } from './stack-api'
import { isComplete, NONE, optionName, stackFieldOfKey, toStackRequest, type StackSelection } from './stack-model'
import type { StackFieldErrors } from './stack-composer'

/** Display names of the derived facts (§4.18). */
export const nextjsModeLabels: Readonly<Record<string, string>> = {
  'full-stack': 'Full-stack (no separate backend)',
  ui: 'UI for the backend',
}

/** FR-120: the generic specialists' display names (keys as the server sends them). */
export const specialistLabels: Readonly<Record<string, string>> = {
  generalist: 'Generalist',
  scaffolder: 'Scaffolder',
  'data-engineer': 'Data engineer',
  'backend-engineer': 'Backend engineer',
  'frontend-engineer': 'Frontend engineer',
  'test-engineer': 'Test engineer',
  'ci-engineer': 'CI engineer',
  'deploy-engineer': 'Deploy engineer',
  'docs-writer': 'Docs writer',
}

export const specialistLabel = (key: string) => specialistLabels[key] ?? key

function useDebounced<T>(value: T, ms = 300): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const t = setTimeout(() => {
      setDebounced(value)
    }, ms)
    return () => {
      clearTimeout(t)
    }
  }, [value, ms])
  return debounced
}

function Row({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="grid gap-1 py-1.5 sm:grid-cols-3">
      <dt className="text-sm text-muted-foreground">{term}</dt>
      <dd className="text-sm break-words sm:col-span-2">{children}</dd>
    </div>
  )
}

/** "React + Vite (frontend)" or "None" for a chosen option; "Not chosen yet" while empty. */
function chosenName(catalog: StackCatalog | undefined, choice: string, option: string, path?: string): string {
  if (!option) {
    return 'Not chosen yet'
  }
  const name = optionName(catalog, choice, option)
  return path && option !== NONE ? `${name} (${path})` : name
}

/** The composition per layer, as the summary card and Settings → Stack show it. */
export function CompositionRows({ catalog, value }: { catalog: StackCatalog | undefined; value: StackSelection }) {
  const host = (choice: string, option: string) => (option ? optionName(catalog, choice, option) : 'None (local only)')
  return (
    <>
      <Row term="Frontend">{chosenName(catalog, 'frontend', value.frontend, value.frontendPath.trim())}</Row>
      <Row term="Backend">{chosenName(catalog, 'backend', value.backend, value.backendPath.trim())}</Row>
      <Row term="Data">{chosenName(catalog, 'data', value.data)}</Row>
      <Row term="VCS">{chosenName(catalog, 'vcs', value.vcs)}</Row>
      <Row term="Frontend host">{host('frontendHost', value.frontendHost)}</Row>
      <Row term="Backend host">{host('backendHost', value.backendHost)}</Row>
      <Row term="Database host">{host('databaseHost', value.databaseHost)}</Row>
      <Row term="Local dev loop">{value.local ? optionName(catalog, 'local', value.local) : 'Default'}</Row>
    </>
  )
}

/** Derived facts of a validated composition (Next.js mode, origin model, specialists) and its caveats. */
export function DerivedRows({ catalog, composition }: { catalog: StackCatalog | undefined; composition: StackComposition }) {
  const { derived } = composition
  return (
    <>
      {derived.nextjsMode && <Row term="Next.js mode">{nextjsModeLabels[derived.nextjsMode] ?? derived.nextjsMode}</Row>}
      <Row term="Origin model">
        {composition.originModel ? optionName(catalog, 'originModel', composition.originModel) : 'None (API only)'}
      </Row>
      <Row term="Specialists">
        {derived.agents.length === 0 ? 'None' : derived.agents.map(specialistLabel).join(', ')}
      </Row>
      {composition.caveats.length > 0 && (
        <Row term="Caveats">
          <ul className="flex flex-col gap-1">
            {composition.caveats.map((c) => (
              <li key={`${c.layer}:${c.option}:${c.rule}`}>
                <span aria-hidden>⚠ </span>
                {c.text}
              </li>
            ))}
          </ul>
        </Row>
      )}
    </>
  )
}

/**
 * FR-114 live summary card: the composition, and once every layer up to VCS is chosen, `POST /api/stack/validate`'s
 * derived facts (Next.js mode, origin model, specialists) and caveats. Path errors from the server are reported to
 * `onFieldErrors` so the path fields show them; a STACK_INCOMPATIBLE answer lists its reasons.
 */
export function StackSummary({
  catalog,
  value,
  onFieldErrors,
}: {
  catalog: StackCatalog | undefined
  value: StackSelection
  onFieldErrors?: (errors: StackFieldErrors) => void
}) {
  const debounced = useDebounced(value)
  const request = isComplete(debounced) ? toStackRequest(debounced) : null
  const validation = useValidateStack(request)

  const error = validation.isError ? validation.error : null
  useEffect(() => {
    if (!onFieldErrors) {
      return
    }
    const fieldErrors: StackFieldErrors = {}
    if (isApiError(error, 'VALIDATION')) {
      for (const [key, messages] of Object.entries(error.fieldErrors)) {
        const field = stackFieldOfKey(key)
        if (field) {
          fieldErrors[field] = messages.join(' ')
        }
      }
    }
    onFieldErrors(fieldErrors)
  }, [error, onFieldErrors])

  return (
    <section aria-label="Stack summary" className="rounded-lg border border-border/60 bg-background/40 p-4">
      <h3 className="font-semibold">Summary</h3>
      <dl className="mt-2 divide-y divide-border/60">
        <CompositionRows catalog={catalog} value={value} />
        {validation.data && request && <DerivedRows catalog={catalog} composition={validation.data} />}
      </dl>
      {!request && (
        <p className="mt-2 text-sm text-muted-foreground">
          Choose the frontend, backend, data and version control layers to see the specialists and the origin model.
        </p>
      )}
      {request && error && (
        <Alert tone={isApiError(error, 'STACK_INCOMPATIBLE') ? 'warning' : 'error'} className="mt-3">
          <p>{errorMessage(error)}</p>
        </Alert>
      )}
    </section>
  )
}
