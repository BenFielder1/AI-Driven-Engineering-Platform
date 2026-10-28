import { zodResolver } from '@hookform/resolvers/zod'
import { useId, useMemo, useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { z } from 'zod'

import { ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { TextAreaField } from '../../components/ui/textarea-field'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError, messageFor } from '../../lib/errors'
import { useProjectStack, useStackCatalog } from '../stack/stack-api'
import { StackEditor } from '../stack/stack-editor'
import { optionName, selectionFromComposition, toStackRequest, type StackSelection } from '../stack/stack-model'
import { checkNewVersion, normalizeVersion } from './semver'
import { VERSION_REQUEST_MAX } from './start-version-rules'
import { useStartReleaseVersion, type ReleaseVersion } from './versions-api'

function startVersionSchema(released: string | null, existing: readonly string[]) {
  return z.object({
    version: z.string().superRefine((value, ctx) => {
      const check = checkNewVersion(value, released, existing)
      if (!check.ok) {
        ctx.addIssue({ code: 'custom', message: check.message })
      }
    }),
    request: z
      .string()
      .trim()
      .min(1, { error: 'Describe the new features, improvements or fixes for this version.' })
      .max(VERSION_REQUEST_MAX, { error: `The request is at most ${VERSION_REQUEST_MAX.toLocaleString()} characters.` }),
  })
}

type Values = z.infer<ReturnType<typeof startVersionSchema>>

/** Server codes that are about the version field (FR-61). */
const versionCodes = ['VERSION_INVALID', 'VERSION_NOT_GREATER', 'TAG_EXISTS'] as const

/**
 * FR-60, FR-61 "Start new version" (SPEC §7.2 dialog, Owner): the version (prefilled with the suggested next minor,
 * validated live: semver with a leading `v` stripped, greater than the released version), the request, and a summary
 * of what happens. Server errors go through the one error map; version errors land on the version field. FR-117:
 * "Change stack" opens the same five sub-steps, pre-filled with the current composition, and the new version is
 * started with that `stack` (omitted: the base version's composition is kept).
 */
export function StartVersionDialog({
  projectId,
  released,
  suggested,
  versions,
  onClose,
  onStarted,
}: {
  projectId: string
  released: string | null
  suggested: string
  versions: readonly ReleaseVersion[]
  onClose: () => void
  onStarted: (version: ReleaseVersion) => void
}) {
  const start = useStartReleaseVersion(projectId)
  const [serverError, setServerError] = useState<string | null>(null)
  const base = useId()
  const ids = { version: `${base}-version`, request: `${base}-request` }
  const existing = useMemo(() => versions.map((v) => v.version), [versions])
  const schema = useMemo(() => startVersionSchema(released, existing), [released, existing])
  const {
    register,
    handleSubmit,
    setError,
    control,
    formState: { errors, isSubmitted },
  } = useForm<Values>({ resolver: zodResolver(schema), mode: 'onChange', defaultValues: { version: suggested, request: '' } })

  const typed = useWatch({ control, name: 'version' })
  const normalised = normalizeVersion(typed)
  const lastReleased = versions.find((v) => v.version === released)
  const tagPrefix = lastReleased?.tagPrefix ?? versions[0]?.tagPrefix ?? 'v'

  // FR-117: the stack of the new version (null: keep the base version's composition, re-pinned to the current catalog).
  const projectStack = useProjectStack(projectId)
  const [editingStack, setEditingStack] = useState(false)
  const catalog = useStackCatalog(editingStack || projectStack.data?.composition != null)
  const [stackChange, setStackChange] = useState<StackSelection | null>(null)
  const stackLabel = (sel: StackSelection) =>
    [
      ['frontend', sel.frontend],
      ['backend', sel.backend],
      ['data', sel.data],
    ]
      .filter(([, option]) => option && option !== 'none')
      .map(([choice = '', option = '']) => optionName(catalog.data, choice, option))
      .join(' · ') || 'No frontend, backend or data layer'

  const onSubmit = handleSubmit((values) => {
    setServerError(null)
    start.mutate(
      {
        version: normalizeVersion(values.version) ?? values.version.trim(),
        request: values.request.trim(),
        ...(stackChange ? { stack: toStackRequest(stackChange, { omitVcs: (projectStack.data?.repoVcs ?? null) !== null }) } : {}),
      },
      {
        onSuccess: (result) => {
          onStarted(result.releaseVersion)
        },
        onError: (error) => {
          const code = versionCodes.find((c) => isApiError(error, c))
          if (code) {
            setError('version', { type: 'server', message: messageFor(code, 'startVersion') })
            return
          }
          const unmatched = applyFieldErrors(error, setError, ['version', 'request'])
          if (!isApiError(error, 'VALIDATION') || unmatched.length > 0) {
            setServerError(unmatched.length > 0 ? unmatched.join(' ') : errorMessage(error, 'startVersion'))
          }
        },
      },
    )
  })

  return (
    <ModalDialog
      open
      title="Start a new version"
      description="Develop new features, improvements or fixes on top of the released version."
      busy={start.isPending}
      onClose={onClose}
    >
      {editingStack && catalog.data && projectStack.data && (
        <section aria-label="Change stack" className="flex flex-col gap-3">
          <p className="text-sm text-muted-foreground">
            The stack of the new version. Changing a layer of code that already exists is a stack change: the specification
            gets a Stack Migration section.
          </p>
          <StackEditor
            catalog={catalog.data}
            initial={stackChange ?? selectionFromComposition(projectStack.data.composition)}
            vcsFixed={projectStack.data.repoVcs}
            submitLabel="Use this stack"
            onSubmit={(sel) => {
              setStackChange(sel)
              setEditingStack(false)
            }}
            onCancel={() => {
              setEditingStack(false)
            }}
          />
        </section>
      )}
      <form noValidate hidden={editingStack} onSubmit={(e) => void onSubmit(e)} className="flex flex-col gap-4">
        <ErrorSummary
          title="We couldn't start the version"
          errors={[...(serverError ? [{ message: serverError }] : []), ...(isSubmitted ? fieldErrorSummary(errors, ids) : [])]}
        />
        <TextField
          id={ids.version}
          label="New version"
          hint={released ? `Semantic version, greater than the released ${released}.` : 'Semantic version, for example 1.0.0.'}
          autoComplete="off"
          spellCheck={false}
          maxLength={72}
          error={errors.version?.message}
          {...register('version')}
        />
        <TextAreaField
          id={ids.request}
          label="What should this version add or fix?"
          hint="New features, improvements or fixes. This becomes the project request for the version."
          rows={5}
          maxLength={VERSION_REQUEST_MAX}
          error={errors.request?.message}
          {...register('request')}
        />
        {projectStack.data && (
          <div className="flex flex-col gap-1 text-sm" data-testid="start-version-stack">
            <p>
              <span className="font-medium">Stack: </span>
              {stackChange
                ? `${stackLabel(stackChange)} (changed)`
                : projectStack.data.composition
                  ? `${stackLabel(selectionFromComposition(projectStack.data.composition))} (kept)`
                  : 'No stack composition (kept)'}
            </p>
            <p className="flex flex-wrap gap-2">
              <Button
                variant="secondary"
                onClick={() => {
                  setEditingStack(true)
                }}
              >
                Change stack
              </Button>
              {stackChange && (
                <Button
                  variant="ghost"
                  onClick={() => {
                    setStackChange(null)
                  }}
                >
                  Keep the current stack
                </Button>
              )}
            </p>
          </div>
        )}
        <section aria-labelledby={`${base}-summary`} className="rounded-lg border border-border/60 p-3 text-sm">
          <h3 id={`${base}-summary`} className="font-semibold">
            What happens
          </h3>
          <ul className="mt-1 list-disc pl-5 text-muted-foreground">
            <li>
              Version {normalised ?? '…'} starts in development
              {released ? `, based on the released ${released}` : ''}. Its release will be tagged{' '}
              <code className="font-mono">{`${tagPrefix}${normalised ?? '…'}`}</code>.
            </li>
            <li>The project goes back to Refinement at checkpoint 1, with your request as its idea.</li>
            <li>The Repository Analyst summarises the existing code for the agents.</li>
            <li>{released ? `${released} stays released and read-only.` : 'Nothing released changes.'}</li>
          </ul>
        </section>
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" onClick={onClose} disabled={start.isPending}>
            Cancel
          </Button>
          <Button type="submit" pending={start.isPending} pendingLabel="Starting…">
            Start development
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}
