import { zodResolver } from '@hookform/resolvers/zod'
import { useRef, useState, type ReactNode } from 'react'
import { useForm } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'

import { panelClass } from '../../components/shared/panel-class'
import { StatusBadge } from '../../components/shared/status-badge'
import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError } from '../../lib/errors'
import { cn } from '../../lib/utils'
import {
  useAdminGitLab,
  useCheckGitLabInstance,
  useCreateGitLabInstance,
  useDeleteGitLabInstance,
  useUpdateGitLabInstance,
  type AdminGitLabInstance,
  type AdminGitLabIntegration,
} from '../gitlab/gitlab-api'
import { GitLabLogo } from '../gitlab/gitlab-logo'

/** Backend limits (GitLabRules.MaxNameLength, GitHostAddressRules.MaxBaseUrlLength, the 500-character OAuth fields). */
const NAME_MAX = 200
const BASE_URL_MAX = 2048
const OAUTH_MAX = 500

function Row({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="grid gap-1 py-2 sm:grid-cols-3">
      <dt className="text-sm font-medium text-muted-foreground">{term}</dt>
      <dd className="break-words text-sm sm:col-span-2">{children}</dd>
    </div>
  )
}

/** FR-130: GitLab has no manifest flow, so the Admin registers an OAuth application by hand with these values. */
function RegistrationHelp({ integration }: { integration: AdminGitLabIntegration }) {
  const copy = (text: string) => {
    void navigator.clipboard.writeText(text).then(
      () => toast.success('Callback URL copied.'),
      () => toast.error("We couldn't copy it. Select the URL and copy it yourself."),
    )
  }
  return (
    <section aria-labelledby="gitlab-registration-title" className="rounded-lg border border-border/60 bg-background/40 p-4">
      <h4 id="gitlab-registration-title" className="font-semibold">
        Register an OAuth application
      </h4>
      <p className="mt-1 text-sm text-muted-foreground">
        On each GitLab instance, create an application (User settings → Applications, or Admin → Applications for the whole
        instance) with these values, then enter its Application ID and Secret here.
      </p>
      <dl className="mt-2 divide-y divide-border/60">
        <Row term="Redirect URI">
          <span className="flex flex-wrap items-center gap-2">
            <code className="break-all font-mono" data-testid="gitlab-callback-url">
              {integration.callbackUrl}
            </code>
            {typeof navigator !== 'undefined' && 'clipboard' in navigator && (
              <Button
                variant="ghost"
                aria-label="Copy the redirect URI"
                onClick={() => {
                  copy(integration.callbackUrl)
                }}
              >
                Copy
              </Button>
            )}
          </span>
        </Row>
        <Row term="Confidential">Yes</Row>
        <Row term="Scopes">
          <code className="font-mono" data-testid="gitlab-scopes">
            {integration.scopes.join(' ')}
          </code>
        </Row>
        <Row term="Minimum version">GitLab {integration.minimumVersion}</Row>
        <Row term="Private networks">
          {integration.allowPrivateNetworks
            ? 'Allowed: instances on private addresses (10/8, 172.16/12, 192.168/16) can be added.'
            : 'Not allowed: instances must resolve to public addresses.'}{' '}
          Set by <code>GitLab__AllowPrivateNetworks</code>.
        </Row>
      </dl>
    </section>
  )
}

const versionTones = { supported: 'success', old: 'warning', unknown: 'neutral' } as const

function VersionText({ instance }: { instance: AdminGitLabInstance }) {
  if (!instance.version) {
    return <span className="text-muted-foreground">Unknown (GitLab answered without its version)</span>
  }
  return (
    <StatusBadge tone={versionTones[instance.versionStatus]}>
      <span data-testid="gitlab-version">GitLab {instance.version}</span>
    </StatusBadge>
  )
}

function InstanceRow({
  instance,
  checking,
  onCheck,
  onEdit,
  onToggle,
  onDelete,
}: {
  instance: AdminGitLabInstance
  checking: boolean
  onCheck: () => void
  onEdit: () => void
  onToggle: () => void
  onDelete: () => void
}) {
  const i = instance
  const configured = i.source === 'configuration'
  return (
    <li aria-label={i.name} className="flex flex-col gap-3 p-4">
      <div className="flex flex-col gap-1 text-sm">
        <span className="flex flex-wrap items-center gap-2">
          <GitLabLogo />
          <span className="font-semibold break-words">{i.name}</span>
          {i.isGitLabCom && <StatusBadge tone="info">Default</StatusBadge>}
          <StatusBadge tone={i.enabled ? 'success' : 'neutral'}>
            <span data-testid="gitlab-instance-enabled">{i.enabled ? 'Enabled' : 'Disabled'}</span>
          </StatusBadge>
        </span>
        <span className="break-all font-mono text-muted-foreground">{i.baseUrl}</span>
      </div>
      <dl className="divide-y divide-border/60">
        <Row term="Source">
          {configured ? (
            <>
              Set in configuration (<code>GitLab__Instances__*</code>), which takes precedence. Change it there and restart
              AIDE; only Enabled can change here.
            </>
          ) : (
            'Added here. The client secret is stored encrypted.'
          )}
        </Row>
        <Row term="OAuth application">
          {i.oauthConfigured ? (
            <>
              Client ID <code className="break-all font-mono">{i.clientId}</code> · secret set. &quot;Continue with GitLab&quot; and
              OAuth connections work.
            </>
          ) : (
            <span data-testid="gitlab-oauth-missing">
              Not set up: users can only connect with an access token, and sign-in with this instance is off.
            </span>
          )}
        </Row>
        <Row term="Version">
          <VersionText instance={i} />
        </Row>
        <Row term="In use">
          {i.connectionCount} connection{i.connectionCount === 1 ? '' : 's'} · {i.projectCount} project
          {i.projectCount === 1 ? '' : 's'}
        </Row>
        {i.allowInsecureHttp && <Row term="Insecure HTTP">Allowed (lab host)</Row>}
      </dl>
      {i.versionStatus === 'old' && (
        <Alert tone="warning" title="Older than the supported version">
          <p>{i.versionWarning ?? `GitLab ${i.version ?? ''} is older than the minimum supported version.`}</p>
        </Alert>
      )}
      {!i.enabled && (
        <p className="text-sm text-muted-foreground">
          Disabled: its connections stop working, sign-in hides it, and its projects show &quot;GitLab instance disabled&quot;.
        </p>
      )}
      <div className="flex flex-wrap gap-2">
        <Button variant="secondary" pending={checking} pendingLabel="Checking…" onClick={onCheck} aria-label={`Check ${i.name}`}>
          Check
        </Button>
        {!configured && (
          <Button variant="secondary" onClick={onEdit} aria-label={`Edit ${i.name}`}>
            Edit
          </Button>
        )}
        <Button variant="secondary" onClick={onToggle} aria-label={`${i.enabled ? 'Disable' : 'Enable'} ${i.name}`}>
          {i.enabled ? 'Disable' : 'Enable'}
        </Button>
        {!configured && !i.isGitLabCom && (
          <Button variant="danger" onClick={onDelete} aria-label={`Remove ${i.name}`}>
            Remove
          </Button>
        )}
      </div>
    </li>
  )
}

function parseUrl(value: string): URL | null {
  try {
    return new URL(value)
  } catch {
    return null
  }
}

const instanceSchema = z
  .object({
    name: z
      .string()
      .trim()
      .min(1, { error: 'Give the instance a name, such as "Acme GitLab".' })
      .max(NAME_MAX, { error: `Names are at most ${String(NAME_MAX)} characters.` }),
    baseUrl: z.string().trim(),
    clientId: z.string().trim().max(OAUTH_MAX, { error: `The Application ID is at most ${String(OAUTH_MAX)} characters.` }),
    clientSecret: z.string().max(OAUTH_MAX, { error: `The secret is at most ${String(OAUTH_MAX)} characters.` }),
    allowInsecureHttp: z.boolean(),
    enabled: z.boolean(),
  })
  .superRefine((v, ctx) => {
    const url = parseUrl(v.baseUrl)
    if (!v.baseUrl) {
      ctx.addIssue({ code: 'custom', path: ['baseUrl'], message: 'Enter the instance address, such as https://gitlab.example.com.' })
    } else if (v.baseUrl.length > BASE_URL_MAX) {
      ctx.addIssue({ code: 'custom', path: ['baseUrl'], message: `The address is at most ${String(BASE_URL_MAX)} characters.` })
    } else if (!url || (url.protocol !== 'https:' && url.protocol !== 'http:')) {
      ctx.addIssue({ code: 'custom', path: ['baseUrl'], message: 'Enter a full address starting with https://.' })
    } else if (url.protocol === 'http:' && !v.allowInsecureHttp) {
      ctx.addIssue({ code: 'custom', path: ['baseUrl'], message: 'Use https://, or tick "Allow insecure HTTP" for a lab host.' })
    }
  })

type InstanceValues = z.infer<typeof instanceSchema>
const instanceIds = {
  name: 'gitlab-instance-name',
  baseUrl: 'gitlab-instance-base-url',
  clientId: 'gitlab-instance-client-id',
  clientSecret: 'gitlab-instance-client-secret',
} as const

/**
 * FR-130: add a self-managed instance, or edit one (gitlab.com keeps its address). The server runs the SSRF guard and
 * the reachability check; the client secret is write-only (empty when editing = unchanged).
 */
function InstanceDialog({ instance, onClose }: { instance: AdminGitLabInstance | null; onClose: () => void }) {
  const editing = instance !== null
  const create = useCreateGitLabInstance()
  const update = useUpdateGitLabInstance()
  const pending = create.isPending || update.isPending
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    formState: { errors, isSubmitted },
  } = useForm<InstanceValues>({
    resolver: zodResolver(instanceSchema),
    defaultValues: {
      name: instance?.name ?? '',
      baseUrl: instance?.baseUrl ?? '',
      clientId: instance?.clientId ?? '',
      clientSecret: '',
      allowInsecureHttp: instance?.allowInsecureHttp ?? false,
      enabled: instance?.enabled ?? true,
    },
  })

  const fail = (error: unknown) => {
    setValue('clientSecret', '')
    create.reset()
    update.reset()
    if (isApiError(error, 'GIT_HOST_NOT_ALLOWED') || isApiError(error, 'UPSTREAM_ERROR')) {
      setError('baseUrl', { type: 'server', message: errorMessage(error, 'gitlabInstance') }, { shouldFocus: true })
      return
    }
    const unmatched = applyFieldErrors(error, setError, ['name', 'baseUrl', 'clientId', 'clientSecret'])
    setServerErrors(unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error, 'gitlabInstance')])
  }

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    const done = (saved: AdminGitLabInstance) => {
      create.reset()
      update.reset()
      toast.success(editing ? `"${saved.name}" saved.` : `"${saved.name}" added and checked.`)
      onClose()
    }
    if (instance) {
      const body: Parameters<typeof update.mutate>[0]['body'] = {}
      if (v.name !== instance.name) {
        body.name = v.name
      }
      if (!instance.isGitLabCom && v.baseUrl !== instance.baseUrl) {
        body.baseUrl = v.baseUrl
      }
      if (v.clientId !== (instance.clientId ?? '')) {
        body.clientId = v.clientId
      }
      if (v.clientSecret) {
        body.clientSecret = v.clientSecret
      }
      if (v.allowInsecureHttp !== instance.allowInsecureHttp) {
        body.allowInsecureHttp = v.allowInsecureHttp
      }
      if (v.enabled !== instance.enabled) {
        body.enabled = v.enabled
      }
      update.mutate({ id: instance.id, body }, { onSuccess: done, onError: fail })
    } else {
      create.mutate(
        {
          name: v.name,
          baseUrl: v.baseUrl,
          allowInsecureHttp: v.allowInsecureHttp,
          enabled: v.enabled,
          ...(v.clientId ? { clientId: v.clientId } : {}),
          ...(v.clientSecret ? { clientSecret: v.clientSecret } : {}),
        },
        { onSuccess: done, onError: fail },
      )
    }
  })

  return (
    <ModalDialog
      open
      title={editing ? `Edit "${instance.name}"` : 'Add a GitLab instance'}
      description="AIDE checks the address (it must be allowed and answer like GitLab) before saving."
      busy={pending}
      onClose={onClose}
      className="sm:max-w-lg"
    >
      <form noValidate onSubmit={(e) => void onSubmit(e)} aria-label="GitLab instance" className="flex flex-col gap-4">
        <ErrorSummary
          title="We couldn't save the instance"
          errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, instanceIds) : [])]}
        />
        <TextField id={instanceIds.name} label="Name" maxLength={NAME_MAX} autoComplete="off" error={errors.name?.message} {...register('name')} />
        <TextField
          id={instanceIds.baseUrl}
          label="Base URL"
          type="url"
          hint={instance?.isGitLabCom ? "gitlab.com's address can't change." : 'For example https://gitlab.example.com.'}
          autoComplete="off"
          spellCheck={false}
          maxLength={BASE_URL_MAX}
          readOnly={instance?.isGitLabCom}
          error={errors.baseUrl?.message}
          {...register('baseUrl')}
        />
        <TextField
          id={instanceIds.clientId}
          label="Application ID (optional)"
          hint="From the OAuth application on GitLab. Without it, users can only connect with an access token."
          autoComplete="off"
          spellCheck={false}
          maxLength={OAUTH_MAX}
          error={errors.clientId?.message}
          {...register('clientId')}
        />
        <TextField
          id={instanceIds.clientSecret}
          label="Secret (optional)"
          type="password"
          autoComplete="new-password"
          spellCheck={false}
          maxLength={OAUTH_MAX}
          hint={
            instance?.hasClientSecret
              ? 'A secret is set. Leave this empty to keep it. It is stored encrypted and never shown.'
              : 'Stored encrypted and never shown again.'
          }
          error={errors.clientSecret?.message}
          {...register('clientSecret')}
        />
        {!instance?.isGitLabCom && (
          <div className="flex items-start gap-2">
            <input
              id="gitlab-instance-insecure"
              type="checkbox"
              className="mt-0.5 size-4 accent-primary"
              aria-describedby="gitlab-instance-insecure-hint"
              {...register('allowInsecureHttp')}
            />
            <div>
              <label htmlFor="gitlab-instance-insecure" className="text-sm font-medium">
                Allow insecure HTTP
              </label>
              <p id="gitlab-instance-insecure-hint" className="text-sm text-muted-foreground">
                Only for a lab host without TLS. Tokens then travel unencrypted.
              </p>
            </div>
          </div>
        )}
        <div className="flex items-center gap-2">
          <input id="gitlab-instance-enabled" type="checkbox" className="size-4 accent-primary" {...register('enabled')} />
          <label htmlFor="gitlab-instance-enabled" className="text-sm font-medium">
            Enabled
          </label>
        </div>
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={pending}>
            Cancel
          </Button>
          <Button type="submit" pending={pending} pendingLabel="Checking the instance…">
            {editing ? 'Save instance' : 'Add instance'}
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}

/** Admin → Integrations → GitLab (FR-130): the registration help and the instances (add, edit, check, disable, remove). */
export function AdminGitLabPanel() {
  const integration = useAdminGitLab()
  const check = useCheckGitLabInstance()
  const update = useUpdateGitLabInstance()
  const remove = useDeleteGitLabInstance()
  const [editing, setEditing] = useState<AdminGitLabInstance | 'new' | null>(null)
  const [disabling, setDisabling] = useState<AdminGitLabInstance | null>(null)
  const [removing, setRemoving] = useState<AdminGitLabInstance | null>(null)
  const [actionError, setActionError] = useState<{ title: string; message: string } | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  const runCheck = (i: AdminGitLabInstance) => {
    setActionError(null)
    check.mutate(i.id, {
      onSuccess: (result) => {
        if (result.versionStatus === 'old') {
          toast.warning(`"${result.name}" is reachable, but GitLab ${result.version ?? ''} is older than the supported version.`)
        } else {
          toast.success(`"${result.name}" is reachable${result.version ? ` (GitLab ${result.version})` : ''}.`)
        }
      },
      onError: (error) => {
        setActionError({ title: `"${i.name}" didn't pass the check`, message: errorMessage(error, 'gitlabInstance') })
      },
    })
  }

  const setEnabled = (i: AdminGitLabInstance, enabled: boolean) => {
    setActionError(null)
    update.mutate(
      { id: i.id, body: { enabled } },
      {
        onSuccess: () => {
          setDisabling(null)
          toast.success(`"${i.name}" ${enabled ? 'enabled' : 'disabled'}.`)
        },
        onError: (error) => {
          setDisabling(null)
          setActionError({
            title: `We couldn't ${enabled ? 'enable' : 'disable'} "${i.name}"`,
            message: errorMessage(error, 'gitlabInstance'),
          })
        },
      },
    )
  }

  const confirmRemove = () => {
    if (!removing) {
      return
    }
    const target = removing
    setActionError(null)
    remove.mutate(target.id, {
      onSuccess: () => {
        setRemoving(null)
        toast.success(`"${target.name}" removed.`)
        headingRef.current?.focus()
      },
      onError: (error) => {
        setRemoving(null)
        setActionError({ title: `We couldn't remove "${target.name}"`, message: errorMessage(error, 'deleteGitLabInstance') })
      },
    })
  }

  let body
  if (integration.isPending) {
    body = <LoadingState label="Loading the GitLab instances…" />
  } else if (integration.isError) {
    body = <ErrorState error={integration.error} title="The GitLab instances couldn't load" onRetry={() => void integration.refetch()} />
  } else {
    body = (
      <div className="flex flex-col gap-4">
        <RegistrationHelp integration={integration.data} />
        <ul aria-label="GitLab instances" className="divide-y divide-border/60 rounded-lg border border-border/60">
          {integration.data.instances.map((i) => (
            <InstanceRow
              key={i.id}
              instance={i}
              checking={check.isPending && check.variables === i.id}
              onCheck={() => {
                runCheck(i)
              }}
              onEdit={() => {
                setEditing(i)
              }}
              onToggle={() => {
                if (i.enabled) {
                  setDisabling(i)
                } else {
                  setEnabled(i, true)
                }
              }}
              onDelete={() => {
                setRemoving(i)
              }}
            />
          ))}
        </ul>
      </div>
    )
  }

  return (
    <section aria-labelledby="admin-gitlab-title" className={cn(panelClass, 'flex flex-col gap-4 p-5')}>
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <h3 id="admin-gitlab-title" ref={headingRef} tabIndex={-1} className="flex items-center gap-2 text-lg outline-none">
            <GitLabLogo className="size-5" />
            GitLab
          </h3>
          <p className="mt-1 text-sm text-muted-foreground">
            gitlab.com and self-managed instances users can connect to and sign in with.
          </p>
        </div>
        <Button
          onClick={() => {
            setEditing('new')
          }}
        >
          Add instance
        </Button>
      </div>
      {actionError && (
        <Alert tone="error" role="alert" title={actionError.title}>
          <p>{actionError.message}</p>
        </Alert>
      )}
      {body}
      {editing !== null && (
        <InstanceDialog
          instance={editing === 'new' ? null : editing}
          onClose={() => {
            setEditing(null)
          }}
        />
      )}
      <ConfirmDialog
        open={disabling !== null}
        title={`Disable "${disabling?.name ?? ''}"?`}
        confirmLabel="Disable instance"
        pendingLabel="Disabling…"
        pending={update.isPending}
        destructive
        onConfirm={() => {
          if (disabling) {
            setEnabled(disabling, false)
          }
        }}
        onCancel={() => {
          setDisabling(null)
        }}
      >
        <p>
          Its {disabling?.connectionCount ?? 0} connection{disabling?.connectionCount === 1 ? '' : 's'} stop working, sign-in
          hides it, and its {disabling?.projectCount ?? 0} project{disabling?.projectCount === 1 ? '' : 's'} show &quot;GitLab
          instance disabled&quot; until you enable it again.
        </p>
      </ConfirmDialog>
      <ConfirmDialog
        open={removing !== null}
        title={`Remove "${removing?.name ?? ''}"?`}
        confirmLabel="Remove instance"
        pendingLabel="Removing…"
        pending={remove.isPending}
        destructive
        onConfirm={confirmRemove}
        onCancel={() => {
          setRemoving(null)
        }}
      >
        <p>
          Users&apos; connections to it and their GitLab sign-in links are removed too. An instance that projects use can&apos;t
          be removed; disable it instead. This can&apos;t be undone.
        </p>
      </ConfirmDialog>
    </section>
  )
}
