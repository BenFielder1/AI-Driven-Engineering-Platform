import { zodResolver } from '@hookform/resolvers/zod'
import { useState, type ReactNode } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { z } from 'zod'

import { panelClass } from '../../components/shared/panel-class'
import { StatusBadge } from '../../components/shared/status-badge'
import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { RadioGroup } from '../../components/ui/radio-group'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError, messageFor } from '../../lib/errors'
import { cn } from '../../lib/utils'
import { GitHubLink } from '../github/github-error-alert'
import {
  useAdminGitHubApp,
  useCreateGitHubAppManifest,
  type AdminGitHubApp,
  type GitHubManifest,
} from '../github/github-api'

/** §4.14: what each App permission is for (GitHub's permission names). */
const permissionPurpose: Record<string, string> = {
  contents: 'Push commits, branches and release tags',
  pull_requests: 'Open, update and merge pull requests',
  workflows: 'Commit GitHub Actions workflow files',
  administration: 'Create repositories from the wizard',
  metadata: 'Read repository metadata (always required)',
  checks: 'Read check runs as a review input',
  statuses: 'Read commit statuses as a review input',
}

const permissionNames: Record<string, string> = {
  contents: 'Contents',
  pull_requests: 'Pull requests',
  workflows: 'Workflows',
  administration: 'Administration',
  metadata: 'Metadata',
  checks: 'Checks',
  statuses: 'Commit statuses',
}

function PermissionList({ permissions, label }: { permissions: Record<string, string>; label: string }) {
  const entries = Object.entries(permissions)
  if (entries.length === 0) {
    return <p className="text-sm text-muted-foreground">No permissions reported.</p>
  }
  return (
    <ul aria-label={label} className="divide-y divide-border/60 rounded-lg border border-border/60 text-sm">
      {entries.map(([key, access]) => (
        <li key={key} className="flex flex-col gap-0.5 px-3 py-2 sm:flex-row sm:items-center sm:justify-between">
          <span>
            <span className="font-medium">{permissionNames[key] ?? key}</span>
            {permissionPurpose[key] && <span className="text-muted-foreground"> · {permissionPurpose[key]}</span>}
          </span>
          <span className="font-mono text-xs text-muted-foreground">{access === 'write' ? 'Read and write' : access === 'read' ? 'Read' : access}</span>
        </li>
      ))}
    </ul>
  )
}

function Row({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="grid gap-1 py-2 sm:grid-cols-3">
      <dt className="text-sm font-medium text-muted-foreground">{term}</dt>
      <dd className="break-words text-sm sm:col-span-2">{children}</dd>
    </div>
  )
}

/** FR-80: the registered App (from the database or `GitHub__App__*` configuration). Secrets are never shown. */
function ConfiguredApp({ app }: { app: AdminGitHubApp }) {
  return (
    <div className="flex flex-col gap-4">
      <dl className="divide-y divide-border/60">
        <Row term="Status">
          <StatusBadge tone="success">
            <span data-testid="github-app-status">Configured</span>
          </StatusBadge>
        </Row>
        <Row term="Source">
          {app.source === 'configuration' ? (
            <>
              Set in <code>secrets.env</code> (<code>GitHub__App__*</code> configuration), which takes precedence over an
              App registered here. Change it there and restart AIDE.
            </>
          ) : (
            'Registered from this page. Its private key and client secret are stored encrypted in the database.'
          )}
        </Row>
        <Row term="App">
          {app.htmlUrl ? (
            <GitHubLink href={app.htmlUrl} variant="link">
              {app.slug ?? 'View the App'}
            </GitHubLink>
          ) : (
            (app.slug ?? 'Unknown')
          )}
          {app.appId !== null && <span className="text-muted-foreground"> · App ID {app.appId}</span>}
        </Row>
        <Row term="Owner">
          {app.ownerLogin ?? 'Unknown'}
          {app.ownerType && (
            <span className="text-muted-foreground"> ({app.ownerType === 'organization' ? 'organisation' : 'personal account'})</span>
          )}
        </Row>
      </dl>
      <div>
        <h4 className="mb-2 text-sm font-semibold">Permissions</h4>
        <PermissionList permissions={app.permissions} label="GitHub App permissions" />
      </div>
      {app.canCreateRepositories ? (
        <p className="text-sm text-muted-foreground">Users can create repositories from the new-project wizard.</p>
      ) : (
        <Alert tone="warning">
          <p>
            &quot;Create repository&quot; is unavailable: the App doesn&apos;t have the Administration permission. Add it in
            the App&apos;s settings on GitHub if users should create repositories from AIDE.
          </p>
        </Alert>
      )}
      {app.installUrl && (
        <div className="flex flex-wrap items-center gap-3">
          <GitHubLink href={app.installUrl}>Install the App</GitHubLink>
          <span className="text-sm text-muted-foreground">
            Users install it on the accounts and repositories AIDE should work with.
          </span>
        </div>
      )}
    </div>
  )
}

const ownerSchema = z
  .object({ ownerType: z.enum(['user', 'organization']), org: z.string() })
  .superRefine((v, ctx) => {
    const org = v.org.trim()
    if (v.ownerType === 'organization') {
      if (!org) {
        ctx.addIssue({ code: 'custom', path: ['org'], message: 'Enter the organisation’s GitHub login, such as "acme".' })
      } else if (!/^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$/.test(org)) {
        ctx.addIssue({ code: 'custom', path: ['org'], message: 'Use the organisation’s login: letters, digits and hyphens.' })
      }
    }
  })

type OwnerValues = z.infer<typeof ownerSchema>
const ownerIds = { ownerType: 'github-app-owner-type', org: 'github-app-org' } as const

/**
 * FR-80 manifest flow: choose the owner, prepare the manifest (AIDE builds it), preview it, then a real HTML form POST
 * to GitHub (`postUrl`, field `manifest`). GitHub redirects back through the API's callback to this page.
 */
function RegisterApp() {
  const create = useCreateGitHubAppManifest()
  const [manifest, setManifest] = useState<GitHubManifest | null>(null)
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    control,
    formState: { errors, isSubmitted },
  } = useForm<OwnerValues>({ resolver: zodResolver(ownerSchema), defaultValues: { ownerType: 'user', org: '' } })
  const ownerType = useWatch({ control, name: 'ownerType' })

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    create.mutate(
      { ownerType: v.ownerType, ...(v.ownerType === 'organization' ? { org: v.org.trim() } : {}) },
      {
        onSuccess: setManifest,
        onError: (error) => {
          const unmatched = applyFieldErrors(error, setError, ['org', 'ownerType'])
          setServerErrors(
            unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error, 'githubAppRegistration')],
          )
        },
      },
    )
  })

  if (manifest) {
    const m = manifest.manifest
    return (
      <div className="flex flex-col gap-4">
        <section aria-labelledby="github-manifest-preview-title" className="flex flex-col gap-3">
          <h4 id="github-manifest-preview-title" className="font-semibold">
            Manifest preview
          </h4>
          <dl className="divide-y divide-border/60">
            <Row term="Name">{m.name}</Row>
            <Row term="Homepage">{m.url}</Row>
            <Row term="Sign-in callback">{m.callback_urls.join(', ')}</Row>
            <Row term="Webhook">Off (AIDE polls GitHub instead)</Row>
            <Row term="Visibility">{m.public ? 'Public' : 'Private (only the owner can install it)'}</Row>
          </dl>
          <PermissionList permissions={m.default_permissions} label="Requested permissions" />
          <p className="text-sm text-muted-foreground">
            On GitHub you can untick permissions before creating the App. Unticking <strong>Administration</strong> disables
            &quot;Create repository&quot; in the new-project wizard.
          </p>
        </section>
        {/* A real form POST: GitHub's manifest flow takes the manifest as a form field and redirects back. */}
        <form method="post" action={manifest.postUrl} aria-label="Register the GitHub App on GitHub" className="flex flex-wrap gap-2">
          <input type="hidden" name="manifest" value={JSON.stringify(m)} />
          <Button type="submit">Register on GitHub</Button>
          <Button
            variant="secondary"
            onClick={() => {
              setManifest(null)
              create.reset()
            }}
          >
            Change owner
          </Button>
        </form>
      </div>
    )
  }

  return (
    <form noValidate onSubmit={(e) => void onSubmit(e)} aria-label="Choose the GitHub App owner" className="flex flex-col gap-4">
      <p className="text-sm text-muted-foreground">
        AIDE works with GitHub through its own GitHub App. Register it once: AIDE prepares the App manifest, GitHub creates
        the App, and AIDE stores its credentials encrypted.
      </p>
      <ErrorSummary
        title="We couldn't prepare the registration"
        errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, ownerIds) : [])]}
      />
      <RadioGroup
        id={ownerIds.ownerType}
        legend="Who owns the App?"
        inputProps={register('ownerType')}
        options={[
          { value: 'user', label: 'My personal account', description: 'The App belongs to your GitHub user.' },
          {
            value: 'organization',
            label: 'An organisation',
            description: 'The App belongs to an organisation you administer.',
          },
        ]}
      />
      {ownerType === 'organization' && (
        <TextField
          id={ownerIds.org}
          label="Organisation"
          hint="The organisation’s GitHub login, as in github.com/acme."
          autoComplete="off"
          spellCheck={false}
          error={errors.org?.message}
          {...register('org')}
        />
      )}
      <div>
        <Button type="submit" pending={create.isPending} pendingLabel="Preparing…">
          Prepare registration
        </Button>
      </div>
    </form>
  )
}

/** Admin → Integrations → GitHub (FR-80): register the AIDE GitHub App, or show the registered one. */
export function GitHubAppPanel({ callback }: { callback?: 'configured' | 'failed' }) {
  const app = useAdminGitHubApp()
  let body
  if (app.isPending) {
    body = <LoadingState label="Loading the GitHub App…" />
  } else if (app.isError) {
    body = <ErrorState error={app.error} title="The GitHub App status couldn't load" onRetry={() => void app.refetch()} />
  } else if (app.data.status === 'configured') {
    body = <ConfiguredApp app={app.data} />
  } else {
    body = (
      <>
        <p className="flex flex-wrap items-center gap-2 text-sm">
          <span className="font-medium">Status</span>
          <StatusBadge tone="neutral">
            <span data-testid="github-app-status">Not registered</span>
          </StatusBadge>
        </p>
        <RegisterApp />
      </>
    )
  }
  return (
    <section aria-labelledby="admin-github-app-title" className={cn(panelClass, 'flex flex-col gap-4 p-5')}>
      <h3 id="admin-github-app-title" className="text-lg">
        GitHub App
      </h3>
      {callback === 'configured' && (
        <Alert tone="success" role="status" title="GitHub App registered">
          <p>Users can now connect GitHub, pick repositories and sign in with GitHub.</p>
        </Alert>
      )}
      {callback === 'failed' && (
        <Alert tone="error" role="alert" title="GitHub App registration failed">
          <p>{messageFor('GITHUB_AUTH_FAILED', 'githubAppRegistration')}</p>
        </Alert>
      )}
      {body}
    </section>
  )
}
