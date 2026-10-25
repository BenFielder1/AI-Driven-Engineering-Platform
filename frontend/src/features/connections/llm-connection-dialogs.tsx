import { zodResolver } from '@hookform/resolvers/zod'
import { useEffect, useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'

import { ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { SelectField } from '../../components/ui/select-field'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError } from '../../lib/errors'
import { llmProviderLabels } from '../projects/labels'
import {
  keyedProviders,
  useCreateLlmConnection,
  useLlmProviders,
  useRenameLlmConnection,
  type LlmCredential,
} from './connections-api'

/** Mirrors LlmConnectionRules (MaxLabelLength, MaxApiKeyLength). */
const LABEL_MAX = 100
const API_KEY_MAX = 1000

const label = z
  .string()
  .trim()
  .min(1, { error: 'Give the key a label, such as "Work key".' })
  .max(LABEL_MAX, { error: `Labels are at most ${String(LABEL_MAX)} characters.` })

const addKeySchema = z.object({
  provider: z.enum(keyedProviders),
  label,
  apiKey: z
    .string()
    .min(1, { error: 'Paste the API key.' })
    .max(API_KEY_MAX, { error: `The API key is at most ${String(API_KEY_MAX)} characters.` })
    .refine((v) => !/[\s\p{Cc}]/u.test(v), { error: "The API key can't contain spaces or line breaks." }),
})

type AddKeyValues = z.infer<typeof addKeySchema>

const addIds = { provider: 'llm-connection-provider', label: 'llm-connection-label', apiKey: 'llm-connection-key' } as const

/**
 * FR-78: add an Anthropic or OpenAI key. The API checks it with the provider before saving; a rejected key
 * (CREDENTIAL_INVALID) saves nothing. The key input is a password field and is cleared after every attempt that
 * reached the server, so the key is never shown back.
 */
export function AddLlmKeyDialog({ open, onClose, onAdded }: { open: boolean; onClose: () => void; onAdded: (c: LlmCredential) => void }) {
  const create = useCreateLlmConnection()
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    getValues,
    reset,
    formState: { errors, isSubmitted },
  } = useForm<AddKeyValues>({
    resolver: zodResolver(addKeySchema),
    defaultValues: { provider: 'anthropic', label: '', apiKey: '' },
  })

  // FR-157: only the keyed providers enabled on this server are offered.
  const llmProviders = useLlmProviders(open)
  const offered: readonly (typeof keyedProviders)[number][] = llmProviders.data
    ? keyedProviders.filter((p) => llmProviders.data.some((o) => o.provider === p))
    : keyedProviders
  const firstOffered = offered[0]
  useEffect(() => {
    if (firstOffered && !offered.some((p) => p === getValues('provider'))) {
      setValue('provider', firstOffered)
    }
  }, [firstOffered, offered, getValues, setValue])

  const close = () => {
    reset()
    setServerErrors([])
    create.reset()
    onClose()
  }

  const onSubmit = handleSubmit((values) => {
    setServerErrors([])
    create.mutate(
      { provider: values.provider, label: values.label, apiKey: values.apiKey },
      {
        // The mutation's variables hold the plaintext key in the MutationCache: drop them after every outcome
        // (and on close), and clear the form field, so the key lives only as long as the request.
        onSuccess: (created) => {
          reset()
          create.reset()
          onAdded(created)
        },
        onError: (error) => {
          setValue('apiKey', '')
          create.reset()
          if (isApiError(error, 'CREDENTIAL_INVALID')) {
            setError('apiKey', { type: 'server', message: errorMessage(error, 'llmConnection') })
            return
          }
          const unmatched = applyFieldErrors(error, setError, ['provider', 'label', 'apiKey'])
          setServerErrors(
            unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error, 'llmConnection')],
          )
        },
      },
    )
  })

  return (
    <ModalDialog
      open={open}
      title="Add an LLM key"
      description="The key is checked with the provider before it's saved. It's stored encrypted and never shown again."
      busy={create.isPending}
      onClose={close}
    >
      <form noValidate onSubmit={(e) => void onSubmit(e)} aria-label="Add an LLM key" className="flex flex-col gap-4">
        <ErrorSummary
          title="We couldn't add the key"
          errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, addIds) : [])]}
        />
        <SelectField id={addIds.provider} label="Provider" error={errors.provider?.message} {...register('provider')}>
          {offered.map((p) => (
            <option key={p} value={p}>
              {llmProviderLabels[p]}
            </option>
          ))}
        </SelectField>
        <TextField
          id={addIds.label}
          label="Label"
          maxLength={LABEL_MAX}
          autoComplete="off"
          error={errors.label?.message}
          {...register('label')}
        />
        <TextField
          id={addIds.apiKey}
          label="API key"
          type="password"
          autoComplete="new-password"
          spellCheck={false}
          maxLength={API_KEY_MAX}
          hint="Only its last 4 characters are shown after saving."
          error={errors.apiKey?.message}
          {...register('apiKey')}
        />
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="ghost" onClick={close} disabled={create.isPending}>
            Cancel
          </Button>
          <Button type="submit" pending={create.isPending} pendingLabel="Checking the key…">
            Add key
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}

const renameSchema = z.object({ label })
type RenameValues = z.infer<typeof renameSchema>

/** FR-78: rename a connection (the key is unchanged). */
export function RenameLlmKeyDialog({ credential, onClose }: { credential: LlmCredential; onClose: (renamed: boolean) => void }) {
  const rename = useRenameLlmConnection()
  const [serverError, setServerError] = useState<string | null>(null)
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<RenameValues>({ resolver: zodResolver(renameSchema), defaultValues: { label: credential.label } })

  const onSubmit = handleSubmit((values) => {
    setServerError(null)
    rename.mutate(
      { id: credential.id, label: values.label },
      {
        onSuccess: () => {
          onClose(true)
        },
        onError: (error) => {
          const unmatched = applyFieldErrors(error, setError, ['label'])
          if (unmatched.length > 0) {
            setServerError(unmatched.join(' '))
          } else if (!isApiError(error, 'VALIDATION')) {
            setServerError(errorMessage(error, 'llmConnection'))
          }
        },
      },
    )
  })

  return (
    <ModalDialog
      open
      title={`Rename "${credential.label}"`}
      busy={rename.isPending}
      onClose={() => {
        onClose(false)
      }}
    >
      <form noValidate onSubmit={(e) => void onSubmit(e)} aria-label="Rename the key" className="flex flex-col gap-4">
        <ErrorSummary title="We couldn't rename the key" errors={serverError ? [{ message: serverError }] : []} />
        <TextField
          id="llm-connection-rename"
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
