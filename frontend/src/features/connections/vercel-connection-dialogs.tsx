import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'

import { ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError } from '../../lib/errors'
import { useCreateVercelConnection, useRenameVercelConnection, type VercelConnection } from './vercel-connections-api'

/** Mirrors the LLM connection limits (label 100, token 1000). */
const LABEL_MAX = 100
const TOKEN_MAX = 1000

const label = z
  .string()
  .trim()
  .min(1, { error: 'Give the token a label, such as "Acme team".' })
  .max(LABEL_MAX, { error: `Labels are at most ${String(LABEL_MAX)} characters.` })

const addSchema = z.object({
  label,
  token: z
    .string()
    .min(1, { error: 'Paste the Vercel access token.' })
    .max(TOKEN_MAX, { error: `The token is at most ${String(TOKEN_MAX)} characters.` })
    .refine((v) => !/[\s\p{Cc}]/u.test(v), { error: "The token can't contain spaces or line breaks." }),
})

type AddValues = z.infer<typeof addSchema>

const addIds = { label: 'vercel-connection-label', token: 'vercel-connection-token' } as const

/**
 * FR-87: add a Vercel access token. AIDE checks it with Vercel before saving (CREDENTIAL_INVALID saves nothing). The
 * token is a password field, cleared after every attempt that reached the server, and the mutation (which holds it)
 * is reset straight away, so the token never stays in memory.
 */
export function AddVercelTokenDialog({
  open,
  onClose,
  onAdded,
}: {
  open: boolean
  onClose: () => void
  onAdded: (c: VercelConnection) => void
}) {
  const create = useCreateVercelConnection()
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    reset,
    formState: { errors, isSubmitted },
  } = useForm<AddValues>({ resolver: zodResolver(addSchema), defaultValues: { label: '', token: '' } })

  const close = () => {
    reset()
    setServerErrors([])
    create.reset()
    onClose()
  }

  const onSubmit = handleSubmit((values) => {
    setServerErrors([])
    create.mutate(
      { label: values.label, token: values.token },
      {
        onSuccess: (created) => {
          reset()
          create.reset()
          onAdded(created)
        },
        onError: (error) => {
          setValue('token', '')
          create.reset()
          if (isApiError(error, 'CREDENTIAL_INVALID')) {
            setError('token', { type: 'server', message: errorMessage(error, 'vercelConnection') })
            return
          }
          const unmatched = applyFieldErrors(error, setError, ['label', 'token'])
          setServerErrors(
            unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error, 'vercelConnection')],
          )
        },
      },
    )
  })

  return (
    <ModalDialog
      open={open}
      title="Add a Vercel token"
      description="AIDE checks the token with Vercel before saving it. It's stored encrypted and never shown again."
      busy={create.isPending}
      onClose={close}
    >
      <form noValidate onSubmit={(e) => void onSubmit(e)} aria-label="Add a Vercel token" className="flex flex-col gap-4">
        <ErrorSummary
          title="We couldn't add the token"
          errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, addIds) : [])]}
        />
        <TextField id={addIds.label} label="Label" maxLength={LABEL_MAX} autoComplete="off" error={errors.label?.message} {...register('label')} />
        <TextField
          id={addIds.token}
          label="Access token"
          type="password"
          autoComplete="new-password"
          spellCheck={false}
          maxLength={TOKEN_MAX}
          hint="Create it in Vercel → Account Settings → Tokens. Use a team- or account-scoped token, ideally with an expiry. Only its last 4 characters are shown after saving."
          error={errors.token?.message}
          {...register('token')}
        />
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="ghost" onClick={close} disabled={create.isPending}>
            Cancel
          </Button>
          <Button type="submit" pending={create.isPending} pendingLabel="Checking the token…">
            Add token
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}

const renameSchema = z.object({ label })
type RenameValues = z.infer<typeof renameSchema>

/** FR-87: rename a Vercel connection (the token is unchanged). */
export function RenameVercelTokenDialog({
  connection,
  onClose,
}: {
  connection: VercelConnection
  onClose: (renamed: boolean) => void
}) {
  const rename = useRenameVercelConnection()
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
            setServerError(errorMessage(error, 'vercelConnection'))
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
      <form noValidate onSubmit={(e) => void onSubmit(e)} aria-label="Rename the token" className="flex flex-col gap-4">
        <ErrorSummary title="We couldn't rename the token" errors={serverError ? [{ message: serverError }] : []} />
        <TextField
          id="vercel-connection-rename"
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
