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
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError } from '../../lib/errors'
import { cn } from '../../lib/utils'
import {
  useAdminBitbucket,
  useDeleteBitbucketConsumer,
  useUpdateBitbucketConsumer,
  type AdminBitbucketIntegration,
} from '../bitbucket/bitbucket-api'
import { BitbucketLogo } from '../bitbucket/bitbucket-logo'

/** Backend UpdateBitbucketConsumerRequest limits. */
const OAUTH_MAX = 500

function Row({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="grid gap-1 py-2 sm:grid-cols-3">
      <dt className="text-sm font-medium text-muted-foreground">{term}</dt>
      <dd className="break-words text-sm sm:col-span-2">{children}</dd>
    </div>
  )
}

/**
 * FR-133: Bitbucket fixes scopes on the OAuth consumer, so the Admin registers it by hand with this callback URL and
 * ticks these permissions.
 */
function RegistrationHelp({ integration }: { integration: AdminBitbucketIntegration }) {
  const copy = (text: string) => {
    void navigator.clipboard.writeText(text).then(
      () => toast.success('Callback URL copied.'),
      () => toast.error("We couldn't copy it. Select the URL and copy it yourself."),
    )
  }
  return (
    <section aria-labelledby="bitbucket-registration-title" className="rounded-lg border border-border/60 bg-background/40 p-4">
      <h4 id="bitbucket-registration-title" className="font-semibold">
        Register an OAuth consumer
      </h4>
      <p className="mt-1 text-sm text-muted-foreground">
        In Bitbucket, open the workspace&apos;s settings → OAuth consumers → Add consumer, enter these values, then enter the
        consumer&apos;s Key and Secret here.
      </p>
      <dl className="mt-2 divide-y divide-border/60">
        <Row term="Callback URL">
          <span className="flex flex-wrap items-center gap-2">
            <code className="break-all font-mono" data-testid="bitbucket-callback-url">
              {integration.callbackUrl}
            </code>
            {typeof navigator !== 'undefined' && 'clipboard' in navigator && (
              <Button
                variant="ghost"
                aria-label="Copy the callback URL"
                onClick={() => {
                  copy(integration.callbackUrl)
                }}
              >
                Copy
              </Button>
            )}
          </span>
        </Row>
        <Row term="Permissions to tick">
          <ul aria-label="Consumer permissions" className="flex flex-wrap gap-2" data-testid="bitbucket-permissions">
            {integration.permissions.map((p) => (
              <li key={p}>
                <code className="font-mono">{p}</code>
              </li>
            ))}
          </ul>
        </Row>
        <Row term="App passwords">
          Not supported: Atlassian has removed them. Users connect with OAuth (this consumer) or an Atlassian API token with
          scopes.
        </Row>
      </dl>
    </section>
  )
}

const consumerSchema = z.object({
  clientId: z
    .string()
    .trim()
    .min(1, { error: "Enter the consumer's Key." })
    .max(OAUTH_MAX, { error: `The Key is at most ${String(OAUTH_MAX)} characters.` }),
  clientSecret: z.string().max(OAUTH_MAX, { error: `The Secret is at most ${String(OAUTH_MAX)} characters.` }),
})
type ConsumerValues = z.infer<typeof consumerSchema>
const consumerIds = { clientId: 'bitbucket-client-id', clientSecret: 'bitbucket-client-secret' } as const

/** FR-133: the consumer's Key and Secret (write-only: empty keeps the saved one). */
function ConsumerForm({ integration }: { integration: AdminBitbucketIntegration }) {
  const update = useUpdateBitbucketConsumer()
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    formState: { errors, isSubmitted },
  } = useForm<ConsumerValues>({
    resolver: zodResolver(consumerSchema),
    defaultValues: { clientId: integration.clientId ?? '', clientSecret: '' },
  })

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    if (!v.clientSecret && !integration.hasClientSecret) {
      setError('clientSecret', { type: 'required', message: "Enter the consumer's Secret." }, { shouldFocus: true })
      return
    }
    update.mutate(
      { clientId: v.clientId, ...(v.clientSecret ? { clientSecret: v.clientSecret } : {}) },
      {
        onSuccess: () => {
          setValue('clientSecret', '')
          update.reset()
          toast.success('Bitbucket consumer saved.')
        },
        onError: (error) => {
          setValue('clientSecret', '')
          update.reset()
          const unmatched = applyFieldErrors(error, setError, ['clientId', 'clientSecret'])
          setServerErrors(
            unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error, 'bitbucketConsumer')],
          )
        },
      },
    )
  })

  return (
    <form noValidate onSubmit={(e) => void onSubmit(e)} aria-label="Bitbucket OAuth consumer" className="flex flex-col gap-4">
      <ErrorSummary
        title="We couldn't save the consumer"
        errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, consumerIds) : [])]}
      />
      <TextField
        id={consumerIds.clientId}
        label="Key"
        hint="The consumer's Key (its OAuth client id)."
        autoComplete="off"
        spellCheck={false}
        maxLength={OAUTH_MAX}
        error={errors.clientId?.message}
        {...register('clientId')}
      />
      <TextField
        id={consumerIds.clientSecret}
        label="Secret"
        type="password"
        autoComplete="new-password"
        spellCheck={false}
        maxLength={OAUTH_MAX}
        hint={
          integration.hasClientSecret
            ? 'A secret is saved. Leave this empty to keep it. It is stored encrypted and never shown.'
            : 'Stored encrypted and never shown again.'
        }
        error={errors.clientSecret?.message}
        {...register('clientSecret')}
      />
      <div>
        <Button type="submit" pending={update.isPending} pendingLabel="Saving…">
          Save consumer
        </Button>
      </div>
    </form>
  )
}

/**
 * Admin → Integrations → Bitbucket (FR-133): the OAuth consumer (Key and write-only Secret), its registration help
 * (callback URL and permissions), and Remove. A consumer set in configuration (`Bitbucket__OAuth__*`) is read-only.
 */
export function AdminBitbucketPanel() {
  const integration = useAdminBitbucket()
  const remove = useDeleteBitbucketConsumer()
  const [removing, setRemoving] = useState(false)
  const [actionError, setActionError] = useState<string | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  const confirmRemove = () => {
    setActionError(null)
    remove.mutate(undefined, {
      onSuccess: () => {
        setRemoving(false)
        toast.success('Bitbucket consumer removed.')
        headingRef.current?.focus()
      },
      onError: (error) => {
        setRemoving(false)
        setActionError(errorMessage(error, 'bitbucketConsumer'))
      },
    })
  }

  let body
  if (integration.isPending) {
    body = <LoadingState label="Loading the Bitbucket consumer…" />
  } else if (integration.isError) {
    body = (
      <ErrorState error={integration.error} title="The Bitbucket consumer couldn't load" onRetry={() => void integration.refetch()} />
    )
  } else {
    const i = integration.data
    const configured = i.source === 'configuration'
    const saved = i.clientId !== null || i.hasClientSecret
    body = (
      <div className="flex flex-col gap-4">
        <RegistrationHelp integration={i} />
        <dl className="divide-y divide-border/60">
          <Row term="Status">
            <span className="flex flex-wrap items-center gap-2">
              <StatusBadge tone={i.oauthConfigured ? 'success' : 'neutral'}>
                <span data-testid="bitbucket-consumer-status">{i.oauthConfigured ? 'Set up' : 'Not set up'}</span>
              </StatusBadge>
              {i.oauthConfigured
                ? '"Continue with Bitbucket" and OAuth connections work.'
                : 'Users can only connect with an Atlassian API token, and sign-in with Bitbucket is off.'}
            </span>
          </Row>
          <Row term="Source">
            {configured ? (
              <span data-testid="bitbucket-config-managed">
                Set in configuration (<code>Bitbucket__OAuth__ClientId</code> and <code>Bitbucket__OAuth__ClientSecret</code>),
                which takes precedence. Change it there and restart AIDE; it can&apos;t be changed or removed here.
              </span>
            ) : saved ? (
              'Saved here. The secret is stored encrypted.'
            ) : (
              'Not saved yet.'
            )}
          </Row>
          {configured && (
            <Row term="Key">
              <code className="break-all font-mono">{i.clientId ?? 'not set'}</code>
              {i.hasClientSecret ? ' · secret set' : ''}
            </Row>
          )}
          <Row term="In use">
            {i.connectionCount} Bitbucket connection{i.connectionCount === 1 ? '' : 's'}
          </Row>
        </dl>
        {!configured && <ConsumerForm key={`${i.clientId ?? ''}-${i.updatedAt}`} integration={i} />}
        {!configured && saved && (
          <div>
            <Button
              variant="danger"
              onClick={() => {
                setRemoving(true)
              }}
            >
              Remove consumer
            </Button>
          </div>
        )}
      </div>
    )
  }

  return (
    <section aria-labelledby="admin-bitbucket-title" className={cn(panelClass, 'flex flex-col gap-4 p-5')}>
      <div>
        <h3 id="admin-bitbucket-title" ref={headingRef} tabIndex={-1} className="flex items-center gap-2 text-lg outline-none">
          <BitbucketLogo className="size-5" />
          Bitbucket
        </h3>
        <p className="mt-1 text-sm text-muted-foreground">
          The Bitbucket Cloud OAuth consumer for &quot;Continue with Bitbucket&quot; and OAuth connections.
        </p>
      </div>
      {actionError && (
        <Alert tone="error" role="alert" title="We couldn't remove the consumer">
          <p>{actionError}</p>
        </Alert>
      )}
      {body}
      <ConfirmDialog
        open={removing}
        title="Remove the Bitbucket consumer?"
        confirmLabel="Remove consumer"
        pendingLabel="Removing…"
        pending={remove.isPending}
        destructive
        onConfirm={confirmRemove}
        onCancel={() => {
          setRemoving(false)
        }}
      >
        <p>
          &quot;Continue with Bitbucket&quot; and OAuth connect stop being offered, and users&apos; OAuth connections stop
          working at their next token refresh. API-token connections keep working.
        </p>
      </ConfirmDialog>
    </section>
  )
}
