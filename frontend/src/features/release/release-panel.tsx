import type { ReactNode } from 'react'

import { Alert } from '../../components/ui/alert'
import { Badge, type BadgeTone } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { copyForCode, errorMessage } from '../../lib/errors'
import { releaseStatusLabels } from '../projects/labels'
import type { ProjectState } from '../projects/projects-api'
import { RelativeTime } from '../projects/relative-time'
import { useReleases, useRetryRelease, type Release, type ReleaseStatus } from './release-api'

const statusTones: Record<ReleaseStatus, BadgeTone> = {
  in_progress: 'info',
  succeeded: 'success',
  failed: 'danger',
}

export function ReleaseStatusBadge({ status }: { status: ReleaseStatus }) {
  return <Badge tone={statusTones[status]}>{releaseStatusLabels[status]}</Badge>
}

function Row({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="flex flex-wrap justify-between gap-x-3 py-1 text-sm">
      <dt className="text-muted-foreground">{term}</dt>
      <dd className="break-all text-right font-medium">{children}</dd>
    </div>
  )
}

function ReleaseFailure({ release, branch }: { release: Release; branch: string | null }) {
  const where = branch ? (
    <>
      on <code className="font-mono">{branch}</code>
    </>
  ) : (
    'on the project branch'
  )
  if (release.errorCode === 'MERGE_CONFLICT') {
    return (
      <Alert tone="error" title="The release hit a merge conflict">
        <p className="mt-1">The project branch conflicts with the default branch in these files:</p>
        {release.conflictingFiles.length > 0 && (
          <ul aria-label="Conflicting files" className="mt-1 list-disc pl-5">
            {release.conflictingFiles.map((f) => (
              <li key={f}>
                <code className="break-all font-mono">{f}</code>
              </li>
            ))}
          </ul>
        )}
        <p className="mt-2">Resolve the conflict {where}, then retry the release.</p>
      </Alert>
    )
  }
  return (
    <Alert tone="error" title="The release failed">
      <p className="mt-1">{copyForCode(release.errorCode, 'releaseAttempt') ?? 'The release job ended with an error.'}</p>
      {release.errorMessage && <p className="mt-1 break-words">{release.errorMessage}</p>}
    </Alert>
  )
}

function ReleaseHistory({ releases }: { releases: Release[] }) {
  return (
    <section aria-labelledby="release-history-title" className="flex flex-col gap-2">
      <h3 id="release-history-title" className="font-semibold">
        Release history
      </h3>
      <ol aria-label="Release attempts" className="flex flex-col gap-1 text-sm">
        {releases.map((r) => (
          <li key={r.id} className="flex flex-wrap items-center gap-x-3 gap-y-1">
            <ReleaseStatusBadge status={r.status} />
            <span>
              Version {r.projectVersion}, attempt {r.attempt}
            </span>
            <code className="font-mono">{r.tag}</code>
            {r.errorCode && <span className="text-muted-foreground">{r.errorCode}</span>}
            <span className="text-muted-foreground">
              <RelativeTime iso={r.startedAt} />
            </span>
          </li>
        ))}
      </ol>
    </section>
  )
}

/**
 * Overview → Release (FR-42, FR-43): the latest release attempt with its tag, merge commit and merged PR; a failed
 * attempt's reason (a merge conflict lists the files); the Owner's retry once the conflict is resolved; and every
 * attempt, newest first. Live `release` events refresh it (FR-46).
 */
export function ReleasePanel({ projectId, state, canRelease }: { projectId: string; state: ProjectState; canRelease: boolean }) {
  const releases = useReleases(projectId)
  const retry = useRetryRelease(projectId)

  const inReleasing = state.phase === 'release' && state.status === 'releasing'
  // The attempt at the current project version (newest first); an older version's attempt doesn't release this one.
  const current = releases.data?.find((r) => r.projectVersion === state.projectVersion) ?? null
  const latest = current ?? releases.data?.[0] ?? null
  // FR-43: no attempt at all while releasing (e.g. its job was lost) — the Owner may start one. Only while /state
  // reports an in-progress attempt that the list hasn't caught up with is the release "starting".
  const noAttempt = latest === null || (inReleasing && current === null)
  const starting = noAttempt && state.latestRelease?.status === 'in_progress'
  // FR-43: a failed release leaves the project in release / releasing; that's when an Owner may retry.
  const retryable = inReleasing && (noAttempt ? !starting : latest.status === 'failed')
  const branch = state.pullRequest?.headBranch ?? null

  const retryControls = (
    <>
      {retryable &&
        (canRelease ? (
          <div>
            <Button
              pending={retry.isPending}
              pendingLabel="Retrying…"
              onClick={() => {
                retry.mutate()
              }}
            >
              Retry release
            </Button>
          </div>
        ) : (
          <p className="text-sm text-muted-foreground">An Owner can retry the release once the problem is fixed.</p>
        ))}
      <div role="alert" aria-live="assertive">
        {retry.isError && <Alert tone="error" title={errorMessage(retry.error, 'retryRelease')} />}
      </div>
    </>
  )

  let body: ReactNode
  if (releases.isPending) {
    body = <LoadingState label="Loading releases…" />
  } else if (releases.isError) {
    body = <ErrorState error={releases.error} title="The releases couldn't load" onRetry={() => void releases.refetch()} />
  } else if (starting) {
    body = (
      <p role="status" aria-busy="true" className="text-sm">
        The release is starting…
      </p>
    )
  } else if (noAttempt) {
    body = (
      <div className="flex flex-col gap-3">
        <p role="status" className="text-sm">
          No release attempt is running for version {state.projectVersion}.
        </p>
        {retryControls}
        {releases.data.length > 0 && <ReleaseHistory releases={releases.data} />}
      </div>
    )
  } else {
    body = (
      <div className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center gap-2">
          <ReleaseStatusBadge status={latest.status} />
          {latest.status === 'in_progress' && (
            <span role="status" className="text-sm">
              Merging and tagging…
            </span>
          )}
        </div>
        <dl>
          <Row term="Tag">
            <code className="font-mono">{latest.tag}</code>
          </Row>
          <Row term="Project version">{latest.projectVersion}</Row>
          <Row term="Attempt">{latest.attempt}</Row>
          <Row term="Merge commit">
            {latest.mergeCommitSha ? (
              <code className="font-mono" title={latest.mergeCommitSha}>
                {latest.mergeCommitSha.slice(0, 7)}
              </code>
            ) : (
              '—'
            )}
          </Row>
          {latest.pullRequestUrl && (
            <Row term="Pull request">
              <a href={latest.pullRequestUrl} target="_blank" rel="noopener noreferrer" className="text-primary underline">
                View on GitHub
              </a>
            </Row>
          )}
          <Row term="Started">
            <RelativeTime iso={latest.startedAt} />
          </Row>
          {latest.completedAt && (
            <Row term="Finished">
              <RelativeTime iso={latest.completedAt} />
            </Row>
          )}
        </dl>
        {latest.status === 'failed' && <ReleaseFailure release={latest} branch={branch} />}
        {retryControls}
        {releases.data.length > 1 && <ReleaseHistory releases={releases.data} />}
      </div>
    )
  }

  return (
    <section
      id="release"
      aria-labelledby="release-title"
      className="flex scroll-mt-4 flex-col gap-3 rounded-xl border border-border/60 bg-card/70 shadow-sm backdrop-blur-md p-4"
    >
      <h2 id="release-title" className="text-lg">
        Release
      </h2>
      {body}
    </section>
  )
}
