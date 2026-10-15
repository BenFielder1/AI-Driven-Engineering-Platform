import { zodResolver } from '@hookform/resolvers/zod'
import { useRef, useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'

import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError } from '../../lib/errors'
import {
  useApiTokens,
  useCreateApiToken,
  useRevokeApiToken,
  type ApiToken,
  type CreatedApiToken,
} from './tokens-api'

function todayIso(): string {
  const now = new Date()
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${String(now.getFullYear())}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
}

const createTokenSchema = z.object({
  name: z
    .string()
    .trim()
    .min(1, { error: 'Give the token a name, such as "Laptop CLI".' })
    .max(100, { error: 'Token names are at most 100 characters.' }),
  expiresOn: z
    .string()
    .refine((v) => v === '' || v >= todayIso(), { error: 'The expiry date must be today or later.' }),
})

type CreateTokenValues = z.infer<typeof createTokenSchema>

const fieldIds = { name: 'token-name', expiresOn: 'token-expires' } as const

/** Expiry is the end of the chosen local day. */
function toExpiresAt(date: string): string | null {
  return date ? new Date(`${date}T23:59:59`).toISOString() : null
}

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' })
const formatDate = (iso: string | null) => (iso ? dateFormat.format(new Date(iso)) : '—')

type TokenStatus = 'Active' | 'Revoked' | 'Expired'

function tokenStatus(token: ApiToken, now = Date.now()): TokenStatus {
  if (token.revokedAt) {
    return 'Revoked'
  }
  if (token.expiresAt && new Date(token.expiresAt).getTime() <= now) {
    return 'Expired'
  }
  return 'Active'
}

/** The plain token, shown exactly once after creation (FR-4). */
function NewTokenNotice({ token, onDone }: { token: CreatedApiToken; onDone: () => void }) {
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle')
  const inputRef = useRef<HTMLInputElement>(null)

  async function copy() {
    try {
      await navigator.clipboard.writeText(token.token)
      setCopyState('copied')
    } catch {
      inputRef.current?.select()
      setCopyState('failed')
    }
  }

  return (
    <Alert tone="success" title={`Token "${token.name}" created`} className="mb-4">
      <p className="mt-1 font-medium">Copy it now. You won&apos;t be able to see it again.</p>
      <div className="mt-3 flex flex-col gap-2 sm:flex-row sm:items-end">
        <div className="flex min-w-0 flex-1 flex-col gap-1">
          <label htmlFor="new-token-value" className="text-sm font-medium">
            Your new token
          </label>
          <input
            ref={inputRef}
            id="new-token-value"
            readOnly
            value={token.token}
            onFocus={(e) => {
              e.currentTarget.select()
            }}
            className="min-h-10 w-full rounded-md border border-field-border bg-background px-3 py-2 font-mono text-sm text-foreground"
          />
        </div>
        <Button variant="secondary" onClick={() => void copy()}>
          Copy token
        </Button>
        <Button variant="ghost" onClick={onDone}>
          Done
        </Button>
      </div>
      <p role="status" aria-live="polite" className="mt-2 text-sm">
        {copyState === 'copied' && 'Copied to the clipboard.'}
        {copyState === 'failed' && "Couldn't copy automatically. The token is selected: copy it manually."}
      </p>
    </Alert>
  )
}

function CreateTokenForm({ onCreated }: { onCreated: (token: CreatedApiToken) => void }) {
  const create = useCreateApiToken()
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    reset,
    formState: { errors, isSubmitted },
  } = useForm<CreateTokenValues>({
    resolver: zodResolver(createTokenSchema),
    defaultValues: { name: '', expiresOn: '' },
  })

  const onSubmit = handleSubmit((values) => {
    setServerErrors([])
    create.mutate(
      { name: values.name, expiresAt: toExpiresAt(values.expiresOn) },
      {
        onSuccess: (token) => {
          reset()
          onCreated(token)
        },
        onError: (error) => {
          const unmatched = applyFieldErrors(error, setError, ['name'])
          const expiry = isApiError(error) ? error.fieldErrors.expiresAt : undefined
          if (expiry) {
            setError('expiresOn', { type: 'server', message: expiry.join(' ') })
          }
          const rest = unmatched.filter((m) => !expiry?.includes(m))
          if (rest.length > 0) {
            setServerErrors(rest)
          } else if (!isApiError(error, 'VALIDATION')) {
            setServerErrors([errorMessage(error)])
          }
        },
      },
    )
  })

  return (
    <form noValidate onSubmit={(e) => void onSubmit(e)} aria-labelledby="create-token-title" className="mb-6">
      <h3 id="create-token-title" className="mb-3 font-semibold">
        Create a token
      </h3>
      <ErrorSummary
        title="We couldn't create the token"
        errors={[
          ...serverErrors.map((message) => ({ message })),
          ...(isSubmitted ? fieldErrorSummary(errors, fieldIds) : []),
        ]}
      />
      <div className="mt-2 flex flex-col gap-4 sm:flex-row sm:items-start">
        <TextField
          id={fieldIds.name}
          label="Name"
          className="flex-1"
          error={errors.name?.message}
          {...register('name')}
        />
        <TextField
          id={fieldIds.expiresOn}
          label="Expires on (optional)"
          type="date"
          min={todayIso()}
          error={errors.expiresOn?.message}
          {...register('expiresOn')}
        />
        <Button type="submit" pending={create.isPending} pendingLabel="Creating…" className="sm:mt-6">
          Create token
        </Button>
      </div>
    </form>
  )
}

/** /account → API tokens (FR-4): create (plain token shown once), list, revoke with confirmation. */
export function ApiTokensSection() {
  const tokens = useApiTokens()
  const revoke = useRevokeApiToken()
  const [created, setCreated] = useState<CreatedApiToken | null>(null)
  const [toRevoke, setToRevoke] = useState<ApiToken | null>(null)
  const [revokeError, setRevokeError] = useState<unknown>(null)

  function confirmRevoke() {
    if (!toRevoke) {
      return
    }
    setRevokeError(null)
    revoke.mutate(toRevoke.id, {
      onSuccess: () => {
        setToRevoke(null)
      },
      onError: (error) => {
        setToRevoke(null)
        setRevokeError(error)
      },
    })
  }

  return (
    <section aria-labelledby="tokens-title" className="rounded-xl border border-border/60 bg-card/70 shadow-sm backdrop-blur-md p-5">
      <h2 id="tokens-title" className="text-lg">
        API tokens
      </h2>
      <p className="mb-4 mt-1 text-sm text-muted-foreground">
        Personal tokens let the <code>aide</code> CLI act as you. Treat them like passwords.
      </p>

      {created && (
        <NewTokenNotice
          token={created}
          onDone={() => {
            setCreated(null)
          }}
        />
      )}
      <CreateTokenForm onCreated={setCreated} />

      {revokeError !== null && (
        <Alert tone="error" role="alert" title="We couldn't revoke the token" className="mb-4">
          <p>{errorMessage(revokeError)}</p>
        </Alert>
      )}

      <h3 className="mb-3 font-semibold">Your tokens</h3>
      {tokens.isPending && <LoadingState label="Loading tokens…" />}
      {tokens.isError && <ErrorState error={tokens.error} onRetry={() => void tokens.refetch()} />}
      {tokens.isSuccess && tokens.data.length === 0 && (
        <EmptyState title="No tokens yet">Create one above to use the CLI.</EmptyState>
      )}
      {tokens.isSuccess && tokens.data.length > 0 && (
        <ul className="divide-y divide-border/60 rounded-lg border border-border/60" aria-label="API tokens">
          {tokens.data.map((token) => {
            const status = tokenStatus(token)
            return (
              <li key={token.id} className="flex flex-col gap-2 p-3 sm:flex-row sm:items-center sm:justify-between">
                <div className="min-w-0">
                  <p className="break-words font-medium">
                    {token.name}{' '}
                    <span
                      className={
                        'ml-1 rounded px-1.5 py-0.5 text-xs font-medium ' +
                        (status === 'Active' ? 'bg-success/10 text-success' : 'bg-muted text-muted-foreground')
                      }
                    >
                      {status}
                    </span>
                  </p>
                  <p className="text-sm text-muted-foreground">
                    Created {formatDate(token.createdAt)} · Last used {formatDate(token.lastUsedAt)} · Expires{' '}
                    {token.expiresAt ? formatDate(token.expiresAt) : 'never'}
                  </p>
                </div>
                {status === 'Active' && (
                  <Button
                    variant="secondary"
                    aria-label={`Revoke ${token.name}`}
                    onClick={() => {
                      setToRevoke(token)
                    }}
                  >
                    Revoke
                  </Button>
                )}
              </li>
            )
          })}
        </ul>
      )}

      <ConfirmDialog
        open={toRevoke !== null}
        title={`Revoke "${toRevoke?.name ?? ''}"?`}
        confirmLabel="Revoke token"
        pendingLabel="Revoking…"
        pending={revoke.isPending}
        destructive
        onConfirm={confirmRevoke}
        onCancel={() => {
          setToRevoke(null)
        }}
      >
        Anything using this token, such as the CLI, stops working immediately. This can&apos;t be undone.
      </ConfirmDialog>
    </section>
  )
}
