import type { ProjectState } from '../projects/projects-api'
import { StageSection } from '../stages/stage-section'
import { useReleases } from './release-api'

/**
 * Release page → GitHub Release (FR-64): the GitHub Release created for the version's tag on the merge commit. A Local
 * project has none (the tag is in the AIDE-managed repository).
 */
export function GitHubReleaseSection({ projectId, state }: { projectId: string; state: ProjectState }) {
  const releases = useReleases(projectId, state.phase === 'release' || state.latestRelease !== null)
  const released = releases.data?.find((r) => r.status === 'succeeded') ?? null
  const local = state.pullRequest?.provider === 'local'

  let body
  if (released?.githubReleaseUrl) {
    body = (
      <p className="text-sm">
        <a href={released.githubReleaseUrl} target="_blank" rel="noopener noreferrer" className="text-primary underline">
          {`View the GitHub Release for ${released.tag}`}
        </a>
      </p>
    )
  } else if (released) {
    body = (
      <p className="text-sm text-muted-foreground">
        {local
          ? `Local projects have no GitHub Release. The tag ${released.tag} is in the AIDE-managed repository.`
          : `No GitHub Release is recorded for ${released.tag}.`}
      </p>
    )
  } else {
    body = (
      <p className="text-sm text-muted-foreground">
        {local
          ? 'Local projects have no GitHub Release; the release tags the merge commit in the AIDE-managed repository.'
          : 'Once the pull request is merged and tagged, AIDE creates a GitHub Release with generated notes and links it here.'}
      </p>
    )
  }
  return (
    <StageSection id="release-github-release" title="GitHub Release">
      {body}
    </StageSection>
  )
}
