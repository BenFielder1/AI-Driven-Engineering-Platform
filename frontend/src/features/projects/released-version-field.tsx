import type { UseFormRegisterReturn } from 'react-hook-form'

import { RadioGroup } from '../../components/ui/radio-group'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextField } from '../../components/ui/text-field'
import { GitHubErrorAlert } from '../github/github-error-alert'
import { releasedOptions, type ReleasedOption, type ReleaseRefs } from './released-versions'
import { NEVER_RELEASED } from './schemas'

const dateFormat = new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeZone: 'UTC' })

/** The host whose Releases and tags are listed (FR-73 GitHub App, FR-141 GitLab). */
export type ReleaseHost = 'github' | 'gitlab'

const hostNames: Record<ReleaseHost, string> = { github: 'GitHub', gitlab: 'GitLab' }

function describe(o: ReleasedOption, host: ReleaseHost): string {
  if (o.kind === 'tag') {
    return `Tag · version ${o.version}`
  }
  return [
    o.isLatest ? `Latest ${hostNames[host]} release` : `${hostNames[host]} release`,
    o.name && o.name !== o.tag ? o.name : null,
    o.publishedAt ? `published ${dateFormat.format(new Date(o.publishedAt))}` : null,
  ]
    .filter(Boolean)
    .join(' · ')
}

/** The refs query as the field reads it (GitHub's or GitLab's). */
interface RefsQuery {
  isPending: boolean
  isError: boolean
  error: unknown
  data: ReleaseRefs | undefined
  refetch: () => unknown
}

/**
 * FR-73, FR-141 "Currently released version": for a GitHub App or GitLab repo, its Releases and semver tags (the
 * wizard preselects one) plus "Never released"; for a URL + token or Local import, the tag typed in (empty: never
 * released), since AIDE can't list those repositories' tags before the project exists.
 */
export function ReleasedVersionField({
  id,
  listed,
  host = 'github',
  repoSelected,
  refs,
  inputProps,
  error,
}: {
  id: string
  /** The GitHub App or GitLab option is chosen: list the repo's releases and tags. */
  listed: boolean
  host?: ReleaseHost
  repoSelected: boolean
  refs: RefsQuery
  inputProps: UseFormRegisterReturn
  error?: string | undefined
}) {
  if (!listed) {
    return (
      <TextField
        id={id}
        label="Tag of the currently released version (optional)"
        hint="For example v2.3.0. Leave it empty if the repository was never released."
        autoComplete="off"
        spellCheck={false}
        maxLength={255}
        error={error}
        {...inputProps}
      />
    )
  }
  if (!repoSelected) {
    return <p className="text-sm text-muted-foreground">Pick a repository to choose its currently released version.</p>
  }
  if (refs.isPending) {
    return <LoadingState label="Loading the repository's releases and tags…" />
  }
  if (refs.isError || !refs.data) {
    return host === 'gitlab' ? (
      <ErrorState
        error={refs.error}
        title="We couldn't load the project's releases and tags"
        context="gitlabRepos"
        onRetry={() => void refs.refetch()}
      />
    ) : (
      <GitHubErrorAlert error={refs.error} title="We couldn't load the repository's releases and tags" context="githubRepos" />
    )
  }
  const options = releasedOptions(refs.data)
  return (
    <div className="flex flex-col gap-2">
      <RadioGroup
        id={id}
        legend="Currently released version"
        inputProps={inputProps}
        error={error}
        options={[
          ...options.map((o) => ({ value: o.tag, label: o.tag, description: describe(o, host) })),
          {
            value: NEVER_RELEASED,
            label: 'Never released',
            description: 'The code has no release yet. The version to develop becomes its first release.',
          },
        ]}
      />
      {options.length === 0 && (
        <p className="text-sm text-muted-foreground">
          This repository has no {hostNames[host]} Releases or semantic-version tags.
        </p>
      )}
    </div>
  )
}
