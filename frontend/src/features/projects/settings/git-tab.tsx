import { useState } from 'react'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm, useWatch } from 'react-hook-form'
import { z } from 'zod'
import { toast } from 'sonner'

import type { Schemas } from '../../../api/client'
import { Alert, ErrorSummary } from '../../../components/ui/alert'
import { Button } from '../../../components/ui/button'
import { fieldErrorSummary } from '../../../components/ui/form-errors'
import { SelectField } from '../../../components/ui/select-field'
import { TextField } from '../../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError } from '../../../lib/errors'
import { gitProviderLabels } from '../labels'
import { useTestGitConnection, useUpdateGitSettings, type GitSettings, type GitTestResult } from '../projects-api'
import { checkBranch, checkRepoUrl, checkSecret, limits } from '../schemas'
import { SecretField, type SecretAction } from './secret-field'

const gitSchema = z
  .object({
    provider: z.enum(['github', 'local']),
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
    if (v.provider === 'github') {
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
  provider: g.provider,
  repoUrl: g.repoUrl ?? '',
  defaultBranch: g.defaultBranch,
  token: '',
})

/** Settings → Git (FR-9, FR-10, FR-35). The token is write-only: only "Set" / "Not set" is ever shown. */
export function GitTab({ projectId, git }: { projectId: string; git: GitSettings }) {
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

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    const github = v.provider === 'github'
    const body: Schemas['UpdateGitSettingsRequest'] = {
      provider: v.provider,
      repoUrl: github ? v.repoUrl.trim() : null,
      defaultBranch: v.defaultBranch.trim(),
    }
    if (github && tokenAction === 'clear') {
      body.clearToken = true
    } else if (github && v.token && (tokenAction === 'replace' || !git.hasToken)) {
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
    <div className="flex max-w-2xl flex-col gap-8">
      <form noValidate onSubmit={(e) => void onSubmit(e)} aria-labelledby="git-title">
        <h2 id="git-title" className="mb-4 text-xl">
          Git repository
        </h2>
        <ErrorSummary
          title="We couldn't save the Git settings"
          errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, fieldIds) : [])]}
        />
        <div className="mt-2 flex flex-col gap-5">
          <SelectField id={fieldIds.provider} label="Provider" error={errors.provider?.message} {...register('provider')}>
            <option value="github">{gitProviderLabels.github}</option>
            <option value="local">{gitProviderLabels.local}</option>
          </SelectField>
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
              <SecretField
                id={fieldIds.token}
                label="Access token"
                isSet={git.hasToken}
                action={tokenAction}
                onActionChange={(a) => {
                  setTokenAction(a)
                  setValue('token', '')
                }}
                hint="Needs read and write access to the repository. Stored encrypted; it's never shown again."
                maxLength={limits.secretMax}
                error={errors.token?.message}
                inputProps={register('token')}
              />
            </>
          ) : (
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
        </div>
        <div className="mt-6">
          <Button type="submit" pending={update.isPending} pendingLabel="Saving…">
            Save Git settings
          </Button>
        </div>
      </form>

      <TestConnection projectId={projectId} />
    </div>
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
