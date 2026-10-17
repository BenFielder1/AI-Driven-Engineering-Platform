import type { ReactNode } from 'react'

import { StatusBadge } from '../../components/shared/status-badge'
import { Alert } from '../../components/ui/alert'
import { Badge, type BadgeTone } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { copyForCode, errorMessage } from '../../lib/errors'
import { releaseStatusLabels } from '../projects/labels'
import type { ProjectState } from '../projects/projects-api'
import { RelativeTime } from '../projects/relative-time'
import { checkpointLabel, useReleaseVersionNames } from '../versions/version-label'
import { MergeReasons } from './merge-readiness-panel'
import { useReleases, useRetryRelease, type Release, type ReleaseStatus } from './release-api'

const statusTones: Record<ReleaseStatus, BadgeTone> = {
  in_progress: 'info',
  awaiting_merge: 'warning',
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
  const title =
    release.errorCode === 'PR_CLOSED'
      ? 'The pull request was closed'
      : release.errorCode === 'MERGE_TIMEOUT'
        ? 'The release stopped waiting for the merge'
        : 'The release failed'
  const showPr = (release.errorCode === 'PR_CLOSED' || release.errorCode === 'MERGE_TIMEOUT') && release.pullRequestUrl
  return (
    <Alert tone="error" title={title}>
      <p className="mt-1">{copyForCode(release.errorCode, 'releaseAttempt') ?? 'The release job ended with an error.'}</p>
      {release.errorMessage && <p className="mt-1 break-words">{release.errorMessage}</p>}
      {showPr && (
        <p className="mt-1">
          <a href={release.pullRequestUrl ?? undefined} target="_blank" rel="noopener noreferrer" className="text-primary underline">
            Open the pull request on GitHub
          </a>
        </p>
      )}
    </Alert>
  )
}

/**
 * FR-65, §4.14: a release attempt waiting for its PR to be merged on GitHub: a live status pill with the time it
 * started waiting, the PR link, the merge-readiness reasons recorded then and how the merge poll works.
 */
function AwaitingMerge({ release, pullRequestUrl }: { release: Release; pullRequestUrl: string | null }) {
  const manual = release.mergeStrategy === 'manual'
  return (
    <section aria-labelledby="awaiting-merge-title" className="flex flex-col gap-2 rounded-lg border border-warning/50 bg-warning/5 p-3 text-sm">
      <h3 id="awaiting-merge-title" className="sr-only">
        Awaiting merge
      </h3>
      <p role="status" className="flex flex-wrap items-center gap-2">
        <StatusBadge tone="warning" pulse>
          Waiting for merge on GitHub
        </StatusBadge>
        {release.awaitingMergeSince && (
          <span className="text-muted-foreground">
            since <RelativeTime iso={release.awaitingMergeSince} />
          </span>
        )}
      </p>
      <p>
        {manual
          ? 'This project merges manually: merge the pull request on GitHub.'
          : "AIDE couldn't merge the pull request itself. Merge it on GitHub once it's allowed."}{' '}
        AIDE then tags the merge commit and creates the GitHub Release.
      </p>
      <MergeReasons reasons={release.mergeReasons ?? []} label="Why AIDE is waiting" />
      {pullRequestUrl && (
        <p>
          <a href={pullRequestUrl} target="_blank" rel="noopener noreferrer" className="text-primary underline">
            Open the pull request on GitHub
          </a>
        </p>
      )}
      <p className="text-muted-foreground">
        AIDE checks the pull request every 2 minutes for the first hour, then every 10 minutes, for up to 14 days. If
        it's closed without merging or still unmerged after 14 days, the release fails and an Owner can retry.
      </p>
    </section>
  )
}

export function ReleaseHistory({ releases, versionOf }: { releases: Release[]; versionOf: (id: string) => string | undefined }) {
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
              {checkpointLabel(versionOf(r.releaseVersionId), r.checkpoint)}, attempt {r.attempt}
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
  const names = useReleaseVersionNames(projectId)
  const versionOf = (id: string) => names(id)
  const version = state.developmentVersion ?? state.releasedVersion

  const inReleasing = state.phase === 'release' && state.status === 'releasing'
  // The attempt at the current checkpoint of the current release version (newest first); an older attempt (e.g. before
  // a rollback) doesn't release this one.
  const current =
    releases.data?.find((r) => r.releaseVersionId === state.releaseVersionId && r.checkpoint === state.checkpoint) ?? null
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
          No release attempt is running for {checkpointLabel(version, state.checkpoint)}.
        </p>
        {retryControls}
        {releases.data.length > 0 && <ReleaseHistory releases={releases.data} versionOf={versionOf} />}
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
          {latest.status === 'succeeded' && versionOf(latest.releaseVersionId) && (
            <span className="text-sm font-semibold">{`Released ${versionOf(latest.releaseVersionId) ?? ''}`}</span>
          )}
        </div>
        {latest.status === 'awaiting_merge' && (
          <AwaitingMerge release={latest} pullRequestUrl={latest.pullRequestUrl ?? state.pullRequest?.url ?? null} />
        )}
        <dl>
          <Row term="Tag">
            <code className="font-mono">{latest.tag}</code>
          </Row>
          <Row term="Version">{checkpointLabel(versionOf(latest.releaseVersionId), latest.checkpoint)}</Row>
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
          {latest.githubReleaseUrl && (
            <Row term="GitHub Release">
              <a href={latest.githubReleaseUrl} target="_blank" rel="noopener noreferrer" className="text-primary underline">
                View the GitHub Release
              </a>
            </Row>
          )}
          {latest.pullRequestUrl && latest.status !== 'awaiting_merge' && (
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
        {releases.data.length > 1 && <ReleaseHistory releases={releases.data} versionOf={versionOf} />}
      </div>
    )
  }

  return (
    <section
      id="release"
      aria-labelledby="release-panel-title"
      className="flex scroll-mt-4 flex-col gap-3 rounded-xl border border-border/60 bg-card/70 shadow-sm backdrop-blur-md p-4"
    >
      <h2 id="release-panel-title" className="text-lg">
        Release
      </h2>
      {body}
    </section>
  )
}
