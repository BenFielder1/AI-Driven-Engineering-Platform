import { zodResolver } from '@hookform/resolvers/zod'
import { useNavigate } from '@tanstack/react-router'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { useForm, useWatch, type Path } from 'react-hook-form'
import { Check } from 'lucide-react'

import type { Schemas } from '../../api/client'
import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { RadioGroup } from '../../components/ui/radio-group'
import { panelClass } from '../../components/shared/panel-class'
import { PageHeader } from '../../components/ui/states'
import { cn } from '../../lib/utils'
import { TextAreaField } from '../../components/ui/textarea-field'
import { TextField } from '../../components/ui/text-field'
import { errorMessage, isApiError } from '../../lib/errors'
import { normalizeVersion } from '../versions/semver'
import { approvalModeDescriptions, approvalModeLabels, approvalModes, gitProviderLabels } from './labels'
import { useCreateProject } from './projects-api'
import { limits, toBudget, wizardDefaults, wizardSchema, type WizardValues } from './schemas'

type Field = Path<WizardValues>

const fieldIds: Record<Field, string> = {
  name: 'project-name',
  idea: 'project-idea',
  approvalMode: 'project-approval-mode',
  complianceThreshold: 'project-threshold',
  maxFilesPerStep: 'project-max-files',
  maxReviewCycles: 'project-max-cycles',
  tokenBudget: 'project-token-budget',
  gitProvider: 'project-git-provider',
  repoUrl: 'project-repo-url',
  defaultBranch: 'project-default-branch',
  gitToken: 'project-git-token',
  initialVersion: 'project-initial-version',
}

interface Step {
  title: string
  fields: readonly Field[]
}

const steps: readonly Step[] = [
  { title: 'Project idea', fields: ['name', 'idea'] },
  {
    title: 'Configuration',
    fields: ['approvalMode', 'complianceThreshold', 'maxFilesPerStep', 'maxReviewCycles', 'tokenBudget'],
  },
  { title: 'Git repository', fields: ['gitProvider', 'repoUrl', 'defaultBranch', 'gitToken'] },
  { title: 'Version', fields: ['initialVersion'] },
  { title: 'Review & create', fields: [] },
]

const LAST_STEP = steps.length - 1

/** Server VALIDATION keys (camelCase, possibly `git.`-prefixed) → wizard fields. */
const serverFieldMap: Record<string, Field> = {
  name: 'name',
  idea: 'idea',
  approvalmode: 'approvalMode',
  compliancethreshold: 'complianceThreshold',
  maxfilesperstep: 'maxFilesPerStep',
  maxreviewcycles: 'maxReviewCycles',
  tokenbudget: 'tokenBudget',
  'git.provider': 'gitProvider',
  'git.repourl': 'repoUrl',
  'git.defaultbranch': 'defaultBranch',
  'git.token': 'gitToken',
  git: 'gitProvider',
  initialversion: 'initialVersion',
  version: 'initialVersion',
}

function firstStepWithError(errors: Partial<Record<Field, unknown>>): number {
  const index = steps.findIndex((s) => s.fields.some((f) => errors[f]))
  return index === -1 ? LAST_STEP : index
}

function toRequest(v: WizardValues): Schemas['CreateProjectRequest'] {
  const github = v.gitProvider === 'github'
  return {
    name: v.name.trim(),
    idea: v.idea.trim(),
    approvalMode: v.approvalMode,
    complianceThreshold: Number(v.complianceThreshold),
    maxFilesPerStep: Number(v.maxFilesPerStep),
    maxReviewCycles: Number(v.maxReviewCycles),
    tokenBudget: toBudget(v.tokenBudget),
    initialVersion: normalizeVersion(v.initialVersion) ?? v.initialVersion.trim(),
    git: {
      provider: v.gitProvider,
      repoUrl: github ? v.repoUrl.trim() : null,
      defaultBranch: v.defaultBranch.trim(),
      token: github && v.gitToken ? v.gitToken : null,
    },
  }
}

/** /projects/new (FR-5, FR-66): five steps with per-step validation; Create navigates to the new project. */
export function NewProjectWizard() {
  const navigate = useNavigate()
  const create = useCreateProject()
  const [step, setStep] = useState(0)
  const [serverError, setServerError] = useState<string | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)
  /** False until the first step change, so the page heading (not the step heading) has focus on arrival. */
  const [moveFocus, setMoveFocus] = useState(false)

  const {
    register,
    handleSubmit,
    trigger,
    getValues,
    control,
    setError,
    formState: { errors },
  } = useForm<WizardValues>({
    resolver: zodResolver(wizardSchema),
    defaultValues: wizardDefaults,
    mode: 'onTouched',
  })

  // Focus the new step's heading so keyboard and screen-reader users start at the top of it.
  useEffect(() => {
    if (moveFocus) {
      headingRef.current?.focus()
    }
  }, [step, moveFocus])

  const goTo = (next: number) => {
    setMoveFocus(true)
    setServerError(null)
    setStep(next)
  }

  const current = steps[step] ?? steps[0]
  const provider = useWatch({ control, name: 'gitProvider' })

  async function next() {
    const valid = await trigger(current?.fields ?? [], { shouldFocus: true })
    if (valid) {
      goTo(step + 1)
    }
  }

  const submit = handleSubmit(
    (values) => {
      setServerError(null)
      create.mutate(toRequest(values), {
        onSuccess: (project) => {
          void navigate({ to: '/projects/$projectId', params: { projectId: project.id } })
        },
        onError: (error) => {
          if (isApiError(error, 'VALIDATION')) {
            const unmatched: string[] = []
            const matched: Partial<Record<Field, string>> = {}
            for (const [key, messages] of Object.entries(error.fieldErrors)) {
              const field = serverFieldMap[key.toLowerCase()]
              if (field) {
                matched[field] = messages.join(' ')
                setError(field, { type: 'server', message: messages.join(' ') })
              } else {
                unmatched.push(messages.join(' '))
              }
            }
            const target = firstStepWithError(matched)
            if (target !== LAST_STEP) {
              goTo(target)
            }
            setServerError(unmatched.length > 0 ? unmatched.join(' ') : errorMessage(error))
          } else if (isApiError(error, 'VERSION_INVALID')) {
            setError('initialVersion', { type: 'server', message: errorMessage(error) })
            goTo(steps.findIndex((s) => s.title === 'Version'))
          } else {
            setServerError(errorMessage(error))
          }
        },
      })
    },
    (invalid) => {
      goTo(firstStepWithError(invalid))
    },
  )

  const stepErrors = fieldErrorSummary(
    errors,
    Object.fromEntries((current?.fields ?? []).map((f) => [f, fieldIds[f]])) as Partial<Record<Field, string>>,
  )

  return (
    <>
      <PageHeader title="New project" />
      <StepIndicator current={step} />

      <form
        noValidate
        aria-labelledby="wizard-step-title"
        className={cn(panelClass, 'max-w-2xl p-4 sm:p-6')}
        onSubmit={(e) => {
          if (step < LAST_STEP) {
            e.preventDefault()
            void next()
          } else {
            void submit(e)
          }
        }}
      >
        <h2
          id="wizard-step-title"
          ref={headingRef}
          tabIndex={-1}
          className="mb-4 text-xl focus:outline-none"
        >
          Step {step + 1} of {steps.length}: {current?.title}
        </h2>

        <ErrorSummary title="Check this step" errors={stepErrors} />
        {serverError && (
          <Alert tone="error" role="alert" title="We couldn't create the project" className="mb-4">
            <p>{serverError}</p>
          </Alert>
        )}

        <div className="mt-2 flex flex-col gap-5">
          {step === 0 && (
            <>
              <TextField
                id={fieldIds.name}
                label="Project name"
                maxLength={limits.nameMax}
                autoComplete="off"
                error={errors.name?.message}
                {...register('name')}
              />
              <TextAreaField
                id={fieldIds.idea}
                label="Your idea"
                hint="What should the software do, and for whom? The refinement agent will ask follow-up questions."
                rows={8}
                maxLength={limits.ideaMax}
                error={errors.idea?.message}
                {...register('idea')}
              />
            </>
          )}

          {step === 1 && (
            <>
              <RadioGroup
                id={fieldIds.approvalMode}
                legend="Approval mode"
                inputProps={register('approvalMode')}
                error={errors.approvalMode?.message}
                options={approvalModes.map((m) => ({
                  value: m,
                  label: approvalModeLabels[m],
                  description: approvalModeDescriptions[m],
                }))}
              />
              <TextField
                id={fieldIds.complianceThreshold}
                label="Compliance threshold"
                hint="The review score (0.50–1.00) a release needs. Default 0.90."
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
                  hint="1–100. Default 20."
                  type="number"
                  inputMode="numeric"
                  min={limits.filesMin}
                  max={limits.filesMax}
                  step={1}
                  error={errors.maxFilesPerStep?.message}
                  {...register('maxFilesPerStep')}
                />
                <TextField
                  id={fieldIds.maxReviewCycles}
                  label="Max review cycles"
                  hint="1–10. Default 3."
                  type="number"
                  inputMode="numeric"
                  min={limits.cyclesMin}
                  max={limits.cyclesMax}
                  step={1}
                  error={errors.maxReviewCycles?.message}
                  {...register('maxReviewCycles')}
                />
              </div>
              <TextField
                id={fieldIds.tokenBudget}
                label="Token budget (optional)"
                hint="At least 1,000 tokens. Leave empty for no limit."
                type="number"
                inputMode="numeric"
                min={limits.budgetMin}
                step={1}
                error={errors.tokenBudget?.message}
                {...register('tokenBudget')}
              />
            </>
          )}

          {step === 2 && (
            <>
              <RadioGroup
                id={fieldIds.gitProvider}
                legend="Git provider"
                inputProps={register('gitProvider')}
                error={errors.gitProvider?.message}
                options={[
                  { value: 'github', label: gitProviderLabels.github, description: 'Use a repository on GitHub.' },
                  {
                    value: 'local',
                    label: gitProviderLabels.local,
                    description: 'AIDE keeps a Git repository for this project on its own data volume.',
                  },
                ]}
              />
              {provider === 'github' ? (
                <>
                  <TextField
                    id={fieldIds.repoUrl}
                    label="Repository URL"
                    hint="For example https://github.com/owner/repo"
                    type="url"
                    autoComplete="off"
                    maxLength={limits.urlMax}
                    error={errors.repoUrl?.message}
                    {...register('repoUrl')}
                  />
                  <TextField
                    id={fieldIds.gitToken}
                    label="Access token (optional)"
                    hint="Needs read and write access to the repository. It is stored encrypted and never shown again."
                    type="password"
                    autoComplete="new-password"
                    maxLength={limits.secretMax}
                    error={errors.gitToken?.message}
                    {...register('gitToken')}
                  />
                </>
              ) : (
                <Alert tone="info">
                  <p>
                    No repository to set up: AIDE creates and keeps a local Git repository for this project on its data
                    volume. Releases are tagged there instead of on GitHub.
                  </p>
                </Alert>
              )}
              <TextField
                id={fieldIds.defaultBranch}
                label="Default branch"
                maxLength={limits.branchMax}
                autoComplete="off"
                error={errors.defaultBranch?.message}
                {...register('defaultBranch')}
              />
            </>
          )}

          {step === 3 && (
            <TextField
              id={fieldIds.initialVersion}
              label="Initial version"
              hint="The first release version (semantic version, e.g. 0.1.0 or 1.0.0). The first release is tagged with it."
              autoComplete="off"
              spellCheck={false}
              maxLength={72}
              error={errors.initialVersion?.message}
              {...register('initialVersion')}
            />
          )}

          {step === LAST_STEP && <ReviewSummary values={getValues()} onEdit={goTo} />}
        </div>

        <div className="mt-6 flex flex-wrap justify-between gap-2">
          {step > 0 ? (
            <Button
              variant="secondary"
              onClick={() => {
                goTo(step - 1)
              }}
            >
              Back
            </Button>
          ) : (
            <span />
          )}
          {step < LAST_STEP ? (
            <Button type="submit">Next</Button>
          ) : (
            <Button type="submit" pending={create.isPending} pendingLabel="Creating…">
              Create project
            </Button>
          )}
        </div>
      </form>
    </>
  )
}

function StepIndicator({ current }: { current: number }) {
  return (
    <ol aria-label="Wizard steps" className="mb-6 flex flex-wrap items-center gap-2 text-sm">
      {steps.map((s, index) => (
        <li
          key={s.title}
          aria-current={index === current ? 'step' : undefined}
          className={cn(
            'flex min-h-9 items-center gap-2 rounded-full border px-3',
            index === current
              ? 'border-primary/60 bg-primary/10 font-medium text-foreground'
              : index < current
                ? 'border-border/60 bg-card/60 text-foreground'
                : 'border-dashed border-border/60 text-muted-foreground',
          )}
        >
          <span
            aria-hidden
            className={cn(
              'flex size-5 items-center justify-center rounded-full font-mono text-[11px]',
              index === current
                ? 'bg-primary text-primary-foreground'
                : index < current
                  ? 'bg-success/20 text-success'
                  : 'bg-muted text-muted-foreground',
            )}
          >
            {index < current ? (
              <Check className="size-3" />
            ) : index === current ? (
              <span className="size-1.5 rounded-full bg-primary-foreground" />
            ) : null}
          </span>
          <span className="sr-only">{index < current ? 'Completed: ' : ''}</span>
          {index + 1}. {s.title}
        </li>
      ))}
    </ol>
  )
}

function SummaryRow({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="grid gap-1 py-2 sm:grid-cols-3">
      <dt className="text-sm font-medium text-muted-foreground">{term}</dt>
      <dd className="whitespace-pre-wrap break-words sm:col-span-2">{children}</dd>
    </div>
  )
}

function SummarySection({ title, step, onEdit, children }: {
  title: string
  step: number
  onEdit: (step: number) => void
  children: ReactNode
}) {
  return (
    <section aria-label={title} className="rounded-lg border border-border/60 bg-background/40 p-4">
      <div className="flex items-center justify-between gap-2">
        <h3 className="font-semibold">{title}</h3>
        <Button
          variant="ghost"
          aria-label={`Edit ${title.toLowerCase()}`}
          onClick={() => {
            onEdit(step)
          }}
        >
          Edit
        </Button>
      </div>
      <dl className="divide-y divide-border/60">{children}</dl>
    </section>
  )
}

function ReviewSummary({ values, onEdit }: { values: WizardValues; onEdit: (step: number) => void }) {
  const github = values.gitProvider === 'github'
  return (
    <div className="flex flex-col gap-4">
      <SummarySection title="Project idea" step={0} onEdit={onEdit}>
        <SummaryRow term="Name">{values.name.trim()}</SummaryRow>
        <SummaryRow term="Idea">{values.idea.trim()}</SummaryRow>
      </SummarySection>
      <SummarySection title="Configuration" step={1} onEdit={onEdit}>
        <SummaryRow term="Approval mode">{approvalModeLabels[values.approvalMode]}</SummaryRow>
        <SummaryRow term="Compliance threshold">{Number(values.complianceThreshold).toFixed(2)}</SummaryRow>
        <SummaryRow term="Max files per step">{values.maxFilesPerStep}</SummaryRow>
        <SummaryRow term="Max review cycles">{values.maxReviewCycles}</SummaryRow>
        <SummaryRow term="Token budget">
          {values.tokenBudget.trim() ? `${Number(values.tokenBudget).toLocaleString()} tokens` : 'No limit'}
        </SummaryRow>
      </SummarySection>
      <SummarySection title="Git repository" step={2} onEdit={onEdit}>
        <SummaryRow term="Provider">{gitProviderLabels[values.gitProvider]}</SummaryRow>
        {github && <SummaryRow term="Repository URL">{values.repoUrl.trim()}</SummaryRow>}
        <SummaryRow term="Default branch">{values.defaultBranch.trim()}</SummaryRow>
        {github && <SummaryRow term="Access token">{values.gitToken ? 'Provided' : 'Not provided'}</SummaryRow>}
      </SummarySection>
      <SummarySection title="Version" step={3} onEdit={onEdit}>
        <SummaryRow term="Initial version">{normalizeVersion(values.initialVersion) ?? values.initialVersion.trim()}</SummaryRow>
      </SummarySection>
    </div>
  )
}
