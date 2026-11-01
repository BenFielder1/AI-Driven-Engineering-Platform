import { zodResolver } from '@hookform/resolvers/zod'
import { Globe, Lock } from 'lucide-react'
import { useEffect, useId, useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { z } from 'zod'

import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { RadioGroup } from '../../components/ui/radio-group'
import { SelectField } from '../../components/ui/select-field'
import { LoadingState } from '../../components/ui/states'
import { TextAreaField } from '../../components/ui/textarea-field'
import { TextField } from '../../components/ui/text-field'
import { errorMessage, isApiError } from '../../lib/errors'
import { cn } from '../../lib/utils'
import { useCreateGitLabRepo, useGitLabOwners, useGitLabRepos, type GitLabRepo } from './gitlab-api'
import { GitLabErrorAlert } from './gitlab-link'

/** Backend GitLabRules limits (name 255, description 2000) and GitLab's path rules. */
const NAME_MAX = 255
const DESCRIPTION_MAX = 2000
/** Backend GitLabRules.MaxSearchLength. */
const SEARCH_MAX = 100
const PATH = /^[A-Za-z0-9_][A-Za-z0-9_.-]*$/

/** GitLab's slug of a project name (lower case, other characters as `-`), as the path suggestion. */
export function gitlabSlug(name: string): string {
  return name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9_.-]+/g, '-')
    .replace(/^[-.]+|[-.]+$/g, '')
}

const createSchema = z.object({
  owner: z.string().min(1, { error: 'Choose where to create the project.' }),
  name: z
    .string()
    .trim()
    .min(1, { error: 'Enter a project name, such as "Todo app".' })
    .max(NAME_MAX, { error: `Names are at most ${String(NAME_MAX)} characters.` }),
  path: z
    .string()
    .trim()
    .max(NAME_MAX, { error: `Paths are at most ${String(NAME_MAX)} characters.` })
    .refine((v) => v === '' || (PATH.test(v) && !v.endsWith('.git') && !v.endsWith('.atom')), {
      error: 'Use letters, digits, underscores, dots or hyphens, starting with a letter, digit or underscore.',
    }),
  description: z.string().max(DESCRIPTION_MAX, { error: `Descriptions are at most ${String(DESCRIPTION_MAX)} characters.` }),
  visibility: z.enum(['private', 'public']),
})

type CreateValues = z.infer<typeof createSchema>
const createIds = {
  owner: 'gitlab-repo-owner',
  name: 'gitlab-repo-name',
  path: 'gitlab-repo-path',
  description: 'gitlab-repo-description',
} as const
const blank: CreateValues = { owner: '', name: '', path: '', description: '', visibility: 'private' }

/**
 * FR-132 "Create project" on GitLab: namespace (the personal namespace, or a group whose project_creation_level allows
 * the user), name, path, description and visibility. GitLab initialises it with a README so `main` exists; on success
 * the caller selects it.
 */
export function CreateGitLabRepoDialog({
  open,
  connectionId,
  onClose,
  onCreated,
}: {
  open: boolean
  connectionId: string
  onClose: () => void
  onCreated: (repo: GitLabRepo) => void
}) {
  const owners = useGitLabOwners(connectionId, open)
  const create = useCreateGitLabRepo()
  const [failure, setFailure] = useState<unknown>(null)
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    getValues,
    reset,
    control,
    formState: { errors, isSubmitted },
  } = useForm<CreateValues>({ resolver: zodResolver(createSchema), defaultValues: blank })
  const slug = gitlabSlug(useWatch({ control, name: 'name' }))

  const creatable = (owners.data ?? []).filter((o) => o.canCreateProjects)
  const blocked = (owners.data ?? []).filter((o) => !o.canCreateProjects)
  const firstCreatable = creatable[0]?.fullPath ?? ''

  useEffect(() => {
    if (firstCreatable && !getValues('owner')) {
      setValue('owner', firstCreatable)
    }
  }, [firstCreatable, getValues, setValue])

  const close = () => {
    reset({ ...blank, owner: firstCreatable })
    setFailure(null)
    create.reset()
    onClose()
  }

  const onSubmit = handleSubmit((v) => {
    setFailure(null)
    const description = v.description.trim()
    create.mutate(
      {
        connectionId,
        owner: v.owner,
        name: v.name.trim(),
        private: v.visibility === 'private',
        ...(v.path ? { path: v.path } : {}),
        ...(description ? { description } : {}),
      },
      {
        onSuccess: (repo) => {
          reset({ ...blank, owner: firstCreatable })
          create.reset()
          onCreated(repo)
        },
        onError: (error) => {
          if (isApiError(error, 'REPO_NAME_TAKEN')) {
            setError('name', { type: 'server', message: errorMessage(error, 'createGitLabRepo') }, { shouldFocus: true })
          } else if (isApiError(error, 'VALIDATION')) {
            for (const [key, messages] of Object.entries(error.fieldErrors)) {
              const field = (['owner', 'name', 'path', 'description'] as const).find((f) => f === key.toLowerCase())
              setError(field ?? 'name', { type: 'server', message: messages.join(' ') })
            }
          } else {
            setFailure(error)
          }
        },
      },
    )
  })

  let body
  if (owners.isPending) {
    body = <LoadingState label="Loading where you can create projects…" />
  } else if (owners.isError) {
    body = <GitLabErrorAlert error={owners.error} title="We couldn't load your namespaces" context="gitlabRepos" />
  } else {
    body = (
      <form
        noValidate
        aria-label="New GitLab project"
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          // The dialog is portalled, but React still bubbles submit to the wizard's form: keep it here.
          e.stopPropagation()
          void onSubmit(e)
        }}
      >
        <ErrorSummary title="Check the project" errors={isSubmitted ? fieldErrorSummary(errors, createIds) : []} />
        {failure !== null && (
          <GitLabErrorAlert error={failure} title="The project wasn't created" context="createGitLabRepo" />
        )}
        {creatable.length === 0 && (
          <Alert tone="warning">
            <p>You can&apos;t create projects in any namespace with this connection.</p>
          </Alert>
        )}
        <SelectField
          id={createIds.owner}
          label="Namespace"
          error={errors.owner?.message}
          disabled={creatable.length === 0}
          {...register('owner')}
        >
          {creatable.length === 0 && <option value="">No namespace available</option>}
          {owners.data.map((o) => (
            <option key={o.id} value={o.fullPath} disabled={!o.canCreateProjects}>
              {o.fullPath}
              {o.kind === 'user' ? ' (personal)' : ' (group)'}
              {o.canCreateProjects ? '' : ' (not allowed)'}
            </option>
          ))}
        </SelectField>
        {blocked.length > 0 && (
          <ul aria-label="Namespaces you can't use" className="-mt-2 flex flex-col gap-1 text-sm text-muted-foreground">
            {blocked.map((o) => (
              <li key={o.id}>
                {o.fullPath}: {o.reason ?? 'GitLab doesn’t let you create projects in this group.'}
              </li>
            ))}
          </ul>
        )}
        <TextField
          id={createIds.name}
          label="Project name"
          autoComplete="off"
          maxLength={NAME_MAX}
          error={errors.name?.message}
          {...register('name')}
        />
        <TextField
          id={createIds.path}
          label="Path (optional)"
          hint={`The project's URL path. Leave empty to use GitLab's slug of the name${slug ? ` (${slug})` : ''}.`}
          autoComplete="off"
          spellCheck={false}
          maxLength={NAME_MAX}
          error={errors.path?.message}
          {...register('path')}
        />
        <TextAreaField
          id={createIds.description}
          label="Description (optional)"
          rows={2}
          maxLength={DESCRIPTION_MAX}
          error={errors.description?.message}
          {...register('description')}
        />
        <RadioGroup
          legend="Visibility"
          inputProps={register('visibility')}
          options={[
            { value: 'private', label: 'Private', description: 'Only members you add can see it.' },
            { value: 'public', label: 'Public', description: 'Anyone can see it.' },
          ]}
        />
        <p className="text-sm text-muted-foreground">GitLab creates it with a README, so the main branch exists.</p>
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" onClick={close} disabled={create.isPending}>
            Cancel
          </Button>
          <Button type="submit" pending={create.isPending} pendingLabel="Creating…" disabled={creatable.length === 0}>
            Create project
          </Button>
        </div>
      </form>
    )
  }

  return (
    <ModalDialog open={open} title="Create GitLab project" busy={create.isPending} onClose={close} className="sm:max-w-lg">
      {body}
    </ModalDialog>
  )
}

/** Debounces `value` by `ms` (search-as-you-type without a request per keystroke). */
function useDebounced(value: string, ms = 300): string {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const t = setTimeout(() => {
      setDebounced(value)
    }, ms)
    return () => {
      clearTimeout(t)
    }
  }, [value, ms])
  return debounced
}

/**
 * FR-132: the GitLab projects the connection's user can develop in (Developer or above), searchable and paged with
 * GitLab's keyset cursor, with "Create project" (which selects the new project).
 */
export function GitLabRepoPicker({
  connectionId,
  searchId,
  selected,
  error,
  onSelect,
  allowCreate = true,
}: {
  connectionId: string
  /** id of the search input (the error-summary link target). */
  searchId: string
  /** The selected project's full path. */
  selected: string
  error?: string | undefined
  onSelect: (repo: GitLabRepo) => void
  /** Offer "Create GitLab project" (off while the result can't be used, so no orphan projects are created). */
  allowCreate?: boolean
}) {
  const [search, setSearch] = useState('')
  const term = useDebounced(search.trim())
  const repos = useGitLabRepos(connectionId, term)
  const [creating, setCreating] = useState(false)
  const name = useId()
  const errorId = error ? `${searchId}-error` : undefined
  const items = repos.data?.pages.flatMap((p) => p.items) ?? []

  let list
  if (repos.isPending) {
    list = <LoadingState label="Loading your GitLab projects…" />
  } else if (repos.isError) {
    list = <GitLabErrorAlert error={repos.error} title="We couldn't load your GitLab projects" context="gitlabRepos" />
  } else if (items.length === 0) {
    list = (
      <p className="rounded-lg border border-dashed border-border p-4 text-sm text-muted-foreground">
        {term ? `No projects match "${term}".` : 'You aren’t a Developer (or above) on any GitLab project yet. Create one.'}
      </p>
    )
  } else {
    list = (
      <fieldset aria-describedby={errorId} className="flex flex-col gap-2">
        <legend className="sr-only">GitLab projects</legend>
        <ul aria-label="GitLab projects" className="flex max-h-80 flex-col gap-2 overflow-y-auto">
          {items.map((r) => {
            const inputId = `${name}-${r.id}`
            const isPublic = r.visibility === 'public'
            return (
              <li
                key={r.id}
                className={cn(
                  'flex items-start gap-3 rounded-lg border border-border/60 bg-background/40 p-3 has-checked:border-primary/60 has-checked:bg-primary/10',
                )}
              >
                <input
                  type="radio"
                  id={inputId}
                  name={name}
                  value={r.fullPath}
                  checked={selected === r.fullPath}
                  onChange={() => {
                    onSelect(r)
                  }}
                  aria-describedby={`${inputId}-desc`}
                  className="mt-1 size-4 accent-primary"
                />
                <div className="min-w-0">
                  <label htmlFor={inputId} className="break-words font-medium">
                    {r.fullPath}
                  </label>
                  <p id={`${inputId}-desc`} className="flex items-center gap-1 text-sm text-muted-foreground">
                    {isPublic ? <Globe aria-hidden className="size-3.5" /> : <Lock aria-hidden className="size-3.5" />}
                    {r.visibility ? r.visibility.charAt(0).toUpperCase() + r.visibility.slice(1) : 'Private'}
                    {r.defaultBranch ? ` · default branch ${r.defaultBranch}` : ' · empty'}
                  </p>
                </div>
              </li>
            )
          })}
        </ul>
        {repos.hasNextPage && (
          <div>
            <Button
              variant="secondary"
              pending={repos.isFetchingNextPage}
              pendingLabel="Loading…"
              onClick={() => void repos.fetchNextPage()}
            >
              Load more projects
            </Button>
          </div>
        )}
      </fieldset>
    )
  }

  return (
    <div className="flex flex-col gap-3">
      <div role="search" aria-label="GitLab project search">
        <TextField
          id={searchId}
          label="Search GitLab projects"
          type="search"
          maxLength={SEARCH_MAX}
          autoComplete="off"
          spellCheck={false}
          value={search}
          error={error}
          onChange={(e) => {
            setSearch(e.target.value)
          }}
        />
      </div>
      {list}
      {allowCreate && (
        <>
          <div>
            <Button
              variant="secondary"
              onClick={() => {
                setCreating(true)
              }}
            >
              Create GitLab project
            </Button>
          </div>
          <CreateGitLabRepoDialog
            open={creating}
            connectionId={connectionId}
            onClose={() => {
              setCreating(false)
            }}
            onCreated={(repo) => {
              setCreating(false)
              onSelect(repo)
            }}
          />
        </>
      )}
    </div>
  )
}
