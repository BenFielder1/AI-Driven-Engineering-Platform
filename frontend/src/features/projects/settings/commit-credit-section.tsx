import { useId, useState } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../../components/ui/alert'
import { bitbucketCommitEmailWarning } from '../../../lib/git-host'
import { errorMessage } from '../../../lib/errors'
import { useUpdateGitSettings, type GitSettings } from '../projects-api'

/** FR-177: "Credit people on commits" on Settings → Git. Owner only (the settings are), and it applies to later commits. */
export function CommitCreditSection({ projectId, git }: { projectId: string; git: GitSettings }) {
  const update = useUpdateGitSettings(projectId)
  const [failure, setFailure] = useState<unknown>(null)
  const id = useId()
  const hintId = `${id}-hint`
  const enabled = git.commitCreditEnabled ?? true

  return (
    <section aria-labelledby={`${id}-title`} className="rounded-lg border border-border/60 bg-background p-4">
      <h3 id={`${id}-title`} className="font-semibold">
        Commit credit
      </h3>
      <div className="mt-2 flex items-start gap-2">
        <input
          id={id}
          type="checkbox"
          className="mt-0.5 size-4 accent-primary"
          checked={enabled}
          disabled={update.isPending}
          aria-describedby={hintId}
          onChange={(event) => {
            setFailure(null)
            update.mutate(
              { commitCreditEnabled: event.target.checked },
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
            Credit people on commits
          </label>
          <div id={hintId} className="text-sm text-muted-foreground">
            <p>Adds the accountable person as a co-author. It applies to later commits and never rewrites history.</p>
            {git.mode === 'bitbucket' && <p className="mt-1">{bitbucketCommitEmailWarning}</p>}
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
