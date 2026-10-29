import { ErrorState, LoadingState } from '../../components/ui/states'
import { gitHostTerms } from '../../lib/git-host'
import { MarkdownView } from '../artifacts/markdown-view'
import type { ProjectState } from '../projects/projects-api'
import { StageSection } from '../stages/stage-section'
import { useReleaseNotes, useReleases, type Release } from './release-api'

/**
 * Release page → the host release (FR-64, FR-140): the GitHub or GitLab Release created for the version's tag on the
 * merge commit (the API records either in `githubReleaseUrl`). A Local project has none (the tag is in the AIDE-managed
 * repository); Bitbucket has no Releases, so the annotated tag holds the notes (D-V4-7): `githubReleaseUrl` is the tag's
 * page, and the release's full notes (GET …/releases/:releaseId/notes) are shown here (Markdown, rendered safely: no raw
 * HTML, no unsafe URLs).
 */
export function GitHubReleaseSection({ projectId, state }: { projectId: string; state: ProjectState }) {
  const releases = useReleases(projectId, state.phase === 'release' || state.latestRelease !== null)
  const released = releases.data?.find((r) => r.status === 'succeeded') ?? null
  const provider = state.pullRequest?.provider
  const local = provider === 'local'
  const host = gitHostTerms(provider)
  const title = host.release ?? 'Release tag'

  let body
  if (released?.githubReleaseUrl) {
    body = (
      <p className="text-sm">
        <a href={released.githubReleaseUrl} target="_blank" rel="noopener noreferrer" className="text-primary underline">
          {host.release ? `View the ${host.release} for ${released.tag}` : `Release tag ${released.tag}`}
        </a>
        {!host.release && <span className="text-muted-foreground">{` on ${host.host}: an annotated tag holding the release notes.`}</span>}
      </p>
    )
  } else if (released) {
    body = (
      <p className="text-sm text-muted-foreground">
        {local
          ? `Local projects have no GitHub Release. The tag ${released.tag} is in the AIDE-managed repository.`
          : host.release
            ? `No ${host.release} is recorded for ${released.tag}.`
            : `${host.host} has no Releases. The annotated tag ${released.tag} holds the release notes.`}
      </p>
    )
  } else {
    body = (
      <p className="text-sm text-muted-foreground">
        {local
          ? 'Local projects have no GitHub Release; the release tags the merge commit in the AIDE-managed repository.'
          : host.release
            ? `Once the ${host.pr} is merged and tagged, AIDE creates a ${host.release} with generated notes and links it here.`
            : `Once the ${host.pr} is merged, AIDE tags the merge commit with an annotated tag holding the release notes.`}
      </p>
    )
  }
  return (
    <StageSection id="release-github-release" title={title}>
      {body}
      {/* Only Bitbucket keeps AIDE's notes (the tag's message); the host Release holds them elsewhere. */}
      {released && provider === 'bitbucket' && <ReleaseNotes projectId={projectId} release={released} />}
    </StageSection>
  )
}

/** FR-140, D-V4-7: the succeeded release's full notes, fetched for the release shown. */
function ReleaseNotes({ projectId, release }: { projectId: string; release: Release }) {
  const notes = useReleaseNotes(projectId, release.id)
  if (notes.isPending) {
    return <LoadingState label="Loading the release notes…" />
  }
  if (notes.isError) {
    return (
      <ErrorState error={notes.error} title="The release notes couldn't load" onRetry={() => void notes.refetch()} />
    )
  }
  const text = notes.data.notes?.trim() ? notes.data.notes : null
  if (!text) {
    return null
  }
  return (
    <div className="mt-4" data-testid="release-notes">
      <h4 className="mb-2 font-semibold">Release notes</h4>
      <MarkdownView content={text} label={`Release notes for ${release.tag}`} />
    </div>
  )
}
