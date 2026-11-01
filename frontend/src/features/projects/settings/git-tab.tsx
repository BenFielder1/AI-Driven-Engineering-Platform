import { useState, type ReactNode } from 'react'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm, useWatch } from 'react-hook-form'
import { z } from 'zod'
import { toast } from 'sonner'

import type { Schemas } from '../../../api/client'
import { StatusBadge } from '../../../components/shared/status-badge'
import { Alert, ErrorSummary } from '../../../components/ui/alert'
import { Button } from '../../../components/ui/button'
import { ConfirmDialog } from '../../../components/ui/confirm-dialog'
import { fieldErrorSummary } from '../../../components/ui/form-errors'
import { SelectField } from '../../../components/ui/select-field'
import { TextField } from '../../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError } from '../../../lib/errors'
import { gitHostTerms } from '../../../lib/git-host'
import { useSwitchGitToApp } from '../../github/github-api'
import { GitHubErrorAlert, GitHubLink } from '../../github/github-error-alert'
import { gitProviderLabels, mergeStrategyLabel } from '../labels'
import { useTestGitConnection, useUpdateGitSettings, type GitSettings, type GitTestResult } from '../projects-api'
import { checkBranch, checkRepoUrl, checkSecret, limits } from '../schemas'
import { BindingAlert, BoundHostRows, isBoundHost, RebindConnection } from './git-binding'
import { ReleaseSettingsForm } from './release-settings-form'
import { SecretField, type SecretAction } from './secret-field'

const gitSchema = z
  .object({
    provider: z.enum(['github_app', 'github_pat', 'gitlab', 'bitbucket', 'local']),
    repoUrl: z.string(),
    defaultBranch: z.string(),
    token: z.string(),
  })
  .superRefine((v, ctx) => {
    const add = (path: string, message: string | null) => {
      if (message) {
        ctx.addIssue({ code: 'custom', path: [path], message })
      }
    }
    add('defaultBranch', checkBranch(v.defaultBranch))
    if (v.provider === 'github_pat') {
      add('repoUrl', checkRepoUrl(v.repoUrl))
      add('token', checkSecret(v.token, 'access token'))
    }
  })

type GitValues = z.infer<typeof gitSchema>

const fieldIds = {
  provider: 'git-provider',
  repoUrl: 'git-repo-url',
  defaultBranch: 'git-default-branch',
  token: 'git-token',
} as const

const toValues = (g: GitSettings): GitValues => ({
  provider: g.mode,
  repoUrl: g.repoUrl ?? '',
  defaultBranch: g.defaultBranch,
  token: '',
})

function Row({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="grid gap-1 py-2 sm:grid-cols-3">
      <dt className="text-sm font-medium text-muted-foreground">{term}</dt>
      <dd className="break-words text-sm sm:col-span-2">{children}</dd>
    </div>
  )
}

/**
 * FR-83: which mode is in use, the repository, App access and the PAT flag (never a token). FR-136: for GitLab and
 * Bitbucket, the host instance, the repository link and the binding.
 */
function GitModeSummary({ git }: { git: GitSettings }) {
  const repo = git.mode === 'github_app' ? git.repoFullName : git.mode === 'github_pat' ? git.repoUrl : null
  return (
    <section aria-labelledby="git-mode-title" className="rounded-lg border border-border/60 bg-background/40 p-4">
      <h3 id="git-mode-title" className="font-semibold">
        Connection
      </h3>
      <dl className="divide-y divide-border/60">
        <Row term="Mode">
          <span data-testid="git-mode">{gitProviderLabels[git.mode]}</span>
        </Row>
        {isBoundHost(git) ? (
          <BoundHostRows git={git} />
        ) : (
          <Row term="Repository">
            {git.mode === 'local' ? 'AIDE-managed local repository' : (repo ?? 'Not set')}
          </Row>
        )}
        {git.mode === 'github_app' && (
          <Row term="GitHub App">
            <StatusBadge tone="success">Installed on the repository</StatusBadge>
            <span className="mt-1 block text-muted-foreground">
              Background Git work uses short-lived installation tokens; no personal token is stored.
            </span>
          </Row>
        )}
        {git.mode === 'github_pat' && (
          <>
            <Row term="Access token">
              <span data-testid="git-has-token">{git.hasToken ? 'Set' : 'Not set'}</span>
            </Row>
            <Row term="GitHub App">
              {git.appInstalledOnRepo === true ? (
                <StatusBadge tone="success">Can access this repository</StatusBadge>
              ) : git.appInstalledOnRepo === false ? (
                <StatusBadge tone="neutral">Not installed on this repository</StatusBadge>
              ) : (
                <span className="text-muted-foreground">Not checked</span>
              )}
            </Row>
          </>
        )}
        <Row term="Merge strategy">
          <span data-testid="git-merge-strategy">{mergeStrategyLabel(git.mergeStrategy, gitHostTerms(git.mode))}</span>
        </Row>
        <Row term="Tag prefix">
          {git.tagPrefix ? <code className="font-mono">{git.tagPrefix}</code> : <span className="text-muted-foreground">None (bare version tags)</span>}
        </Row>
      </dl>
    </section>
  )
}

/** FR-83: move a PAT project onto the App (Owner, App installed on the repo); the PAT is removed. */
function SwitchToApp({ projectId, git }: { projectId: string; git: GitSettings }) {
  const switchToApp = useSwitchGitToApp(projectId)
  const [confirming, setConfirming] = useState(false)
  const [failure, setFailure] = useState<unknown>(null)
  if (!git.canSwitchToApp && failure === null) {
    return null
  }
  return (
    <section aria-labelledby="git-switch-title" className="rounded-lg border border-primary/40 bg-primary/5 p-4">
      <h3 id="git-switch-title" className="font-semibold">
        Use the GitHub App
      </h3>
      <p className="mb-3 mt-1 text-sm text-muted-foreground">
        The AIDE GitHub App is installed on this repository. Switching uses its installation tokens for all Git work and
        removes the stored access token.
      </p>
      {failure !== null && (
        <div className="mb-3">
          <GitHubErrorAlert error={failure} title="The project wasn't switched" context="switchGitToApp" installTarget={git.repoUrl?.split('/')[3]} connectionsLink />
        </div>
      )}
      {git.canSwitchToApp && (
        <Button
          onClick={() => {
            setConfirming(true)
          }}
        >
          Switch to GitHub App
        </Button>
      )}
      <ConfirmDialog
        open={confirming}
        title="Switch to the GitHub App?"
        confirmLabel="Switch to GitHub App"
        pendingLabel="Switching…"
        pending={switchToApp.isPending}
        onConfirm={() => {
          setFailure(null)
          switchToApp.mutate(undefined, {
            onSuccess: () => {
              setConfirming(false)
              toast.success('The project now uses the GitHub App.')
            },
            onError: (error) => {
              setConfirming(false)
              setFailure(error)
            },
          })
        }}
        onCancel={() => {
          setConfirming(false)
        }}
      >
        <p>AIDE removes the stored access token and uses the App for clones, pushes, pull requests and releases.</p>
      </ConfirmDialog>
    </section>
  )
}

/**
 * Settings → Git (FR-9, FR-10, FR-35, FR-83, and the release settings of FR-64/FR-65). The token is write-only: only "Set" / "Not set" is ever shown. A project
 * moves onto the GitHub App only through "Switch to GitHub App" (or the wizard), never through this form.
 */
export function GitTab({ projectId, git }: { projectId: string; git: GitSettings }) {
  return (
    <div className="flex max-w-2xl flex-col gap-8">
      {isBoundHost(git) && <BindingAlert git={git} />}
      <GitModeSummary git={git} />
      <RebindConnection projectId={projectId} git={git} />
      <SwitchToApp projectId={projectId} git={git} />
      {/* Remount when the mode changes (Switch to GitHub App), so the form starts from the saved settings. */}
      <GitForm key={git.mode} projectId={projectId} git={git} />
      <TestConnection projectId={projectId} />
      <ReleaseSettingsForm projectId={projectId} git={git} />
    </div>
  )
}

function GitForm({ projectId, git }: { projectId: string; git: GitSettings }) {
  const update = useUpdateGitSettings(projectId)
  const [tokenAction, setTokenAction] = useState<SecretAction>('keep')
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    reset,
    control,
    formState: { errors, isSubmitted },
  } = useForm<GitValues>({ resolver: zodResolver(gitSchema), defaultValues: toValues(git) })

  const provider = useWatch({ control, name: 'provider' })
  const isApp = git.mode === 'github_app'
  /** GitLab/Bitbucket projects (FR-136) keep their host here; their binding and Rebind are above the form. */
  const hostMode = git.mode === 'gitlab' || git.mode === 'bitbucket' ? git.mode : null

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    const pat = v.provider === 'github_pat'
    const body: Schemas['UpdateGitSettingsRequest'] = {
      provider: v.provider,
      defaultBranch: v.defaultBranch.trim(),
    }
    if (pat) {
      body.repoUrl = v.repoUrl.trim()
    } else if (v.provider === 'local') {
      body.repoUrl = null
    }
    if (pat && tokenAction === 'clear') {
      body.clearToken = true
    } else if (pat && v.token && (tokenAction === 'replace' || !git.hasToken)) {
      body.token = v.token
    }
    update.mutate(body, {
      onSuccess: (next) => {
        reset(toValues(next))
        setTokenAction('keep')
        toast.success('Git settings saved.')
      },
      onError: (error) => {
        const unmatched = applyFieldErrors(error, setError, ['provider', 'repoUrl', 'defaultBranch', 'token'])
        setServerErrors(unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error)])
      },
    })
  })

  return (
    <form noValidate onSubmit={(e) => void onSubmit(e)} aria-labelledby="git-title">
      <h2 id="git-title" className="mb-4 text-xl">
        Git repository
      </h2>
      <ErrorSummary
        title="We couldn't save the Git settings"
        errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, fieldIds) : [])]}
      />
      <div className="mt-2 flex flex-col gap-5">
        <SelectField
          id={fieldIds.provider}
          label="Mode"
          hint={isApp ? undefined : 'To use the GitHub App, use "Switch to GitHub App" once the App is installed on the repository.'}
          error={errors.provider?.message}
          {...register('provider')}
        >
          {isApp && <option value="github_app">{gitProviderLabels.github_app}</option>}
          {hostMode && <option value={hostMode}>{gitProviderLabels[hostMode]}</option>}
          <option value="github_pat">{gitProviderLabels.github_pat}</option>
          <option value="local">{gitProviderLabels.local}</option>
        </SelectField>
        {provider === 'github_app' && (
          <Alert tone="info">
            <p>
              The repository is <strong>{git.repoFullName}</strong>, picked when the project was created. AIDE accesses it
              through the GitHub App.
            </p>
          </Alert>
        )}
        {(provider === 'gitlab' || provider === 'bitbucket') && (
          <Alert tone="info">
            <p>
              The repository is <strong>{git.repoFullName}</strong> on {gitProviderLabels[provider]}, picked when the project
              was created.
            </p>
          </Alert>
        )}
        {provider === 'github_pat' && (
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
            <SecretField
              id={fieldIds.token}
              label="Access token"
              isSet={git.mode === 'github_pat' && git.hasToken}
              action={tokenAction}
              onActionChange={(a) => {
                setTokenAction(a)
                setValue('token', '')
              }}
              hint="Needs Contents and Pull requests read and write. Stored encrypted; it's never shown again."
              maxLength={limits.secretMax}
              error={errors.token?.message}
              inputProps={register('token')}
            />
          </>
        )}
        {provider === 'local' && (
          <Alert tone="info">
            <p>AIDE keeps a Git repository for this project on its own data volume. No token is needed.</p>
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
        {git.branchName && (
          <p className="text-sm text-muted-foreground">
            Project branch: <code>{git.branchName}</code>
          </p>
        )}
        {isApp && git.repoFullName && (
          <p className="text-sm">
            <GitHubLink href={`https://github.com/${git.repoFullName}`} variant="link">
              Open the repository on GitHub
            </GitHubLink>
          </p>
        )}
      </div>
      <div className="mt-6">
        <Button type="submit" pending={update.isPending} pendingLabel="Saving…">
          Save Git settings
        </Button>
      </div>
    </form>
  )
}

function Check({ label, ok }: { label: string; ok: boolean }) {
  return (
    <li className="flex justify-between gap-3 py-1">
      <span>{label}</span>
      <span className={ok ? 'font-medium text-success' : 'font-medium text-destructive'}>{ok ? 'Yes' : 'No'}</span>
    </li>
  )
}

/** FR-10: tests the saved settings (nothing is saved) and shows each check. */
function TestConnection({ projectId }: { projectId: string }) {
  const test = useTestGitConnection(projectId)
  const [result, setResult] = useState<GitTestResult | null>(null)
  const [error, setError] = useState<unknown>(null)

  return (
    <section aria-labelledby="git-test-title" className="rounded-lg border border-border/60 bg-background/40 p-4">
      <h3 id="git-test-title" className="font-semibold">
        Test connection
      </h3>
      <p className="mb-3 mt-1 text-sm text-muted-foreground">Checks the saved settings. Save any changes first.</p>
      <Button
        variant="secondary"
        pending={test.isPending}
        pendingLabel="Testing…"
        onClick={() => {
          setResult(null)
          setError(null)
          test.mutate(undefined, { onSuccess: setResult, onError: setError })
        }}
      >
        Test connection
      </Button>
      <div role="status" aria-live="polite" className="mt-3">
        {result && (
          <Alert tone={result.ok ? 'success' : 'error'} title={result.ok ? 'Connection OK' : 'Connection failed'}>
            {!result.ok && result.reason && <p className="mt-1">{result.reason}</p>}
            <ul aria-label="Connection checks" className="mt-2 divide-y divide-border/60">
              <Check label="Repository reachable" ok={result.reachable} />
              <Check label="Write access" ok={result.writable} />
              <Check label="Default branch exists" ok={result.defaultBranchExists} />
            </ul>
          </Alert>
        )}
      </div>
      {error !== null && (
        <Alert tone="error" role="alert" title="The test couldn't run" className="mt-3">
          <p>{errorMessage(error)}</p>
        </Alert>
      )}
    </section>
  )
}
