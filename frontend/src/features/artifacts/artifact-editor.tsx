import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import type { z } from 'zod'

import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { TextAreaField } from '../../components/ui/textarea-field'
import { applyFieldErrors, errorMessage } from '../../lib/errors'
import { artifactTypeLabels } from '../projects/labels'
import { draftSchema } from './artifact-schemas'
import { useCreateArtifactDraft, type Artifact, type ArtifactType } from './artifacts-api'

type Values = z.infer<typeof draftSchema>

/**
 * FR-18: edit the latest version's Markdown and save it as a new draft (writers). The new version's parent is the
 * latest one; an existing draft is superseded (§4.3), which the form says up front.
 */
export function ArtifactEditor({
  projectId,
  type,
  base,
  openDraftVersion,
  onCancel,
  onSaved,
}: {
  projectId: string
  type: ArtifactType
  /** The version being edited (null when writing the first version); read once, when the editor opens. */
  base: Pick<Artifact, 'version' | 'content'> | null
  /** The current draft that saving will supersede, if any. */
  openDraftVersion: number | null
  onCancel: () => void
  onSaved: (artifact: Artifact) => void
}) {
  // The version the edit started from, pinned when the editor opens: a refetch of the latest version (another save,
  // a live event) must not move the base under the user, or saving would silently replace that newer version.
  const [openedFrom] = useState(base)
  const create = useCreateArtifactDraft(projectId, type)
  const [serverError, setServerError] = useState<string | null>(null)
  const contentId = `artifact-${type}-content`
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitted },
  } = useForm<Values>({ resolver: zodResolver(draftSchema), defaultValues: { content: openedFrom?.content ?? '' } })

  const onSubmit = handleSubmit(({ content }) => {
    setServerError(null)
    if (openedFrom?.content === content) {
      setError('content', { type: 'unchanged', message: `Change the Markdown before saving: it matches v${String(openedFrom.version)}.` })
      return
    }
    create.mutate({ content, baseVersion: openedFrom?.version ?? null }, {
      onSuccess: onSaved,
      onError: (error) => {
        const unmatched = applyFieldErrors(error, setError, ['content'])
        setServerError(unmatched.length > 0 ? unmatched.join(' ') : errorMessage(error, 'createArtifactDraft'))
      },
    })
  })

  const label = artifactTypeLabels[type].toLowerCase()
  return (
    <form
      noValidate
      aria-label={`Edit ${label}`}
      onSubmit={(e) => void onSubmit(e)}
      className="flex flex-col gap-4 rounded-lg border border-border bg-surface-raised p-4"
    >
      <ErrorSummary
        title="We couldn't save the draft"
        errors={[
          ...(serverError ? [{ message: serverError }] : []),
          ...(isSubmitted ? fieldErrorSummary(errors, { content: contentId }) : []),
        ]}
      />
      {openDraftVersion !== null && (
        <Alert tone="warning">
          Saving supersedes the open draft v{openDraftVersion}: only one draft per artifact can wait for approval.
        </Alert>
      )}
      <TextAreaField
        id={contentId}
        label="Markdown"
        hint={
          openedFrom
            ? `Starting from v${String(openedFrom.version)}. Saving creates a new draft version with v${String(openedFrom.version)} as its parent; an Owner approves it.`
            : 'Saving creates the first draft version; an Owner approves it.'
        }
        rows={20}
        spellCheck
        error={errors.content?.message}
        {...register('content')}
      />
      <div className="flex flex-wrap justify-end gap-2">
        <Button variant="secondary" onClick={onCancel} disabled={create.isPending}>
          Cancel
        </Button>
        <Button type="submit" pending={create.isPending} pendingLabel="Saving…">
          Save as new draft
        </Button>
      </div>
    </form>
  )
}
