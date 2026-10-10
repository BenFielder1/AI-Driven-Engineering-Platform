import { zodResolver } from '@hookform/resolvers/zod'
import { useRef, useState } from 'react'
import { useFieldArray, useForm, useWatch, type FieldErrors, type UseFormSetError } from 'react-hook-form'
import { z } from 'zod'

import type { Schemas } from '../../../api/client'
import { Alert, ErrorSummary, type SummaryError } from '../../../components/ui/alert'
import { Button } from '../../../components/ui/button'
import { SelectField } from '../../../components/ui/select-field'
import { EmptyState } from '../../../components/ui/states'
import { TextField } from '../../../components/ui/text-field'
import { ApiError, errorMessage, isApiError } from '../../../lib/errors'
import { channelTypeLabels, channelTypes, notificationEventLabels, notificationEvents } from '../labels'
import { useUpdateNotificationSettings, type NotificationSettings } from '../projects-api'
import { limits } from '../schemas'
import { SecretField, type SecretAction } from './secret-field'

const MAX_CHANNELS = 20

type ChannelType = (typeof channelTypes)[number]

/** Slack and Teams incoming-webhook URLs are bearer credentials, so they're sent and stored as the channel secret. */
const urlIsSecret = (type: ChannelType) => type === 'slack' || type === 'teams'

const HTTPS_URL = /^https:\/\/\S+$/

const channelSchema = z
  .object({
    id: z.string(),
    type: z.enum(channelTypes),
    /** The type the channel was saved with (`null` for a new channel); a saved secret is kept only for the same type. */
    savedType: z.enum(channelTypes).nullable(),
    /** Email address or webhook URL. Unused for Slack/Teams (the URL is the secret). */
    target: z.string(),
    /** The API's display target: for Slack/Teams a masked value such as `hooks.slack.com/…`. */
    savedTarget: z.string(),
    hasSecret: z.boolean(),
    secretAction: z.enum(['keep', 'replace', 'clear']),
    secret: z.string(),
    events: z.array(z.enum(notificationEvents)),
  })
  .superRefine((c, ctx) => {
    const add = (path: string, message: string) => {
      ctx.addIssue({ code: 'custom', path: [path], message })
    }
    const entering = !secretIsSet(c) || c.secretAction === 'replace'
    if (urlIsSecret(c.type)) {
      const label = channelTypeLabels[c.type]
      const url = c.secret.trim()
      if (entering && !url) {
        add('secret', `Enter the ${label} incoming-webhook URL.`)
      } else if (entering && !HTTPS_URL.test(url)) {
        add('secret', `Use the https:// incoming-webhook URL from ${label}.`)
      } else if (url.length > limits.urlMax) {
        add('secret', `The URL is at most ${String(limits.urlMax)} characters.`)
      }
    } else {
      const target = c.target.trim()
      if (!target) {
        add('target', c.type === 'email' ? 'Enter an email address.' : 'Enter the webhook URL.')
      } else if (c.type === 'email' && !z.email().safeParse(target).success) {
        add('target', 'Enter a valid email address.')
      } else if (c.type === 'webhook' && !HTTPS_URL.test(target)) {
        add('target', 'Enter an https:// URL.')
      } else if (target.length > limits.urlMax) {
        add('target', `The target is at most ${String(limits.urlMax)} characters.`)
      }
      // FR-47, NFR-SEC: webhook bodies are always HMAC-signed, so a key is required unless one is saved and kept.
      if (c.type === 'webhook' && entering && !c.secret.trim()) {
        add('secret', 'Enter a signing key: webhook bodies are always HMAC-signed.')
      } else if (c.type === 'webhook' && c.secret.length > limits.secretMax) {
        add('secret', `The signing key is at most ${String(limits.secretMax)} characters.`)
      }
    }
    if (c.events.length === 0) {
      add('events', 'Choose at least one event.')
    }
  })

const notificationsSchema = z.object({ channels: z.array(channelSchema).max(MAX_CHANNELS) })

type NotificationValues = z.infer<typeof notificationsSchema>
type ChannelValues = NotificationValues['channels'][number]

/** The API keeps a saved secret only for the same channel id and type. */
function secretIsSet(c: Pick<ChannelValues, 'hasSecret' | 'type' | 'savedType'>) {
  return c.hasSecret && c.type === c.savedType
}

const toValues = (s: NotificationSettings): NotificationValues => ({
  channels: s.channels.map((c) => ({
    id: c.id,
    type: c.type,
    savedType: c.type,
    target: urlIsSecret(c.type) ? '' : c.target,
    savedTarget: c.target,
    hasSecret: c.hasSecret,
    secretAction: 'keep',
    secret: '',
    events: [...c.events],
  })),
})

/**
 * Builds the API input. Email: target only. Webhook: target plus the required HMAC key (FR-47); omitting it keeps the
 * saved key (same id and type). Slack/Teams: the URL goes in `secret`; omitting it keeps the saved URL (same id and
 * type). Neither sends `clearSecret`: a webhook, Slack or Teams channel always has a secret.
 */
function toInput(c: ChannelValues): Schemas['NotificationChannelInput'] {
  const input: Schemas['NotificationChannelInput'] = { id: c.id || null, type: c.type, events: c.events }
  const entered = c.secretAction === 'replace' || !secretIsSet(c)
  if (urlIsSecret(c.type)) {
    if (entered) {
      input.secret = c.secret.trim()
    }
    return input
  }
  input.target = c.target.trim()
  if (c.type === 'webhook' && entered) {
    input.secret = c.secret
  }
  return input
}

// eslint-disable-next-line @typescript-eslint/restrict-template-expressions -- RHF paths are typed `channels.${number}.…`
const channelPath = <K extends keyof ChannelValues>(i: number, key: K) => `channels.${i}.${key}` as const

const targetId = (i: number) => `channel-${String(i)}-target`
const secretId = (i: number) => `channel-${String(i)}-secret`
const eventsId = (i: number) => `channel-${String(i)}-event-${notificationEvents[0]}`

function summaryErrors(errors: FieldErrors<NotificationValues>): SummaryError[] {
  const list: SummaryError[] = []
  errors.channels?.forEach?.((c, i) => {
    if (c?.target?.message) {
      list.push({ fieldId: targetId(i), message: `Channel ${String(i + 1)}: ${c.target.message}` })
    }
    if (c?.secret?.message) {
      list.push({ fieldId: secretId(i), message: `Channel ${String(i + 1)}: ${c.secret.message}` })
    }
    if (c?.events?.message) {
      list.push({ fieldId: eventsId(i), message: `Channel ${String(i + 1)}: ${c.events.message}` })
    }
  })
  return list
}

const channelFields = ['target', 'secret', 'events'] as const satisfies readonly (keyof ChannelValues)[]

/**
 * Maps VALIDATION keys such as `channels[0].secret` onto the channel's fields; returns the messages that don't
 * belong to a field (shown in the summary).
 */
function applyChannelErrors(error: unknown, setError: UseFormSetError<NotificationValues>, count: number): string[] {
  if (!(error instanceof ApiError)) {
    return []
  }
  const unmatched: string[] = []
  for (const [key, messages] of Object.entries(error.fieldErrors)) {
    const message = messages.join(' ')
    const m = /^channels\[(\d+)\]\.(\w+)$/i.exec(key)
    const index = m ? Number(m[1]) : -1
    const field = channelFields.find((f) => f === m?.[2]?.toLowerCase())
    if (field && index >= 0 && index < count) {
      setError(channelPath(index, field), { type: 'server', message })
    } else if (message) {
      unmatched.push(m ? `Channel ${String(index + 1)}: ${message}` : message)
    }
  }
  return unmatched
}

/** Settings → Notifications (FR-9, FR-47): channels with event subscriptions; saving replaces the whole list. */
export function NotificationsTab({ projectId, notifications }: { projectId: string; notifications: NotificationSettings }) {
  const update = useUpdateNotificationSettings(projectId)
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const [saved, setSaved] = useState(false)
  const addRef = useRef<HTMLButtonElement>(null)
  const {
    register,
    control,
    handleSubmit,
    setValue,
    setError,
    reset,
    formState: { errors, isSubmitted },
  } = useForm<NotificationValues>({
    resolver: zodResolver(notificationsSchema),
    defaultValues: toValues(notifications),
  })
  const { fields, append, remove } = useFieldArray({ control, name: 'channels' })
  const channels = useWatch({ control, name: 'channels' })

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    setSaved(false)
    update.mutate(
      { channels: v.channels.map(toInput) },
      {
        onSuccess: (next) => {
          reset(toValues(next))
          setSaved(true)
        },
        onError: (error) => {
          const unmatched = applyChannelErrors(error, setError, v.channels.length)
          setServerErrors(unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error)])
        },
      },
    )
  })

  return (
    <form noValidate onSubmit={(e) => void onSubmit(e)} aria-labelledby="notifications-title" className="max-w-3xl">
      <h2 id="notifications-title" className="mb-1 text-xl font-semibold">
        Notifications
      </h2>
      <p className="mb-4 text-sm text-muted">
        Send this project&apos;s events to email, Slack, Microsoft Teams or a generic webhook. Webhook bodies are always
        signed with HMAC using the channel&apos;s signing key. Failed deliveries are retried per channel. Your own
        in-app notifications are set on your Account page.
      </p>
      <ErrorSummary
        title="We couldn't save the notification settings"
        errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? summaryErrors(errors) : [])]}
      />
      <div role="status" aria-live="polite">
        {saved && (
          <Alert tone="success" className="mb-4">
            Notification settings saved.
          </Alert>
        )}
      </div>

      {fields.length === 0 && (
        <EmptyState title="No channels yet">Add a channel to get notified about this project.</EmptyState>
      )}

      <ul aria-label="Notification channels" className="flex flex-col gap-4">
        {fields.map((field, i) => {
          const channel = channels[i]
          const chErrors = errors.channels?.[i]
          const n = String(i + 1)
          const type = channel?.type ?? field.type
          const typeField = register(channelPath(i, 'type'))
          const secretField = register(channelPath(i, 'secret'))
          const setSecretAction = (a: SecretAction) => {
            setValue(channelPath(i, 'secretAction'), a)
            setValue(channelPath(i, 'secret'), '')
          }
          const isSet = channel ? secretIsSet(channel) : false
          return (
            <li key={field.id}>
              <fieldset className="flex flex-col gap-4 rounded-lg border border-border p-4">
                <legend className="px-1 font-semibold">Channel {n}</legend>
                <div className="grid gap-4 sm:grid-cols-3">
                  <SelectField
                    label={`Channel ${n} type`}
                    {...typeField}
                    onChange={(e) => {
                      // A different type never keeps the saved secret, so start the secret over.
                      setSecretAction('keep')
                      void typeField.onChange(e)
                    }}
                  >
                    {channelTypes.map((t) => (
                      <option key={t} value={t}>
                        {channelTypeLabels[t]}
                      </option>
                    ))}
                  </SelectField>
                  {!urlIsSecret(type) && (
                    <TextField
                      id={targetId(i)}
                      label={type === 'email' ? `Channel ${n} email address` : `Channel ${n} webhook URL`}
                      type={type === 'email' ? 'email' : 'url'}
                      autoComplete="off"
                      className="sm:col-span-2"
                      error={chErrors?.target?.message}
                      {...register(channelPath(i, 'target'))}
                    />
                  )}
                </div>
                {urlIsSecret(type) && (
                  <SecretField
                    id={secretId(i)}
                    label={`Channel ${n} ${channelTypeLabels[type]} webhook URL`}
                    nounPhrase={`channel ${n} ${channelTypeLabels[type]} webhook URL`}
                    isSet={isSet}
                    setDetail={isSet ? channel?.savedTarget : undefined}
                    action={channel?.secretAction ?? 'keep'}
                    onActionChange={setSecretAction}
                    allowClear={false}
                    required
                    hint={`The incoming-webhook URL from ${channelTypeLabels[type]}. It works like a password, so it's stored encrypted and only its host is shown again.`}
                    maxLength={limits.urlMax}
                    error={chErrors?.secret?.message}
                    inputProps={secretField}
                  />
                )}
                {type === 'webhook' && (
                  <SecretField
                    id={secretId(i)}
                    label={`Channel ${n} signing key`}
                    isSet={isSet}
                    action={channel?.secretAction ?? 'keep'}
                    onActionChange={setSecretAction}
                    allowClear={false}
                    required
                    hint="HMAC key used to sign each webhook body so the receiver can verify it came from AIDE. Stored encrypted; it's never shown again."
                    maxLength={limits.secretMax}
                    error={chErrors?.secret?.message}
                    inputProps={secretField}
                  />
                )}
                <fieldset
                  aria-describedby={chErrors?.events?.message ? `channel-${String(i)}-events-error` : undefined}
                >
                  <legend className="mb-2 text-sm font-medium">Channel {n} events</legend>
                  <div className="grid gap-2 sm:grid-cols-2">
                    {notificationEvents.map((ev) => {
                      const id = `channel-${String(i)}-event-${ev}`
                      return (
                        <div key={ev} className="flex items-center gap-2">
                          <input id={id} type="checkbox" value={ev} className="size-4" {...register(channelPath(i, 'events'))} />
                          <label htmlFor={id} className="text-sm">
                            {notificationEventLabels[ev]}
                          </label>
                        </div>
                      )
                    })}
                  </div>
                  {chErrors?.events?.message && (
                    <p id={`channel-${String(i)}-events-error`} className="mt-1 text-sm text-danger">
                      {chErrors.events.message}
                    </p>
                  )}
                </fieldset>
                <div>
                  <Button
                    variant="secondary"
                    onClick={() => {
                      remove(i)
                      addRef.current?.focus()
                    }}
                  >
                    Remove channel {n}
                  </Button>
                </div>
              </fieldset>
            </li>
          )
        })}
      </ul>

      <div className="mt-5 flex flex-wrap gap-2">
        <Button
          ref={addRef}
          variant="secondary"
          disabled={fields.length >= MAX_CHANNELS}
          onClick={() => {
            append({
              id: '',
              type: 'email',
              savedType: null,
              target: '',
              savedTarget: '',
              hasSecret: false,
              secretAction: 'keep',
              secret: '',
              events: ['gate_required'],
            })
          }}
        >
          Add channel
        </Button>
        <Button type="submit" pending={update.isPending} pendingLabel="Saving…">
          Save notifications
        </Button>
      </div>
    </form>
  )
}
