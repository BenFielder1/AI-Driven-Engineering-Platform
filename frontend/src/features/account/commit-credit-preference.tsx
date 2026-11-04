import { useId, useState } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { errorMessage } from '../../lib/errors'
import { bitbucketCommitEmailWarning } from '../../lib/git-host'
import { useUpdateMe, type User } from '../auth/auth-api'

/** FR-177: "Credit me on commits made by Tevrin" (default on). Turning it off skips this person in commit credit. */
export function CommitCreditPreference({ user }: { user: User }) {
  const update = useUpdateMe()
  const [failure, setFailure] = useState<unknown>(null)
  const id = useId()
  const hintId = `${id}-hint`

  return (
    <section aria-labelledby={`${id}-title`} className="rounded-xl border border-border/60 bg-card/70 shadow-sm backdrop-blur-md p-5">
      <h2 id={`${id}-title`} className="mb-2 text-lg">
        Commit credit
      </h2>
      <div className="flex items-start gap-2">
        <input
          id={id}
          type="checkbox"
          className="mt-0.5 size-4 accent-primary"
          checked={!(user.commitCreditOptOut ?? false)}
          disabled={update.isPending}
          aria-describedby={hintId}
          onChange={(event) => {
            setFailure(null)
            update.mutate(
              { commitCreditOptOut: !event.target.checked },
              {
                onSuccess: () => {
                  toast.success('Commit credit saved.')
                },
                onError: setFailure,
              },
            )
          }}
        />
        <div>
          <label htmlFor={id} className="text-sm">
            Credit me on commits made by Tevrin
          </label>
          <div id={hintId} className="text-sm text-muted-foreground">
            <p>Tevrin adds you as a co-author using your Git host account, never your Tevrin sign-in email.</p>
            <p className="mt-1">{bitbucketCommitEmailWarning}</p>
          </div>
        </div>
      </div>
      {failure !== null && (
        <Alert tone="error" role="alert" title="Commit credit wasn't saved" className="mt-3">
          <p>{errorMessage(failure)}</p>
        </Alert>
      )}
    </section>
  )
}
