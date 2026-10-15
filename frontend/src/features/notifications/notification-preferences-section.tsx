import { zodResolver } from '@hookform/resolvers/zod'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage } from '../../lib/errors'
import { notificationTypeLabels, notificationTypes } from '../projects/labels'
import {
  useNotificationPreferences,
  useUpdateNotificationPreferences,
  type NotificationPreferences,
} from './notifications-api'

const preferencesSchema = z.object({ shown: z.array(z.enum(notificationTypes)) })

type PreferencesValues = z.infer<typeof preferencesSchema>

const toValues = (p: NotificationPreferences): PreferencesValues => ({
  shown: notificationTypes.filter((t) => !p.mutedTypes.includes(t)),
})

function PreferencesForm({ preferences }: { preferences: NotificationPreferences }) {
  const update = useUpdateNotificationPreferences()
  const { register, handleSubmit, reset } = useForm<PreferencesValues>({
    resolver: zodResolver(preferencesSchema),
    defaultValues: toValues(preferences),
  })

  const onSubmit = handleSubmit(({ shown }) => {
    update.mutate(
      { mutedTypes: notificationTypes.filter((t) => !shown.includes(t)) },
      {
        onSuccess: (next) => {
          reset(toValues(next))
          toast.success('Notification preferences saved.')
        },
      },
    )
  })

  return (
    <form noValidate onSubmit={(e) => void onSubmit(e)}>
      <fieldset>
        <legend className="mb-2 text-sm font-medium">Show in my notification centre</legend>
        <div className="grid gap-2 sm:grid-cols-2">
          {notificationTypes.map((type) => {
            const id = `notification-pref-${type}`
            return (
              <div key={type} className="flex items-center gap-2">
                <input id={id} type="checkbox" value={type} className="size-4 accent-primary" {...register('shown')} />
                <label htmlFor={id} className="text-sm">
                  {notificationTypeLabels[type]}
                </label>
              </div>
            )
          })}
        </div>
      </fieldset>
      {update.isError && (
        <Alert tone="error" role="alert" title="We couldn't save your preferences" className="mt-3">
          <p>{errorMessage(update.error)}</p>
        </Alert>
      )}
      <Button type="submit" className="mt-3" pending={update.isPending} pendingLabel="Saving…">
        Save preferences
      </Button>
    </form>
  )
}

/**
 * Account → Notification preferences (§2, FR-47): which notification types appear in the user's in-app centre.
 * Unchecked types are muted (already created notifications are kept). Project channels are project settings.
 */
export function NotificationPreferencesSection() {
  const preferences = useNotificationPreferences()
  return (
    <section aria-labelledby="notification-prefs-title" className="rounded-xl border border-border/60 bg-card/70 shadow-sm backdrop-blur-md p-5">
      <h2 id="notification-prefs-title" className="text-lg">
        Notification preferences
      </h2>
      <p className="mb-4 mt-1 text-sm text-muted-foreground">
        Choose what appears in your in-app notifications. Email, Slack, Teams and webhook channels are set per project by
        its Owners in Settings → Notifications.
      </p>
      {preferences.isPending && <LoadingState label="Loading preferences…" />}
      {preferences.isError && <ErrorState error={preferences.error} onRetry={() => void preferences.refetch()} />}
      {preferences.isSuccess && <PreferencesForm preferences={preferences.data} />}
    </section>
  )
}
