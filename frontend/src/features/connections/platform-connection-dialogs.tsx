import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'

import { ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { SelectField } from '../../components/ui/select-field'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessages, isApiError, platformConnectionMessage } from '../../lib/errors'
import { GitHostLink } from '../git-hosts/git-host-link'
import {
  isTrackerPlatform,
  useCreatePlatformConnection,
  usePlatformResources,
  useRenamePlatformConnection,
  useUpdateTrackerConnection,
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

/** FR-165: an optional date from a date field; the connection expires at the start of that day, UTC. */
const expiryDate = z
  .string()
  .refine((v) => v === '' || !Number.isNaN(Date.parse(`${v}T00:00:00Z`)), { error: 'Enter a valid date.' })
const toExpiresAt = (date: string) => (date === '' ? null : new Date(`${date}T00:00:00Z`).toISOString())

/** The site as Jira shows it: a pasted URL is reduced to its host. */
const normalizeSite = (site: string) =>
  site
    .trim()
    .toLowerCase()
    .replace(/^https?:\/\//, '')
    .replace(/\/.*$/, '')

const addSchema = (info: PlatformProviderInfo) =>
  z.object({
    site: z.string().superRefine((v, ctx) => {
      if (info.id === 'jira' && !/^[a-z0-9-]+\.atlassian\.net$/.test(normalizeSite(v))) {
        ctx.addIssue({ code: 'custom', message: errorMessages.TRACKER_SITE_NOT_ALLOWED })
      }
    }),
    email: z.string().superRefine((v, ctx) => {
      if (info.id === 'jira' && !/^[^\s@]+@[^\s@]+$/.test(v.trim())) {
        ctx.addIssue({ code: 'custom', message: 'Enter your Atlassian account email.' })
      }
    }),
    expiresAt: expiryDate,
    acceptanceCriteriaField: z.string().trim().max(100, { error: 'The field ID is at most 100 characters.' }),
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
    site: `${provider}-connection-site`,
    email: `${provider}-connection-email`,
    expiresAt: `${provider}-connection-expires-at`,
    acceptanceCriteriaField: `${provider}-connection-ac-field`,
  }) as const

/** FR-142, §4.20/§4.21: exactly what to create on the provider, with a link to its token page. */
function WhatToCreate({ info }: { info: PlatformProviderInfo }) {
  const titleId = `${info.id}-token-what-title`
  return (
    <section aria-labelledby={titleId} className="rounded-lg border border-border/60 bg-background p-3 text-sm">
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
 * FR-142: add a platform connection (a pasted token with a label). Tevrin checks it with the platform before saving:
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
  } = useForm<AddValues>({ resolver: zodResolver(addSchema(info)), defaultValues: { label: '', token: '', accountId: '', site: '', email: '', expiresAt: '', acceptanceCriteriaField: '' } })

  const close = () => {
    create.reset()
    onClose()
  }

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    const accountId = info.accountIdField && v.accountId !== '' ? v.accountId : null
    create.mutate(
      {
        provider,
        label: v.label,
        token: v.token,
        accountId,
        ...(isTrackerPlatform(provider) ? { expiresAt: toExpiresAt(v.expiresAt) } : {}),
        ...(provider === 'jira'
          ? {
              site: normalizeSite(v.site),
              email: v.email.trim(),
              ...(v.acceptanceCriteriaField === '' ? {} : { acceptanceCriteriaField: v.acceptanceCriteriaField }),
            }
          : {}),
      },
      {
        onSuccess: (created) => {
          create.reset()
          onAdded(created)
        },
        onError: (error) => {
          setValue('token', '')
          create.reset()
          if (isApiError(error, 'TRACKER_SITE_NOT_ALLOWED')) {
            setError('site', { type: 'server', message: errorMessages.TRACKER_SITE_NOT_ALLOWED }, { shouldFocus: true })
            return
          }
          if (isApiError(error, 'CREDENTIAL_INVALID') || isApiError(error, 'CONNECTION_SCOPE_INSUFFICIENT')) {
            setError('token', { type: 'server', message: platformConnectionMessage(error, info) }, { shouldFocus: true })
            return
          }
          const unmatched = applyFieldErrors(error, setError, ['label', 'token', 'accountId', 'site', 'email', 'expiresAt', 'acceptanceCriteriaField'])
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
      description={`Tevrin checks the token with ${info.name} before saving it. It's stored encrypted and never shown again; only its last 4 characters are.`}
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
        {provider === 'jira' && (
          <>
            <TextField
              id={ids.site}
              label="Jira site"
              hint="Your Jira Cloud address, like acme.atlassian.net."
              autoComplete="off"
              spellCheck={false}
              error={errors.site?.message}
              {...register('site')}
            />
            <TextField
              id={ids.email}
              label="Atlassian account email"
              type="email"
              hint="Stored encrypted with the token."
              autoComplete="off"
              spellCheck={false}
              error={errors.email?.message}
              {...register('email')}
            />
          </>
        )}
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
        {isTrackerPlatform(provider) && (
          <TextField
            id={ids.expiresAt}
            label="Expiry date (optional)"
            hint={`${info.name} doesn't report when a token expires. Enter it, and Tevrin warns you 14 days before.`}
            type="date"
            error={errors.expiresAt?.message}
            {...register('expiresAt')}
          />
        )}
        {provider === 'jira' && (
          <TextField
            id={ids.acceptanceCriteriaField}
            label="Acceptance criteria field (optional)"
            hint="The ID of your custom field, like customfield_10042. Or leave it empty and pick it from a list with Edit once the connection is saved."
            maxLength={100}
            autoComplete="off"
            spellCheck={false}
            error={errors.acceptanceCriteriaField?.message}
            {...register('acceptanceCriteriaField')}
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

const trackerEditSchema = z.object({ label, expiresAt: expiryDate, acceptanceCriteriaField: z.string() })
type TrackerEditValues = z.infer<typeof trackerEditSchema>

/** FR-165: a Jira or Linear connection's label, expiry date and (Jira) acceptance criteria field, picked from `GET /field`. */
export function EditTrackerConnectionDialog({
  connection,
  onClose,
}: {
  connection: PlatformConnection
  onClose: (saved: boolean) => void
}) {
  const info = platformProviders[connection.provider]
  const jira = connection.provider === 'jira'
  const update = useUpdateTrackerConnection()
  const fields = usePlatformResources(jira ? connection.id : null, jira ? 'fields' : null)
  const current = connection.metadata.acceptanceCriteriaField ?? ''
  const [serverError, setServerError] = useState<string | null>(null)
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<TrackerEditValues>({
    resolver: zodResolver(trackerEditSchema),
    defaultValues: {
      label: connection.label,
      expiresAt: connection.expiresAt?.slice(0, 10) ?? '',
      acceptanceCriteriaField: current,
    },
  })

  const onSubmit = handleSubmit((v) => {
    setServerError(null)
    update.mutate(
      {
        id: connection.id,
        label: v.label,
        ...(v.expiresAt === '' ? { clearExpiresAt: true } : { expiresAt: toExpiresAt(v.expiresAt) }),
        ...(jira && fields.isSuccess ? { acceptanceCriteriaField: v.acceptanceCriteriaField } : {}),
      },
      {
        onSuccess: () => {
          onClose(true)
        },
        onError: (error) => {
          const unmatched = applyFieldErrors(error, setError, ['label', 'expiresAt', 'acceptanceCriteriaField'])
          if (unmatched.length > 0) {
            setServerError(unmatched.join(' '))
          } else if (!isApiError(error, 'VALIDATION')) {
            setServerError(platformConnectionMessage(error, info))
          }
        },
      },
    )
  })

  const title = `Edit "${connection.label}"`
  return (
    <ModalDialog
      open
      title={title}
      busy={update.isPending}
      onClose={() => {
        onClose(false)
      }}
    >
      <form noValidate onSubmit={(e) => void onSubmit(e)} aria-label="Edit the connection" className="flex flex-col gap-4">
        <ErrorSummary title="We couldn't save the connection" errors={serverError ? [{ message: serverError }] : []} />
        <TextField id="tracker-connection-edit-label" label="Label" maxLength={LABEL_MAX} autoComplete="off" error={errors.label?.message} {...register('label')} />
        <TextField
          id="tracker-connection-edit-expiry"
          label="Expiry date (optional)"
          hint="Tevrin warns you 14 days before. Leave it empty if the token doesn't expire."
          type="date"
          error={errors.expiresAt?.message}
          {...register('expiresAt')}
        />
        {jira &&
          (fields.isError ? (
            <p className="text-sm text-muted-foreground">
              Jira&apos;s field list couldn&apos;t load, so the acceptance criteria field stays as it is. {platformConnectionMessage(fields.error, info)}
            </p>
          ) : fields.isPending ? (
            <p className="text-sm text-muted-foreground">Loading Jira&apos;s fields…</p>
          ) : (
            <SelectField
              label="Acceptance criteria field (optional)"
              hint="The custom field Tevrin imports as each item's acceptance criteria."
              error={errors.acceptanceCriteriaField?.message}
              defaultValue={current}
              {...register('acceptanceCriteriaField')}
            >
              <option value="">None</option>
              {current !== '' && !fields.data.some((f) => f.id === current) && <option value={current}>{current}</option>}
              {fields.data.map((f) => (
                <option key={f.id} value={f.id}>
                  {f.name} ({f.id})
                </option>
              ))}
            </SelectField>
          ))}
        <div className="flex flex-wrap justify-end gap-2">
          <Button
            variant="ghost"
            disabled={update.isPending}
            onClick={() => {
              onClose(false)
            }}
          >
            Cancel
          </Button>
          <Button type="submit" pending={update.isPending} pendingLabel="Saving…">
            Save
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
