import { zodResolver } from '@hookform/resolvers/zod'
import { useNavigate } from '../../lib/navigation'
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
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
import { normalizeVersion, suggestNextVersion } from '../versions/semver'
import { buttonClasses } from '../../components/ui/button-classes'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { useAuthProviders } from '../auth/auth-api'
import { connectGitHubHref, useGitHubConnection, useGitHubRepoRefs, type GitHubRepo } from '../github/github-api'
import { GitHubErrorAlert } from '../github/github-error-alert'
import { RepoPicker } from '../github/repo-picker'
import { connectionLabel } from '../connections/connection-format'
import { useLlmProviders, type LlmProviderOption } from '../connections/connections-api'
import { useVercelConnections } from '../connections/vercel-connections-api'
import { VercelGitAlert } from '../deployments/vercel-git-alert'
import { suggestVercelProjectName } from '../deployments/vercel-project-rules'
import { PresetPicker } from '../presets/preset-picker'
import { usePresets } from '../presets/presets-api'
import { agentKeys, agentLabels, approvalModeDescriptions, approvalModeLabels, approvalModes, llmProviderLabels } from './labels'
import { catalogModel } from './model-picker'
import { useCreateProject } from './projects-api'
import { ReleasedVersionField } from './released-version-field'
import { deploymentFieldIds } from './wizard-deployment-ids'
import { WizardDeploymentStep } from './wizard-deployment-step'
import { defaultReleasedTag, releasedOptions, semverOfTag } from './released-versions'
import {
  limits,
  localSourcePathMax,
  NEVER_RELEASED,
  releasedTagOf,
  toBudget,
  wizardAgentModelFields,
  wizardDefaults,
  wizardSchema,
  type WizardValues,
} from './schemas'
import { clearWizardDraft, readWizardDraft, saveWizardDraft } from './wizard-draft'
import {
  defaultLlmProvider,
  initialModel,
  llmFieldIds,
  llmFields,
  llmValuesFor,
  showsConnectionPicker,
  toLlmRequest,
} from './wizard-llm'
import { WizardLlmSection } from './wizard-llm-section'
import { useStackCatalog, useStackDetection } from '../stack/stack-api'
import { StackComposer, stackLayers, type StackFieldErrors } from '../stack/stack-composer'
import { detectedAnything, emptyStackSelection, selectionFromDetection, stackFieldOfKey, type StackSelection } from '../stack/stack-model'
import { CompositionRows, StackSummary } from '../stack/stack-summary'
import {
  detectRequestOf,
  repoVcsOf,
  selectionOfValues,
  toWizardStackRequest,
  WIZARD_STACK_PREFIX,
  wizardStackFieldIds,
  wizardStackFieldList,
  wizardStackFields,
  detectionIdOf,
  stackSnapshot,
  wizardStackLayerFields,
  type WizardStackField,
} from './wizard-stack'

type Field = Path<WizardValues>
type Source = WizardValues['source']

const fieldIds: Record<Field, string> = {
  source: 'project-source',
  name: 'project-name',
  idea: 'project-idea',
  approvalMode: 'project-approval-mode',
  complianceThreshold: 'project-threshold',
  maxFilesPerStep: 'project-max-files',
  maxReviewCycles: 'project-max-cycles',
  tokenBudget: 'project-token-budget',
  gitMode: 'project-git-provider',
  repoUrl: 'project-repo-url',
  defaultBranch: 'project-default-branch',
  gitToken: 'project-git-token',
  repoFullName: 'project-repo-search',
  installationId: 'project-repo-search',
  repoHasCode: 'project-repo-search',
  initialVersion: 'project-initial-version',
  releasedTag: 'project-released-tag',
  releasedVersion: 'project-released-tag',
  developmentVersion: 'project-development-version',
  localSourcePath: 'project-local-source-path',
  presetId: 'project-preset',
  stackDetection: 'project-stack-kind',
  ...wizardStackFieldIds,
  ...deploymentFieldIds,
  ...llmFieldIds,
}

/**
 * SPEC §7.2 order: Source → Idea (or Request) → Repository → Version → Stack (FR-74) → Configuration → Deployment
 * (FR-88, optional) → Review & create.
 */
const stepKeys = ['source', 'idea', 'repository', 'version', 'stack', 'configuration', 'deployment', 'review'] as const
type StepKey = (typeof stepKeys)[number]

const stepFields: Record<StepKey, readonly Field[]> = {
  source: ['source'],
  idea: ['name', 'idea'],
  repository: ['gitMode', 'repoFullName', 'repoUrl', 'defaultBranch', 'gitToken', 'releasedTag', 'localSourcePath'],
  version: ['initialVersion', 'developmentVersion'],
  stack: ['stackKind', ...wizardStackFieldList, 'presetId'],
  configuration: ['approvalMode', 'complianceThreshold', 'maxFilesPerStep', 'maxReviewCycles', 'tokenBudget', ...llmFields],
  deployment: ['deploymentMode', 'vercelConnectionId', 'vercelProjectMode', 'vercelProjectName', 'vercelRootDirectory', 'vercelProjectId'],
  review: [],
}

function stepTitle(key: StepKey, source: Source): string {
  switch (key) {
    case 'source':
      return 'Source'
    case 'idea':
      return source === 'import' ? 'Request' : 'Project idea'
    case 'repository':
      return 'Repository'
    case 'version':
      return 'Version'
    case 'stack':
      return 'Stack'
    case 'configuration':
      return 'Configuration'
    case 'deployment':
      return 'Deployment'
    case 'review':
      return 'Review & create'
  }
}

const stepIndex = (key: StepKey) => stepKeys.indexOf(key)
const LAST_STEP = stepKeys.length - 1

/** A server VALIDATION key → wizard field: the map below, or a `stack.*` key of the composition (FR-114, FR-119). */
function wizardFieldOfKey(key: string): Field | undefined {
  const lower = key.toLowerCase()
  const mapped = serverFieldMap[lower]
  if (mapped) {
    return mapped
  }
  const stackField = lower.startsWith('stack.') ? stackFieldOfKey(lower) : undefined
  return stackField ? wizardStackFields[stackField] : undefined
}

/** Server VALIDATION keys (camelCase, possibly `git.`/`import.`-prefixed) → wizard fields. */
const serverFieldMap: Record<string, Field> = {
  source: 'source',
  name: 'name',
  idea: 'idea',
  approvalmode: 'approvalMode',
  compliancethreshold: 'complianceThreshold',
  maxfilesperstep: 'maxFilesPerStep',
  maxreviewcycles: 'maxReviewCycles',
  tokenbudget: 'tokenBudget',
  'git.mode': 'gitMode',
  'git.provider': 'gitMode',
  'git.repourl': 'repoUrl',
  'git.defaultbranch': 'defaultBranch',
  'git.token': 'gitToken',
  'git.installationid': 'repoFullName',
  'git.repofullname': 'repoFullName',
  'git.localsourcepath': 'localSourcePath',
  git: 'gitMode',
  initialversion: 'initialVersion',
  version: 'initialVersion',
  import: 'developmentVersion',
  'import.developmentversion': 'developmentVersion',
  'import.releasedtag': 'releasedTag',
  presetid: 'presetId',
  // FR-114: `stack.*` keys map through stackFieldOfKey; `stack` itself (e.g. presetId and stack together) is the kind.
  stack: 'stackKind',
  deployment: 'deploymentMode',
  // A Local project without the Fake provider: VALIDATION on the deployment (FR-88).
  provider: 'deploymentMode',
  'deployment.provider': 'deploymentMode',
  'deployment.connectionid': 'vercelConnectionId',
  'deployment.vercelprojectid': 'vercelProjectId',
  'deployment.create': 'vercelProjectName',
  'deployment.create.name': 'vercelProjectName',
  'deployment.create.rootdirectory': 'vercelRootDirectory',
  // FR-159: the AI model section.
  llm: 'llmProvider',
  'llm.provider': 'llmProvider',
  'llm.credentialid': 'llmCredentialId',
  'llm.defaultmodel': 'llmDefaultModel',
  'llm.modelsbyagent': 'llmModelIdeaRefinement',
  // Per-agent models (`llm.modelsByAgent.codeGenerator` → the Code generator field).
  ...Object.fromEntries(
    Object.entries(wizardAgentModelFields).map(([agent, field]) => [`llm.modelsbyagent.${agent.toLowerCase()}`, field]),
  ),
}

/**
 * Which credential a CREDENTIAL_NOT_OWNER / NOT_FOUND on create is about: the ProblemDetails `errors` key says so
 * (`llm.credentialId` vs `deployment.connectionId`). Without one, the old rule: an LLM credential unless Vercel is
 * chosen (CREDENTIAL_NOT_OWNER), or unless a PAT import's repository is the likely 404 (NOT_FOUND).
 */
function credentialErrorTarget(error: unknown, v: WizardValues, importing: boolean): 'llm' | 'other' {
  const keys = isApiError(error) ? Object.keys(error.fieldErrors).map((k) => k.toLowerCase()) : []
  if (keys.some((k) => k.startsWith('llm'))) {
    return 'llm'
  }
  if (keys.length > 0) {
    return 'other'
  }
  if (isApiError(error, 'CREDENTIAL_NOT_OWNER')) {
    return v.deploymentMode === 'vercel' ? 'other' : 'llm'
  }
  return importing && v.gitMode === 'github_pat' ? 'other' : 'llm'
}

function firstStepWithError(errors: Partial<Record<Field, unknown>>): number {
  const index = stepKeys.findIndex((k) => stepFields[k].some((f) => errors[f]))
  return index === -1 ? LAST_STEP : index
}

/** FR-86: `git` per mode; `mode` only (the v1 `provider` alias is deprecated). */
function toGitRequest(v: WizardValues): Schemas['CreateProjectGitRequest'] {
  const defaultBranch = v.defaultBranch.trim()
  switch (v.gitMode) {
    case 'github_app':
      return { mode: 'github_app', installationId: Number(v.installationId), repoFullName: v.repoFullName, defaultBranch }
    case 'github_pat':
      return { mode: 'github_pat', repoUrl: v.repoUrl.trim(), defaultBranch, token: v.gitToken ? v.gitToken : null }
    case 'local':
      // FR-73: a Local import names the existing repository on the server (development and E2E only).
      return v.source === 'import'
        ? { mode: 'local', defaultBranch, localSourcePath: v.localSourcePath.trim() }
        : { mode: 'local', defaultBranch }
  }
}

const versionText = (text: string) => normalizeVersion(text) ?? text.trim()

/** FR-88: the optional `deployment` (Vercel with an own connection and a new or existing project). */
function toDeploymentRequest(v: WizardValues): Schemas['CreateProjectDeploymentRequest'] | null {
  if (v.deploymentMode !== 'vercel') {
    return null
  }
  return {
    provider: 'vercel',
    connectionId: v.vercelConnectionId,
    ...(v.vercelProjectMode === 'create'
      ? { create: { name: v.vercelProjectName.trim(), rootDirectory: v.vercelRootDirectory.trim() || '.' } }
      : { vercelProjectId: v.vercelProjectId.trim() }),
  }
}

/**
 * FR-5, FR-66, FR-73: scratch sends `initialVersion`; import sends `import`, and `idea` is the version's request.
 * FR-114: `stack` for a composition; FR-74: `presetId` only for a legacy preset ("No stack (general)" sends neither).
 */
function toRequest(v: WizardValues): Schemas['CreateProjectRequest'] {
  const deployment = toDeploymentRequest(v)
  const llm = toLlmRequest(v)
  const base = {
    ...(v.stackKind === 'preset' && v.presetId ? { presetId: v.presetId } : {}),
    ...(v.stackKind === 'compose' ? { stack: toWizardStackRequest(v) } : {}),
    name: v.name.trim(),
    idea: v.idea.trim(),
    approvalMode: v.approvalMode,
    complianceThreshold: Number(v.complianceThreshold),
    maxFilesPerStep: Number(v.maxFilesPerStep),
    maxReviewCycles: Number(v.maxReviewCycles),
    tokenBudget: toBudget(v.tokenBudget),
    git: toGitRequest(v),
    // FR-88: `deployment` only when Vercel is chosen (skipping omits it).
    ...(deployment ? { deployment } : {}),
    // FR-159: the AI model section.
    ...(llm ? { llm } : {}),
  }
  return v.source === 'import'
    ? {
        ...base,
        source: 'import',
        import: { releasedTag: releasedTagOf(v), developmentVersion: versionText(v.developmentVersion) },
      }
    : { ...base, source: 'scratch', initialVersion: versionText(v.initialVersion) }
}

/** The released version as the summary says it: "v2.3.0", "release-2.3.0 (2.3.0)" or "Never released". */
function releasedLabel(v: Pick<WizardValues, 'releasedTag' | 'releasedVersion'>): string {
  const tag = releasedTagOf(v)
  if (tag === null) {
    return 'Never released'
  }
  return v.releasedVersion && normalizeVersion(tag) !== v.releasedVersion ? `${tag} (${v.releasedVersion})` : tag
}

/** `acme` of `acme/todo-app`, or undefined when nothing is picked. */
function repoOwner(fullName: string): string | undefined {
  const [owner = ''] = fullName.split('/')
  return owner === '' ? undefined : owner
}

const sourceLabels: Record<Source, string> = {
  scratch: 'Start from scratch',
  import: 'Import an existing repository',
}

/** The wizard's Git options (FR-86). */
const gitModeOptionLabels: Record<WizardValues['gitMode'], string> = {
  github_app: 'GitHub (AIDE GitHub App)',
  github_pat: 'Repository URL and access token',
  local: 'Local repository',
}

/**
 * FR-86 GitHub App option: the repo picker (with Create repository) once the user has connected GitHub, else Connect
 * GitHub. In "start from scratch", a repo with code gets the "AIDE will build on it" warning (it's treated as an
 * import, never released) and offers "Import instead".
 */
function AppRepository({
  connectionPending,
  connectionError,
  connected,
  installUrl,
  selected,
  hasCode,
  source,
  error,
  onSelect,
  onImportInstead,
}: {
  connectionPending: boolean
  connectionError: unknown
  connected: boolean
  installUrl: string | null
  selected: string
  hasCode: boolean
  source: Source
  error?: string | undefined
  onSelect: (repo: GitHubRepo) => void
  onImportInstead: () => void
}) {
  if (connectionPending) {
    return <LoadingState label="Checking your GitHub connection…" />
  }
  if (connectionError) {
    return <GitHubErrorAlert error={connectionError} title="We couldn't check your GitHub connection" returnUrl="/projects/new" />
  }
  if (!connected) {
    return (
      <Alert tone="info" title="Connect GitHub to pick a repository">
        <p>
          Connecting opens GitHub and brings you back to a new wizard (what you entered here isn&apos;t kept), so connect
          first, or choose another option.
        </p>
        <p className="mt-2">
          <a href={connectGitHubHref('/projects/new')} className={buttonClasses('primary')}>
            Connect GitHub
          </a>
        </p>
        {error && <p className="mt-2 text-destructive">{error}</p>}
      </Alert>
    )
  }
  return (
    <div className="flex flex-col gap-3">
      <RepoPicker searchId={fieldIds.repoFullName} selected={selected} installUrl={installUrl} error={error} onSelect={onSelect} />
      <p role="status" aria-live="polite" className="text-sm">
        {selected ? (
          <>
            Selected: <span className="font-medium">{selected}</span>
          </>
        ) : (
          <span className="text-muted-foreground">No repository selected yet.</span>
        )}
      </p>
      {selected && hasCode && source === 'scratch' && (
        <Alert tone="warning" title="This repository has code. AIDE will build on it">
          <p>
            The project is treated as an import of a repository that was never released: the Repository Analyst reads the
            code first, and the first specification covers what is already there. Import it instead to say which version
            is released.
          </p>
          <p className="mt-2">
            <Button variant="secondary" onClick={onImportInstead}>
              Import instead
            </Button>
          </p>
        </Alert>
      )}
    </div>
  )
}

/**
 * /projects/new (FR-5, FR-66, FR-73, FR-74, FR-86, FR-88): eight steps with per-step validation; "Import an existing repository" asks
 * for the request, the repository with its released version, and the version to develop. Create navigates to the
 * new project.
 */
export function NewProjectWizard() {
  const navigate = useNavigate()
  const create = useCreateProject()
  /**
   * FR-159: back from Connections ("Add a connection"), the wizard saved in session storage. The authenticated layout
   * renders pages only in the browser, so it is read on the first render (and removed once used).
   */
  const [draft] = useState(readWizardDraft)
  useEffect(() => {
    if (draft) {
      clearWizardDraft()
    }
  }, [draft])
  const [step, setStep] = useState(() => (draft ? Math.min(draft.step, LAST_STEP) : 0))
  /** The create failure, with the error itself so GitHub codes can offer their way out (install link, connect). */
  const [serverError, setServerError] = useState<{ message: string; error?: unknown } | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)
  /** False until the first step change, so the page heading (not the step heading) has focus on arrival. */
  const [moveFocus, setMoveFocus] = useState(false)

  const {
    register,
    handleSubmit,
    trigger,
    getValues,
    setValue,
    getFieldState,
    resetField,
    control,
    setError,
    formState: { errors },
  } = useForm<WizardValues>({
    resolver: zodResolver(wizardSchema),
    defaultValues: draft?.values ?? wizardDefaults,
    mode: 'onTouched',
  })

  /** FR-114: the Stack step's sub-step (Frontend → Backend → Data → VCS → Deployment). */
  const [stackSubStep, setStackSubStep] = useState(0)

  // Focus the new step's heading so keyboard and screen-reader users start at the top of it.
  useEffect(() => {
    if (moveFocus) {
      headingRef.current?.focus()
    }
  }, [step, stackSubStep, moveFocus])

  const goTo = (next: number) => {
    setMoveFocus(true)
    setServerError(null)
    setStep(next)
  }

  const key = stepKeys[step] ?? 'source'
  const source = useWatch({ control, name: 'source' })
  const importing = source === 'import'
  const gitMode = useWatch({ control, name: 'gitMode' })
  const repoFullName = useWatch({ control, name: 'repoFullName' })
  const repoHasCode = useWatch({ control, name: 'repoHasCode' })
  const releasedTag = useWatch({ control, name: 'releasedTag' })
  const releasedVersion = useWatch({ control, name: 'releasedVersion' })
  const presetId = useWatch({ control, name: 'presetId' })
  const deploymentMode = useWatch({ control, name: 'deploymentMode' })
  const vercelProjectMode = useWatch({ control, name: 'vercelProjectMode' })
  const vercelConnectionId = useWatch({ control, name: 'vercelConnectionId' })
  const llmProvider = useWatch({ control, name: 'llmProvider' })
  const stackKind = useWatch({ control, name: 'stackKind' })
  const stackValues: readonly string[] = useWatch({ control, name: wizardStackFieldList })
  const stackSelection = Object.fromEntries(
    Object.keys(wizardStackFields).map((k, i) => [k, stackValues[i] ?? '']),
  ) as unknown as StackSelection
  // Watched so detection re-reads the repository fields (FR-118).
  useWatch({ control, name: ['repoUrl', 'localSourcePath', 'defaultBranch', 'gitToken'] })

  // FR-88: suggest the new Vercel project's name from the project name when the step opens.
  useEffect(() => {
    if (key === 'deployment' && !getValues('vercelProjectName')) {
      setValue('vercelProjectName', suggestVercelProjectName(getValues('name')))
    }
  }, [key, getValues, setValue])

  // FR-86: the GitHub App option exists once an App is registered; it is the default when the user is connected.
  const providers = useAuthProviders()
  const appAvailable = providers.data?.githubMode === 'app'
  const connection = useGitHubConnection(appAvailable)
  const connected = connection.data?.connected === true && connection.data.status === 'valid'
  // A restored wizard keeps its Git option.
  const defaultApplied = useRef(draft !== null)
  useEffect(() => {
    if (!defaultApplied.current && appAvailable && connected && !getFieldState('gitMode').isDirty) {
      defaultApplied.current = true
      setValue('gitMode', 'github_app')
    }
  }, [appAvailable, connected, getFieldState, setValue])

  // The list may not know whether a repo is empty; its refs always do (FR-86). They also list releases and tags (FR-73).
  const refs = useGitHubRepoRefs(gitMode === 'github_app' && repoFullName ? repoFullName : null)
  const refsData = gitMode === 'github_app' ? refs.data : undefined
  useEffect(() => {
    if (refs.data) {
      setValue('repoHasCode', !refs.data.empty)
    }
  }, [refs.data, setValue])

  /**
   * Whether the user chose the released version / edited the version to develop. Kept here rather than read from
   * RHF's dirty state, which also turns true for programmatic prefills once any other field changes.
   */
  const [releasedChosen, setReleasedChosen] = useState(draft !== null)
  const [versionEdited, setVersionEdited] = useState(draft !== null)
  const resetReleased = useCallback(() => {
    setReleasedChosen(false)
    resetField('releasedTag')
  }, [resetField])

  // FR-73: preselect the latest release once a repo's refs are known, unless the user already chose.
  useEffect(() => {
    if (refsData && importing && !releasedChosen) {
      setValue('releasedTag', defaultReleasedTag(releasedOptions(refsData)))
    }
  }, [refsData, importing, releasedChosen, setValue])

  // A typed tag (URL + token, Local) doesn't carry over to the picker, and a picked one doesn't carry over to the field.
  const previousMode = useRef(gitMode)
  useEffect(() => {
    if (previousMode.current !== gitMode) {
      previousMode.current = gitMode
      resetReleased()
    }
  }, [gitMode, resetReleased])

  // Keep the released tag's semver in sync, and prefill the version to develop with its next minor (FR-73).
  useEffect(() => {
    setValue('releasedVersion', semverOfTag(releasedTag, refsData) ?? '')
  }, [releasedTag, refsData, setValue])
  useEffect(() => {
    if (!versionEdited) {
      setValue('developmentVersion', suggestNextVersion(releasedVersion || null))
    }
  }, [releasedVersion, versionEdited, setValue])

  const pickRepo = (repo: GitHubRepo) => {
    if (repo.fullName !== getValues('repoFullName')) {
      resetReleased()
    }
    setValue('repoFullName', repo.fullName, { shouldValidate: getFieldState('repoFullName').invalid })
    setValue('installationId', String(repo.installationId))
    setValue('repoHasCode', repo.empty === false)
    setValue('defaultBranch', repo.defaultBranch, { shouldValidate: true })
  }

  /** FR-86: a repo with code in "start from scratch" becomes an import that was never released. */
  const importInstead = () => {
    setValue('source', 'import', { shouldDirty: true })
    setReleasedChosen(true)
    setValue('releasedTag', NEVER_RELEASED, { shouldDirty: true })
  }

  // FR-158, FR-159: the AI model section. Keep a usable chosen provider (filling in its connection and model), else
  // preselect the default one. An unusable provider is never preselected.
  const llmProviders = useLlmProviders()
  const llmOptions = llmProviders.data
  // Back from Connections, the cached list may predate the new connection: refetch it, and wait for that answer
  // before keeping or replacing the intended provider.
  const [mountedAt] = useState(() => Date.now())
  const { refetch: refetchLlmProviders } = llmProviders
  useEffect(() => {
    if (draft) {
      void refetchLlmProviders()
    }
  }, [draft, refetchLlmProviders])
  const settledLlmOptions = !draft || llmProviders.dataUpdatedAt >= mountedAt ? llmOptions : undefined
  const applyLlm = useCallback(
    (values: ReturnType<typeof llmValuesFor> | Pick<WizardValues, 'llmProvider' | 'llmCredentialId' | 'llmDefaultModel'>) => {
      for (const [field, value] of Object.entries(values) as [keyof typeof values, string][]) {
        setValue(field, value)
      }
    },
    [setValue],
  )
  useEffect(() => {
    if (!settledLlmOptions) {
      return
    }
    const current = settledLlmOptions.find((p) => p.provider === getValues('llmProvider'))
    if (current?.usable) {
      const credential = getValues('llmCredentialId')
      if (showsConnectionPicker(current) && !current.connections.some((c) => c.id === credential)) {
        setValue('llmCredentialId', current.connections[0]?.id ?? '')
      } else if (!showsConnectionPicker(current) && credential) {
        setValue('llmCredentialId', '')
      }
      if (!getValues('llmDefaultModel').trim()) {
        setValue('llmDefaultModel', initialModel(current))
      }
      return
    }
    const fallback = defaultLlmProvider(settledLlmOptions)
    applyLlm(fallback ? llmValuesFor(fallback) : { llmProvider: '', llmCredentialId: '', llmDefaultModel: '' })
  }, [settledLlmOptions, getValues, setValue, applyLlm])

  const chooseLlmProvider = (option: LlmProviderOption) => {
    applyLlm(llmValuesFor(option))
  }

  /** "Add a connection": saves the wizard, with that provider as the intended choice, before Connections opens. */
  const addLlmConnection = (provider: LlmProviderOption['provider']) => {
    const noOverrides = Object.fromEntries(Object.values(wizardAgentModelFields).map((f) => [f, ''])) as Partial<WizardValues>
    saveWizardDraft(
      { ...getValues(), ...noOverrides, llmProvider: provider, llmCredentialId: '', llmDefaultModel: '' },
      step,
    )
  }

  // FR-114: the stack catalog (option cards) and the composition fields, set from the five sub-steps.
  const stackCatalog = useStackCatalog(step >= stepIndex('stack'))
  const [liveStackErrors, setLiveStackErrors] = useState<StackFieldErrors>({})
  const setStack = useCallback(
    (sel: StackSelection) => {
      for (const [k, field] of Object.entries(wizardStackFields) as [keyof StackSelection, WizardStackField][]) {
        if (getValues(field) !== sel[k]) {
          setValue(field, sel[k], { shouldDirty: true, shouldValidate: getFieldState(field).invalid })
        }
      }
    },
    [getValues, setValue, getFieldState],
  )
  const stackErrors: StackFieldErrors = { ...liveStackErrors }
  for (const [k, field] of Object.entries(wizardStackFields) as [keyof StackSelection, WizardStackField][]) {
    const message = errors[field]?.message
    if (message) {
      stackErrors[k] = message
    }
  }

  // FR-118: an import pre-fills the Stack step from the repository's files (never blocking). Detection runs only while
  // the Stack step is shown, and a result is applied only while the stack is untouched since the last applied one, so
  // a late answer or a new repository never overwrites the user's own choices.
  const detectRequest = importing && key === 'stack' ? detectRequestOf(getValues()) : null
  const detection = useStackDetection(detectRequest)
  const detectionKey = detection.data && detectRequest ? detectionIdOf(detectRequest, detection.data) : null
  const appliedDetection = useWatch({ control, name: 'stackDetection' })
  const lastApplied = useRef<string | null>(null)
  useEffect(() => {
    if (key !== 'stack' || !detection.data || !detectionKey || getValues('stackDetection') === detectionKey) {
      return
    }
    const values = getValues()
    lastApplied.current ??= stackSnapshot(draft?.values ?? wizardDefaults)
    if (stackSnapshot(values) !== lastApplied.current) {
      return
    }
    const found = detectedAnything(detection.data)
    setStack(found ? selectionFromDetection(detection.data, emptyStackSelection) : emptyStackSelection)
    setValue('presetId', '')
    setValue('stackKind', found ? 'compose' : 'general', { shouldDirty: true })
    setValue('stackDetection', detectionKey)
    lastApplied.current = stackSnapshot(getValues())
  }, [key, detection.data, detectionKey, draft, getValues, setStack, setValue])

  async function next() {
    if (key === 'stack' && getValues('stackKind') === 'compose') {
      const layers = stackLayers(stackCatalog.data)
      if (stackSubStep < layers.length - 1) {
        const fields = wizardStackLayerFields[layers[stackSubStep]?.id ?? ''] ?? []
        const valid = fields.length === 0 || (await trigger([...fields], { shouldFocus: true }))
        if (valid) {
          setMoveFocus(true)
          setServerError(null)
          setStackSubStep(stackSubStep + 1)
        }
        return
      }
    }
    const valid = await trigger(stepFields[key], { shouldFocus: true })
    if (valid) {
      goTo(step + 1)
    }
  }

  function back() {
    if (key === 'stack' && stackKind === 'compose' && stackSubStep > 0) {
      setMoveFocus(true)
      setStackSubStep(stackSubStep - 1)
      return
    }
    goTo(step - 1)
  }

  /** The sub-step of the first stack field with a server error (create's VALIDATION on `stack.*`). */
  const showStackField = (fields: readonly Field[]) => {
    const layers = stackLayers(stackCatalog.data)
    const index = layers.findIndex((l) => (wizardStackLayerFields[l.id] ?? []).some((f) => fields.includes(f)))
    if (index >= 0) {
      setStackSubStep(index)
    }
  }

  const failVersion = (field: 'developmentVersion' | 'releasedTag' | 'initialVersion', error: unknown) => {
    setError(field, { type: 'server', message: errorMessage(error, 'createProject') })
    goTo(stepIndex(field === 'releasedTag' ? 'repository' : 'version'))
  }

  const submit = handleSubmit(
    (values) => {
      setServerError(null)
      create.mutate(toRequest(values), {
        onSuccess: (project) => {
          void navigate({ to: '/projects/$projectId', params: { projectId: project.id } })
        },
        onError: (error) => {
          const importValues = values.source === 'import'
          if (isApiError(error, 'VALIDATION')) {
            const unmatched: string[] = []
            const matched: Partial<Record<Field, string>> = {}
            for (const [field, messages] of Object.entries(error.fieldErrors)) {
              const target = wizardFieldOfKey(field)
              if (target) {
                matched[target] = messages.join(' ')
                setError(target, { type: 'server', message: messages.join(' ') })
              } else {
                unmatched.push(messages.join(' '))
              }
            }
            const target = firstStepWithError(matched)
            if (target !== LAST_STEP) {
              goTo(target)
            }
            if (target === stepIndex('stack')) {
              showStackField(Object.keys(matched) as Field[])
            }
            setServerError({ message: unmatched.length > 0 ? unmatched.join(' ') : errorMessage(error) })
          } else if (isApiError(error, 'VERSION_INVALID')) {
            // Import: the version to develop, unless it is valid, in which case the released tag isn't a version.
            failVersion(
              !importValues
                ? 'initialVersion'
                : normalizeVersion(values.developmentVersion) && releasedTagOf(values) !== null
                  ? 'releasedTag'
                  : 'developmentVersion',
              error,
            )
          } else if (importValues && (isApiError(error, 'VERSION_NOT_GREATER') || isApiError(error, 'TAG_EXISTS'))) {
            failVersion('developmentVersion', error)
          } else if (importValues && values.gitMode === 'github_pat' && isApiError(error, 'NOT_FOUND')) {
            // FR-73: the repository to import doesn't exist, or the token can't see it.
            setError('repoUrl', { type: 'server', message: errorMessage(error, 'createProject') })
            goTo(stepIndex('repository'))
          } else if (isApiError(error, 'PRESET_NOT_FOUND')) {
            // FR-74: the preset was withdrawn since the list loaded; pick again from the fresh list.
            setError('presetId', { type: 'server', message: errorMessage(error, 'createProject') })
            goTo(stepIndex('stack'))
          } else if (isApiError(error, 'STACK_INCOMPATIBLE') || isApiError(error, 'VCS_MISMATCH')) {
            // FR-116, FR-117: the composition breaks a rule (the reasons are listed), or the VCS isn't the repo's host.
            goTo(stepIndex('stack'))
            setServerError({ message: errorMessage(error, 'createProject'), error })
          } else if (isApiError(error, 'LLM_PROVIDER_UNAVAILABLE')) {
            // FR-157, FR-159: the provider was turned off, or the user can no longer use it. Choose again.
            void llmProviders.refetch()
            setError('llmProvider', { type: 'server', message: errorMessage(error, 'createProject') })
            goTo(stepIndex('configuration'))
          } else if (
            values.llmCredentialId &&
            (isApiError(error, 'CREDENTIAL_NOT_OWNER') || isApiError(error, 'NOT_FOUND')) &&
            credentialErrorTarget(error, values, importValues) === 'llm'
          ) {
            // FR-159: the AI model section's connection isn't the caller's, or no longer exists.
            void llmProviders.refetch()
            setError('llmCredentialId', { type: 'server', message: errorMessage(error, 'createProjectLlm') })
            goTo(stepIndex('configuration'))
          } else if (isApiError(error, 'CREDENTIAL_NOT_OWNER')) {
            // FR-88: only the caller's own Vercel connections can be used.
            setError('vercelConnectionId', { type: 'server', message: errorMessage(error, 'createProject') })
            goTo(stepIndex('deployment'))
          } else if (isApiError(error, 'VERCEL_GIT_NOT_CONNECTED') || isApiError(error, 'VERCEL_PLAN_LIMIT')) {
            goTo(stepIndex('deployment'))
            setServerError({ message: errorMessage(error, 'createProject'), error })
          } else if (importValues && isApiError(error, 'TAG_NOT_FOUND')) {
            void refs.refetch()
            failVersion('releasedTag', error)
          } else {
            setServerError({ message: errorMessage(error, 'createProject'), error })
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
    Object.fromEntries(stepFields[key].map((f) => [f, fieldIds[f]])) as Partial<Record<Field, string>>,
  )
  const titles = stepKeys.map((k) => stepTitle(k, source))

  return (
    <>
      <PageHeader title="New project" />
      <StepIndicator current={step} titles={titles} />

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
          Step {step + 1} of {stepKeys.length}: {titles[step]}
        </h2>

        <ErrorSummary title="Check this step" errors={stepErrors} />
        {serverError && (
          <div className="mb-4">
            {isApiError(serverError.error, 'VERCEL_GIT_NOT_CONNECTED') ? (
              <VercelGitAlert error={serverError.error} title="We couldn't create the project" />
            ) : isApiError(serverError.error, 'GITHUB_APP_NOT_INSTALLED') ||
              isApiError(serverError.error, 'GITHUB_NOT_CONNECTED') ? (
              <GitHubErrorAlert
                error={serverError.error}
                title="We couldn't create the project"
                installTarget={repoOwner(getValues('repoFullName'))}
                returnUrl="/projects/new"
              />
            ) : (
              <Alert tone="error" role="alert" title="We couldn't create the project">
                <p>{serverError.message}</p>
              </Alert>
            )}
          </div>
        )}

        <div className="mt-2 flex flex-col gap-5">
          {key === 'source' && (
            <RadioGroup
              id={fieldIds.source}
              legend="Where does the code come from?"
              inputProps={register('source')}
              error={errors.source?.message}
              options={[
                {
                  value: 'scratch',
                  label: sourceLabels.scratch,
                  description: 'A new project: the agents build the first version from your idea.',
                },
                {
                  value: 'import',
                  label: sourceLabels.import,
                  description:
                    'A repository that already has code: the Repository Analyst summarises it, then the agents develop the next version from your request.',
                },
              ]}
            />
          )}

          {key === 'idea' && (
            <>
              <TextField
                id={fieldIds.name}
                label="Project name"
                maxLength={limits.nameMax}
                autoComplete="off"
                error={errors.name?.message}
                {...register('name')}
              />
              {importing ? (
                <TextAreaField
                  id={fieldIds.idea}
                  label="What should this version deliver?"
                  hint="New features, improvements or fixes. The refinement agent reads the existing code's summary and asks how the change fits it."
                  rows={8}
                  maxLength={limits.ideaMax}
                  error={errors.idea?.message}
                  {...register('idea')}
                />
              ) : (
                <TextAreaField
                  id={fieldIds.idea}
                  label="Your idea"
                  hint="What should the software do, and for whom? The refinement agent will ask follow-up questions."
                  rows={8}
                  maxLength={limits.ideaMax}
                  error={errors.idea?.message}
                  {...register('idea')}
                />
              )}
            </>
          )}

          {key === 'repository' && (
            <>
              <RadioGroup
                id={fieldIds.gitMode}
                legend={importing ? 'Where is the repository?' : 'Where should the code live?'}
                inputProps={register('gitMode')}
                error={errors.gitMode?.message}
                options={[
                  ...(appAvailable
                    ? [
                        {
                          value: 'github_app',
                          label: gitModeOptionLabels.github_app,
                          description: importing
                            ? 'Pick a repository the AIDE GitHub App can access. Recommended.'
                            : 'Pick or create a repository the AIDE GitHub App can access. Recommended.',
                        },
                      ]
                    : []),
                  {
                    value: 'github_pat',
                    label: gitModeOptionLabels.github_pat,
                    description: "For a GitHub repository the AIDE GitHub App can't be installed on.",
                  },
                  {
                    value: 'local',
                    label: gitModeOptionLabels.local,
                    description: importing
                      ? 'An existing Git repository on the AIDE server (development and testing only).'
                      : 'AIDE keeps a Git repository for this project on its own data volume.',
                  },
                ]}
              />
              {gitMode === 'github_app' && (
                <AppRepository
                  connectionPending={connection.isPending}
                  connectionError={connection.isError ? connection.error : null}
                  connected={connected}
                  installUrl={connection.data?.installUrl ?? null}
                  selected={repoFullName}
                  hasCode={repoHasCode}
                  source={source}
                  error={errors.repoFullName?.message}
                  onSelect={pickRepo}
                  onImportInstead={importInstead}
                />
              )}
              {gitMode === 'github_pat' && (
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
                    hint="A personal access token with Contents and Pull requests read and write. It is stored encrypted and never shown again."
                    type="password"
                    autoComplete="new-password"
                    maxLength={limits.secretMax}
                    error={errors.gitToken?.message}
                    {...register('gitToken')}
                  />
                </>
              )}
              {gitMode === 'local' &&
                (importing ? (
                  <TextField
                    id={fieldIds.localSourcePath}
                    label="Path of an existing repository on the server (development only)"
                    hint="A Git repository directory under the server's import root. Only available where the server allows local imports."
                    autoComplete="off"
                    spellCheck={false}
                    maxLength={localSourcePathMax}
                    error={errors.localSourcePath?.message}
                    {...register('localSourcePath')}
                  />
                ) : (
                  <Alert tone="info">
                    <p>
                      No repository to set up: AIDE creates and keeps a local Git repository for this project on its data
                      volume. Releases are tagged there instead of on GitHub.
                    </p>
                  </Alert>
                ))}
              <TextField
                id={fieldIds.defaultBranch}
                label="Default branch"
                hint={gitMode === 'github_app' && repoFullName ? 'Prefilled from the repository.' : undefined}
                maxLength={limits.branchMax}
                autoComplete="off"
                error={errors.defaultBranch?.message}
                {...register('defaultBranch')}
              />
              {importing && (
                <ReleasedVersionField
                  id={fieldIds.releasedTag}
                  listed={gitMode === 'github_app'}
                  repoSelected={Boolean(repoFullName)}
                  refs={refs}
                  inputProps={register('releasedTag', {
                    onChange: () => {
                      setReleasedChosen(true)
                    },
                  })}
                  error={errors.releasedTag?.message}
                />
              )}
            </>
          )}

          {key === 'version' &&
            (importing ? (
              <TextField
                id={fieldIds.developmentVersion}
                label="Version to develop"
                hint={
                  releasedVersion
                    ? `Greater than the released version ${releasedVersion}. Prefilled with its next minor; its release is tagged with it.`
                    : 'The repository was never released, so this becomes its first release (semantic version, e.g. 0.1.0 or 1.0.0).'
                }
                autoComplete="off"
                spellCheck={false}
                maxLength={72}
                error={errors.developmentVersion?.message}
                {...register('developmentVersion', {
                  onChange: () => {
                    setVersionEdited(true)
                    void trigger('developmentVersion')
                  },
                })}
              />
            ) : (
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
            ))}

          {key === 'stack' && (
            <>
              <RadioGroup
                id={fieldIds.stackKind}
                legend="How should the agents build it?"
                inputProps={register('stackKind', {
                  onChange: () => {
                    setValue('presetId', '')
                  },
                })}
                error={errors.stackKind?.message}
                options={[
                  {
                    value: 'general',
                    label: 'No stack (general)',
                    description:
                      'Any stack: the generalist agent writes every plan step, and the specification has the standard sections.',
                  },
                  {
                    value: 'compose',
                    label: 'Choose the stack',
                    description:
                      'Pick the frontend, backend, data, version control and deployment layers. A specialist agent works on each layer.',
                  },
                ]}
              />
              {importing && <DetectionNotice detection={detection} />}
              {stackKind === 'compose' &&
                (stackCatalog.isPending ? (
                  <LoadingState label="Loading the stack catalog…" />
                ) : stackCatalog.isError ? (
                  <ErrorState
                    error={stackCatalog.error}
                    title="The stack catalog couldn't load"
                    onRetry={() => void stackCatalog.refetch()}
                  />
                ) : (
                  <>
                    <StackComposer
                      catalog={stackCatalog.data}
                      value={stackSelection}
                      onChange={setStack}
                      subStep={stackSubStep}
                      onSubStepChange={setStackSubStep}
                      idPrefix={WIZARD_STACK_PREFIX}
                      vcsFixed={repoVcsOf(gitMode)}
                      vcsFixedReason="Set from the repository you chose (GitHub). It can only be the repository's host."
                      detection={importing && detectionKey !== null && detectionKey === appliedDetection ? detection.data : null}
                      errors={stackErrors}
                    />
                    <StackSummary catalog={stackCatalog.data} value={stackSelection} onFieldErrors={setLiveStackErrors} />
                  </>
                ))}
              <details
                className="rounded-lg border border-border/60 bg-background/40 p-3"
                open={stackKind === 'preset' ? true : undefined}
              >
                <summary className="min-h-6 cursor-pointer font-medium">Legacy presets (retiring in v4)</summary>
                <div className="mt-3">
                  <PresetPicker
                    id={fieldIds.presetId}
                    selected={presetId}
                    hideNone
                    inputProps={register('presetId', {
                      onChange: (e: { target: { value: string } }) => {
                        setValue('stackKind', e.target.value ? 'preset' : 'general')
                      },
                    })}
                    error={errors.presetId?.message}
                  />
                </div>
              </details>
            </>
          )}

          {key === 'configuration' && (
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
              <WizardLlmSection
                providers={llmProviders}
                control={control}
                register={register}
                errors={errors}
                provider={llmProvider}
                onChooseProvider={chooseLlmProvider}
                onAddConnection={addLlmConnection}
              />
            </>
          )}

          {key === 'deployment' && (
            <WizardDeploymentStep
              register={register}
              errors={errors}
              values={{ gitMode, presetId, deploymentMode, vercelProjectMode, vercelConnectionId }}
            />
          )}

          {key === 'review' && <ReviewSummary values={getValues()} llmOptions={llmOptions} onEdit={goTo} />}
        </div>

        <div className="mt-6 flex flex-wrap justify-between gap-2">
          {step > 0 ? (
            <Button variant="secondary" onClick={back}>
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

function StepIndicator({ current, titles }: { current: number; titles: readonly string[] }) {
  return (
    <ol aria-label="Wizard steps" className="mb-6 flex flex-wrap items-center gap-2 text-sm">
      {titles.map((title, index) => (
        <li
          key={stepKeys[index]}
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
          {index + 1}. {title}
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
  step: StepKey
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
            onEdit(stepIndex(step))
          }}
        >
          Edit
        </Button>
      </div>
      <dl className="divide-y divide-border/60">{children}</dl>
    </section>
  )
}

/** The picked preset's name and version (from the cached list), or "No preset (general)". */
function PresetName({ presetId }: { presetId: string }) {
  const presets = usePresets(Boolean(presetId))
  if (!presetId) {
    return <>No preset (general)</>
  }
  const preset = presets.data?.find((p) => p.id === presetId)
  return <>{preset ? `${preset.name} ${preset.version}` : presetId}</>
}

/** FR-114: the composition in the summary, layer by layer (names from the catalog). */
function StackReview({ values }: { values: WizardValues }) {
  const catalog = useStackCatalog()
  return <CompositionRows catalog={catalog.data} value={selectionOfValues(values)} />
}

/** FR-118: what import detection found (or that it couldn't run); it never blocks the step. */
function DetectionNotice({ detection }: { detection: ReturnType<typeof useStackDetection> }) {
  if (detection.isPending && detection.fetchStatus === 'idle') {
    return null
  }
  if (detection.isPending) {
    return (
      <p role="status" className="text-sm text-muted-foreground">
        Detecting the repository&apos;s stack…
      </p>
    )
  }
  if (detection.isError) {
    return (
      <Alert tone="info" role="status" title="We couldn't detect the stack">
        <p>{errorMessage(detection.error, 'detectStack')}</p>
      </Alert>
    )
  }
  const d = detection.data
  if (!detectedAnything(d)) {
    return (
      <p role="status" className="text-sm text-muted-foreground">
        Nothing was detected in the repository. Choose each layer yourself, or keep No stack (general).
      </p>
    )
  }
  return (
    <Alert tone="success" role="status" title="Stack detected">
      <p>
        The layers detected in <code className="font-mono">{d.ref}</code> are pre-filled and marked Detected. You can
        change any of them.
      </p>
      {d.truncated && <p className="mt-1">The repository has more files than AIDE listed, so some layers may be missing.</p>}
    </Alert>
  )
}

/** FR-88: the Deployment step in the summary ("Vercel", the connection, and the new or existing Vercel project). */
function DeploymentSummary({ values }: { values: WizardValues }) {
  const request = toDeploymentRequest(values)
  const connections = useVercelConnections(request !== null)
  if (!request) {
    return <SummaryRow term="Deployment">Skipped</SummaryRow>
  }
  const connection = connections.data?.find((c) => c.id === request.connectionId)
  return (
    <>
      <SummaryRow term="Provider">Vercel</SummaryRow>
      <SummaryRow term="Connection">{connection ? connectionLabel(connection) : request.connectionId}</SummaryRow>
      <SummaryRow term="Vercel project">
        {request.create
          ? `New project ${request.create.name} (root ${request.create.rootDirectory ?? '.'})`
          : `Existing project ${request.vercelProjectId ?? ''}`}
      </SummaryRow>
    </>
  )
}

/** FR-159: the AI model section in the summary (provider, connection, default model and any per-agent models). */
function LlmSummary({ values, options }: { values: WizardValues; options: readonly LlmProviderOption[] | undefined }) {
  if (!values.llmProvider) {
    return <SummaryRow term="AI provider">Not chosen</SummaryRow>
  }
  const option = options?.find((p) => p.provider === values.llmProvider)
  const catalog = option?.catalog ?? []
  const modelText = (id: string) => {
    const m = catalogModel(catalog, id)
    return m ? `${m.name} (${m.id})` : id
  }
  const connection = option?.connections.find((c) => c.id === values.llmCredentialId)
  return (
    <>
      <SummaryRow term="AI provider">{llmProviderLabels[values.llmProvider]}</SummaryRow>
      {values.llmCredentialId ? (
        <SummaryRow term="Connection">{connection ? connectionLabel(connection) : values.llmCredentialId}</SummaryRow>
      ) : (
        option?.reason === 'system_key' && <SummaryRow term="Connection">This server&apos;s key</SummaryRow>
      )}
      <SummaryRow term="Default model">{modelText(values.llmDefaultModel.trim())}</SummaryRow>
      {agentKeys.map((agent) => {
        const model = values[wizardAgentModelFields[agent]].trim()
        return model ? (
          <SummaryRow key={agent} term={`${agentLabels[agent]} model`}>
            {modelText(model)}
          </SummaryRow>
        ) : null
      })}
    </>
  )
}

function ReviewSummary({
  values,
  llmOptions,
  onEdit,
}: {
  values: WizardValues
  llmOptions: readonly LlmProviderOption[] | undefined
  onEdit: (step: number) => void
}) {
  const pat = values.gitMode === 'github_pat'
  const app = values.gitMode === 'github_app'
  const importing = values.source === 'import'
  return (
    <div className="flex flex-col gap-4">
      <SummarySection title="Source" step="source" onEdit={onEdit}>
        <SummaryRow term="Source">{sourceLabels[values.source]}</SummaryRow>
      </SummarySection>
      <SummarySection title={importing ? 'Request' : 'Project idea'} step="idea" onEdit={onEdit}>
        <SummaryRow term="Name">{values.name.trim()}</SummaryRow>
        <SummaryRow term={importing ? 'Request' : 'Idea'}>{values.idea.trim()}</SummaryRow>
      </SummarySection>
      <SummarySection title="Repository" step="repository" onEdit={onEdit}>
        <SummaryRow term="Mode">{gitModeOptionLabels[values.gitMode]}</SummaryRow>
        {app && <SummaryRow term="Repository">{values.repoFullName}</SummaryRow>}
        {app && values.repoHasCode && !importing && (
          <SummaryRow term="Existing code">AIDE builds on it (treated as an import that was never released)</SummaryRow>
        )}
        {pat && <SummaryRow term="Repository URL">{values.repoUrl.trim()}</SummaryRow>}
        {importing && values.gitMode === 'local' && (
          <SummaryRow term="Repository path">{values.localSourcePath.trim()}</SummaryRow>
        )}
        <SummaryRow term="Default branch">{values.defaultBranch.trim()}</SummaryRow>
        {pat && <SummaryRow term="Access token">{values.gitToken ? 'Provided' : 'Not provided'}</SummaryRow>}
        {importing && <SummaryRow term="Currently released version">{releasedLabel(values)}</SummaryRow>}
      </SummarySection>
      <SummarySection title="Version" step="version" onEdit={onEdit}>
        {importing ? (
          <SummaryRow term="Version to develop">{versionText(values.developmentVersion)}</SummaryRow>
        ) : (
          <SummaryRow term="Initial version">{versionText(values.initialVersion)}</SummaryRow>
        )}
      </SummarySection>
      <SummarySection title="Stack" step="stack" onEdit={onEdit}>
        {values.stackKind === 'compose' ? (
          <StackReview values={values} />
        ) : values.stackKind === 'preset' ? (
          <SummaryRow term="Legacy preset">
            <PresetName presetId={values.presetId} />
          </SummaryRow>
        ) : (
          <SummaryRow term="Stack">No stack (general)</SummaryRow>
        )}
      </SummarySection>
      <SummarySection title="Configuration" step="configuration" onEdit={onEdit}>
        <SummaryRow term="Approval mode">{approvalModeLabels[values.approvalMode]}</SummaryRow>
        <SummaryRow term="Compliance threshold">{Number(values.complianceThreshold).toFixed(2)}</SummaryRow>
        <SummaryRow term="Max files per step">{values.maxFilesPerStep}</SummaryRow>
        <SummaryRow term="Max review cycles">{values.maxReviewCycles}</SummaryRow>
        <SummaryRow term="Token budget">
          {values.tokenBudget.trim() ? `${Number(values.tokenBudget).toLocaleString()} tokens` : 'No limit'}
        </SummaryRow>
        <LlmSummary values={values} options={llmOptions} />
      </SummarySection>
      <SummarySection title="Deployment" step="deployment" onEdit={onEdit}>
        <DeploymentSummary values={values} />
      </SummarySection>
    </div>
  )
}
