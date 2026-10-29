import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'

import { ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, isApiError, platformConnectionMessage } from '../../lib/errors'
import { GitHostLink } from '../git-hosts/git-host-link'
import {
  useCreatePlatformConnection,
  useRenamePlatformConnection,
  type PlatformConnection,
  type PlatformProvider,
} from './platform-connections-api'
import { platformProviders, type PlatformProviderInfo } from './platform-providers'

/** Backend PlatformConnectionRules: label 100, token 1000, account id 200 (letters, digits, `.`, `_`, `:`, `-`). */
const LABEL_MAX = 100
const TOKEN_MAX = 1000
const ACCOUNT_ID_MAX = 200

const label = z
  .string()
  .trim()
  .min(1, { error: 'Give the connection a label, such as "Acme team".' })
  .max(LABEL_MAX, { error: `Labels are at most ${String(LABEL_MAX)} characters.` })

const addSchema = (info: PlatformProviderInfo) =>
  z.object({
    label,
    token: z
      .string()
      .min(1, { error: `Paste the ${info.name} ${info.tokenNoun}.` })
      .max(TOKEN_MAX, { error: `The token is at most ${String(TOKEN_MAX)} characters.` })
      .refine((v) => !/[\s\p{Cc}]/u.test(v), { error: "The token can't contain spaces or line breaks." }),
    accountId: z
      .string()
      .trim()
      .max(ACCOUNT_ID_MAX, { error: `The ID is at most ${String(ACCOUNT_ID_MAX)} characters.` })
      .refine((v) => v === '' || /^[A-Za-z0-9._:-]+$/.test(v), { error: "Use letters, digits, '.', '_', ':' or '-'." }),
  })

type AddValues = z.infer<ReturnType<typeof addSchema>>

const idsFor = (provider: PlatformProvider) =>
  ({
    label: `${provider}-connection-label`,
    token: `${provider}-connection-token`,
    accountId: `${provider}-connection-account-id`,
  }) as const

/** FR-142, §4.20/§4.21: exactly what to create on the provider, with a link to its token page. */
function WhatToCreate({ info }: { info: PlatformProviderInfo }) {
  const titleId = `${info.id}-token-what-title`
  return (
    <section aria-labelledby={titleId} className="rounded-lg border border-border/60 bg-background/40 p-3 text-sm">
      <h3 id={titleId} className="font-semibold">
        What to create on {info.name}
      </h3>
      <ul className="mt-1 list-disc space-y-1 pl-5" data-testid="platform-token-guidance">
        <li>
          <strong>Token type:</strong> {info.tokenType}
        </li>
        <li>
          <strong>{info.permissions.length > 0 ? 'Permissions:' : 'Scopes:'}</strong> {info.scopeNote}
          {info.permissions.length > 0 && (
            <ul aria-label={`${info.name} permissions to grant`} className="mt-1 list-[circle] space-y-0.5 pl-5">
              {info.permissions.map((p) => (
                <li key={p}>{p}</li>
              ))}
            </ul>
          )}
        </li>
        <li>
          <strong>Expiry:</strong> {info.expiry}
        </li>
        {info.notes?.map((n) => (
          <li key={n}>{n}</li>
        ))}
      </ul>
      <p className="mt-2">
        <GitHostLink site={info.name} href={info.tokenPageUrl}>{`Create a token on ${info.tokenPageSite}`}</GitHostLink>
      </p>
    </section>
  )
}

/**
 * FR-142: add a platform connection (a pasted token with a label). AIDE checks it with the platform before saving:
 * CREDENTIAL_INVALID and CONNECTION_SCOPE_INSUFFICIENT save nothing, while a token missing permissions is saved as
 * `insufficient_scope` (the row lists them). The token is a password field, cleared after every attempt that reached
 * the server, and the mutation holding it is reset at once, so the token never stays in memory.
 */
export function AddPlatformConnectionDialog({
  provider,
  onClose,
  onAdded,
}: {
  provider: PlatformProvider
  onClose: () => void
  onAdded: (c: PlatformConnection) => void
}) {
  const info = platformProviders[provider]
  const ids = idsFor(provider)
  const create = useCreatePlatformConnection()
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    formState: { errors, isSubmitted },
  } = useForm<AddValues>({ resolver: zodResolver(addSchema(info)), defaultValues: { label: '', token: '', accountId: '' } })

  const close = () => {
    create.reset()
    onClose()
  }

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    const accountId = info.accountIdField && v.accountId !== '' ? v.accountId : null
    create.mutate(
      { provider, label: v.label, token: v.token, accountId },
      {
        onSuccess: (created) => {
          create.reset()
          onAdded(created)
        },
        onError: (error) => {
          setValue('token', '')
          create.reset()
          if (isApiError(error, 'CREDENTIAL_INVALID') || isApiError(error, 'CONNECTION_SCOPE_INSUFFICIENT')) {
            setError('token', { type: 'server', message: platformConnectionMessage(error, info) }, { shouldFocus: true })
            return
          }
          const unmatched = applyFieldErrors(error, setError, ['label', 'token', 'accountId'])
          setServerErrors(
            unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [platformConnectionMessage(error, info)],
          )
        },
      },
    )
  })

  const title = `Add a ${info.name} connection`
  return (
    <ModalDialog
      open
      title={title}
      description={`AIDE checks the token with ${info.name} before saving it. It's stored encrypted and never shown again; only its last 4 characters are.`}
      busy={create.isPending}
      onClose={close}
      className="sm:max-w-lg"
    >
      <form noValidate onSubmit={(e) => void onSubmit(e)} aria-label={title} className="flex flex-col gap-4">
        <ErrorSummary
          title="We couldn't add the connection"
          errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, ids) : [])]}
        />
        <WhatToCreate info={info} />
        <TextField id={ids.label} label="Label" maxLength={LABEL_MAX} autoComplete="off" error={errors.label?.message} {...register('label')} />
        <TextField
          id={ids.token}
          label={info.tokenNoun.charAt(0).toUpperCase() + info.tokenNoun.slice(1)}
          type="password"
          autoComplete="new-password"
          spellCheck={false}
          maxLength={TOKEN_MAX}
          error={errors.token?.message}
          {...register('token')}
        />
        {info.accountIdField && (
          <TextField
            id={ids.accountId}
            label={`${info.accountIdField.label} (optional)`}
            hint={info.accountIdField.hint}
            maxLength={ACCOUNT_ID_MAX}
            autoComplete="off"
            spellCheck={false}
            error={errors.accountId?.message}
            {...register('accountId')}
          />
        )}
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="ghost" onClick={close} disabled={create.isPending}>
            Cancel
          </Button>
          <Button type="submit" pending={create.isPending} pendingLabel="Checking the token…">
            Add connection
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}

const renameSchema = z.object({ label })
type RenameValues = z.infer<typeof renameSchema>

/** FR-142: rename a platform connection (the token is unchanged). */
export function RenamePlatformConnectionDialog({
  connection,
  onClose,
}: {
  connection: PlatformConnection
  onClose: (renamed: boolean) => void
}) {
  const info = platformProviders[connection.provider]
  const rename = useRenamePlatformConnection()
  const [serverError, setServerError] = useState<string | null>(null)
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<RenameValues>({ resolver: zodResolver(renameSchema), defaultValues: { label: connection.label } })

  const onSubmit = handleSubmit((values) => {
    setServerError(null)
    rename.mutate(
      { id: connection.id, label: values.label },
      {
        onSuccess: () => {
          onClose(true)
        },
        onError: (error) => {
          const unmatched = applyFieldErrors(error, setError, ['label'])
          if (unmatched.length > 0) {
            setServerError(unmatched.join(' '))
          } else if (!isApiError(error, 'VALIDATION')) {
            setServerError(platformConnectionMessage(error, info))
          }
        },
      },
    )
  })

  return (
    <ModalDialog
      open
      title={`Rename "${connection.label}"`}
      busy={rename.isPending}
      onClose={() => {
        onClose(false)
      }}
    >
      <form noValidate onSubmit={(e) => void onSubmit(e)} aria-label="Rename the connection" className="flex flex-col gap-4">
        <ErrorSummary title="We couldn't rename the connection" errors={serverError ? [{ message: serverError }] : []} />
        <TextField
          id="platform-connection-rename"
          label="Label"
          maxLength={LABEL_MAX}
          autoComplete="off"
          error={errors.label?.message}
          {...register('label')}
        />
        <div className="flex flex-wrap justify-end gap-2">
          <Button
            variant="ghost"
            disabled={rename.isPending}
            onClick={() => {
              onClose(false)
            }}
          >
            Cancel
          </Button>
          <Button type="submit" pending={rename.isPending} pendingLabel="Saving…">
            Save label
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}
