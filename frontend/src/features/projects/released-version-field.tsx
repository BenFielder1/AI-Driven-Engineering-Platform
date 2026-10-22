import type { UseFormRegisterReturn } from 'react-hook-form'

import { RadioGroup } from '../../components/ui/radio-group'
import { LoadingState } from '../../components/ui/states'
import { TextField } from '../../components/ui/text-field'
import type { useGitHubRepoRefs } from '../github/github-api'
import { GitHubErrorAlert } from '../github/github-error-alert'
import { releasedOptions, type ReleasedOption } from './released-versions'
import { NEVER_RELEASED } from './schemas'

const dateFormat = new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeZone: 'UTC' })

function describe(o: ReleasedOption): string {
  if (o.kind === 'tag') {
    return `Tag · version ${o.version}`
  }
  return [
    o.isLatest ? 'Latest GitHub release' : 'GitHub release',
    o.name && o.name !== o.tag ? o.name : null,
    o.publishedAt ? `published ${dateFormat.format(new Date(o.publishedAt))}` : null,
  ]
    .filter(Boolean)
    .join(' · ')
}

/**
 * FR-73 "Currently released version": for a GitHub App repo, its GitHub Releases and semver tags (the latest release
 * preselected by the wizard) plus "Never released"; for a URL + token or Local import, the tag typed in (empty: never
 * released), since AIDE can't list those repositories' tags before the project exists.
 */
export function ReleasedVersionField({
  id,
  listed,
  repoSelected,
  refs,
  inputProps,
  error,
}: {
  id: string
  /** The GitHub App option is chosen: list the repo's releases and tags. */
  listed: boolean
  repoSelected: boolean
  refs: ReturnType<typeof useGitHubRepoRefs>
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
  if (refs.isError) {
    return <GitHubErrorAlert error={refs.error} title="We couldn't load the repository's releases and tags" context="githubRepos" />
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
          ...options.map((o) => ({ value: o.tag, label: o.tag, description: describe(o) })),
          {
            value: NEVER_RELEASED,
            label: 'Never released',
            description: 'The code has no release yet. The version to develop becomes its first release.',
          },
        ]}
      />
      {options.length === 0 && (
        <p className="text-sm text-muted-foreground">This repository has no GitHub Releases or semantic-version tags.</p>
      )}
    </div>
  )
}
