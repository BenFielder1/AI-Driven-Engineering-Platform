import { gitHostTerms } from '../../lib/git-host'
import type { ProjectState } from '../projects/projects-api'
import { StageSection } from '../stages/stage-section'
import { useReleases } from './release-api'

/**
 * Release page → the host release (FR-64, FR-140): the GitHub or GitLab Release created for the version's tag on the
 * merge commit (the API records either in `githubReleaseUrl`). A Local project has none (the tag is in the AIDE-managed
 * repository); Bitbucket has no Releases, so the annotated tag holds the notes (D-V4-7).
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
          {`View the ${title} for ${released.tag}`}
        </a>
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
    </StageSection>
  )
}
