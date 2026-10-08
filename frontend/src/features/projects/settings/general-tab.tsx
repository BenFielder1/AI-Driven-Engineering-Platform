import { zodResolver } from '@hookform/resolvers/zod'
import { useState, type ReactNode } from 'react'
import { useForm, useWatch } from 'react-hook-form'

import type { Schemas } from '../../../api/client'
import { Alert, ErrorSummary } from '../../../components/ui/alert'
import { Button } from '../../../components/ui/button'
import { fieldErrorSummary } from '../../../components/ui/form-errors'
import { RadioGroup } from '../../../components/ui/radio-group'
import { TextField } from '../../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError } from '../../../lib/errors'
import { approvalModeDescriptions, approvalModeLabels, approvalModes, formatNumber } from '../labels'
import { useUpdateProject, type Project } from '../projects-api'
import { generalSchema, limits, toBudget, type GeneralValues } from '../schemas'

const fieldIds = {
  name: 'settings-name',
  approvalMode: 'settings-approval-mode',
  complianceThreshold: 'settings-threshold',
  maxFilesPerStep: 'settings-max-files',
  maxReviewCycles: 'settings-max-cycles',
  tokenBudget: 'settings-token-budget',
} as const

function toValues(p: Project): GeneralValues {
  return {
    name: p.name,
    approvalMode: p.approvalMode,
    complianceThreshold: p.complianceThreshold.toFixed(2),
    maxFilesPerStep: String(p.maxFilesPerStep),
    maxReviewCycles: String(p.maxReviewCycles),
    tokenBudget: p.tokenBudget === null ? '' : String(p.tokenBudget),
  }
}

/** Only changed fields are sent (FR-7 partial update); an emptied budget is sent as `clearTokenBudget`. */
function toPatch(p: Project, v: GeneralValues): Schemas['UpdateProjectRequest'] {
  const patch: Schemas['UpdateProjectRequest'] = {}
  if (v.name.trim() !== p.name) {
    patch.name = v.name.trim()
  }
  if (v.approvalMode !== p.approvalMode) {
    patch.approvalMode = v.approvalMode
  }
  if (Number(v.complianceThreshold) !== p.complianceThreshold) {
    patch.complianceThreshold = Number(v.complianceThreshold)
  }
  if (Number(v.maxFilesPerStep) !== p.maxFilesPerStep) {
    patch.maxFilesPerStep = Number(v.maxFilesPerStep)
  }
  if (Number(v.maxReviewCycles) !== p.maxReviewCycles) {
    patch.maxReviewCycles = Number(v.maxReviewCycles)
  }
  const budget = toBudget(v.tokenBudget)
  if (budget === null && p.tokenBudget !== null) {
    patch.clearTokenBudget = true
  } else if (budget !== null && budget !== p.tokenBudget) {
    patch.tokenBudget = budget
  }
  return patch
}

function Row({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="grid gap-1 py-2 sm:grid-cols-3">
      <dt className="text-sm font-medium text-muted">{term}</dt>
      <dd className="break-words sm:col-span-2">{children}</dd>
    </div>
  )
}

function budgetText(p: Project) {
  const used = `${formatNumber(p.tokensUsed)} used`
  return p.tokenBudget === null ? `No limit (${used})` : `${formatNumber(p.tokenBudget)} tokens (${used})`
}

/** Read-only General settings for Contributors and Viewers (FR-7: only Owners edit). */
export function GeneralReadOnly({ project }: { project: Project }) {
  return (
    <section aria-labelledby="general-title">
      <h2 id="general-title" className="text-xl font-semibold">
        General
      </h2>
      <p className="mb-3 mt-1 text-sm text-muted">Only project Owners can change these settings.</p>
      <dl className="divide-y divide-border">
        <Row term="Name">{project.name}</Row>
        <Row term="Approval mode">
          {approvalModeLabels[project.approvalMode]}
          <span className="block text-sm text-muted">{approvalModeDescriptions[project.approvalMode]}</span>
        </Row>
        <Row term="Compliance threshold">{project.complianceThreshold.toFixed(2)}</Row>
        <Row term="Max files per step">{project.maxFilesPerStep}</Row>
        <Row term="Max review cycles">{project.maxReviewCycles}</Row>
        <Row term="Token budget">{budgetText(project)}</Row>
      </dl>
    </section>
  )
}

/** General settings form for Owners (FR-7). */
export function GeneralForm({ project }: { project: Project }) {
  const update = useUpdateProject(project.id)
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const [saved, setSaved] = useState(false)
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    reset,
    control,
    formState: { errors, isSubmitted, isDirty },
  } = useForm<GeneralValues>({
    resolver: zodResolver(generalSchema),
    values: toValues(project),
    resetOptions: { keepDirtyValues: true },
  })

  const onSubmit = handleSubmit((values) => {
    setServerErrors([])
    setSaved(false)
    const patch = toPatch(project, values)
    if (Object.keys(patch).length === 0) {
      setSaved(true)
      return
    }
    update.mutate(patch, {
      onSuccess: (updated) => {
        reset(toValues(updated))
        setSaved(true)
      },
      onError: (error) => {
        const unmatched = applyFieldErrors(error, setError, Object.keys(fieldIds) as (keyof GeneralValues)[])
        setServerErrors(unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error)])
      },
    })
  })

  const budget = useWatch({ control, name: 'tokenBudget' })

  return (
    <form noValidate onSubmit={(e) => void onSubmit(e)} aria-labelledby="general-title" className="max-w-2xl">
      <h2 id="general-title" className="mb-4 text-xl font-semibold">
        General
      </h2>
      <ErrorSummary
        title="We couldn't save the settings"
        errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, fieldIds) : [])]}
      />
      <div role="status" aria-live="polite">
        {saved && !isDirty && (
          <Alert tone="success" className="mb-4">
            Settings saved.
          </Alert>
        )}
      </div>
      <div className="mt-2 flex flex-col gap-5">
        <TextField id={fieldIds.name} label="Project name" maxLength={limits.nameMax} error={errors.name?.message} {...register('name')} />
        <RadioGroup
          id={fieldIds.approvalMode}
          legend="Approval mode"
          inputProps={register('approvalMode')}
          error={errors.approvalMode?.message}
          options={approvalModes.map((m) => ({ value: m, label: approvalModeLabels[m], description: approvalModeDescriptions[m] }))}
        />
        <TextField
          id={fieldIds.complianceThreshold}
          label="Compliance threshold"
          hint="0.50–1.00"
          type="number"
          inputMode="decimal"
          min={limits.thresholdMin}
          max={limits.thresholdMax}
          step={0.01}
          error={errors.complianceThreshold?.message}
          {...register('complianceThreshold')}
        />
        <div className="grid gap-5 sm:grid-cols-2">
          <TextField
            id={fieldIds.maxFilesPerStep}
            label="Max files per step"
            hint="1–100"
            type="number"
            inputMode="numeric"
            min={limits.filesMin}
            max={limits.filesMax}
            error={errors.maxFilesPerStep?.message}
            {...register('maxFilesPerStep')}
          />
          <TextField
            id={fieldIds.maxReviewCycles}
            label="Max review cycles"
            hint="1–10"
            type="number"
            inputMode="numeric"
            min={limits.cyclesMin}
            max={limits.cyclesMax}
            error={errors.maxReviewCycles?.message}
            {...register('maxReviewCycles')}
          />
        </div>
        <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
          <TextField
            id={fieldIds.tokenBudget}
            label="Token budget"
            hint={`At least 1,000 tokens; empty means no limit. ${formatNumber(project.tokensUsed)} used so far.`}
            type="number"
            inputMode="numeric"
            min={limits.budgetMin}
            className="flex-1"
            error={errors.tokenBudget?.message}
            {...register('tokenBudget')}
          />
          {budget !== '' && (
            <Button
              variant="secondary"
              onClick={() => {
                setValue('tokenBudget', '', { shouldDirty: true })
              }}
            >
              Remove budget
            </Button>
          )}
        </div>
      </div>
      <div className="mt-6">
        <Button type="submit" pending={update.isPending} pendingLabel="Saving…">
          Save changes
        </Button>
      </div>
    </form>
  )
}
