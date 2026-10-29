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
import { GitHostErrorAlert } from '../git-hosts/git-host-link'
import { useBitbucketOwners, useBitbucketRepos, useCreateBitbucketRepo, type BitbucketRepo } from './bitbucket-api'

/** Backend BitbucketRules limits. */
const NAME_MAX = 255
const DESCRIPTION_MAX = 2000
const SEARCH_MAX = 100
const SLUG_MAX = 62
const PROJECT_KEY_MAX = 128

/**
 * Bitbucket's slug of a repository name, as backend BitbucketRules.Slug makes it: lower case, other characters and runs
 * of `-` as one `-`, no leading or trailing `-` or `.`, cut to 62 characters. Shown as the address preview.
 */
export function bitbucketSlug(name: string): string {
  const slug = name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9_.-]+/g, '-')
    .replace(/-{2,}/g, '-')
    .replace(/^[-.]+|[-.]+$/g, '')
  return slug.length <= SLUG_MAX ? slug : slug.slice(0, SLUG_MAX).replace(/^[-.]+|[-.]+$/g, '')
}

const createSchema = z.object({
  owner: z.string().min(1, { error: 'Choose the workspace.' }),
  project: z.string(),
  /** A project key typed by hand, when the workspace's projects were truncated (it overrides `project`). */
  projectKey: z
    .string()
    .trim()
    .max(PROJECT_KEY_MAX, { error: `Project keys are at most ${String(PROJECT_KEY_MAX)} characters.` }),
  name: z
    .string()
    .trim()
    .min(1, { error: 'Enter a repository name, such as "todo-app".' })
    .max(NAME_MAX, { error: `Names are at most ${String(NAME_MAX)} characters.` })
    .refine((v) => bitbucketSlug(v) !== '', { error: 'Use at least one letter or digit.' }),
  description: z.string().max(DESCRIPTION_MAX, { error: `Descriptions are at most ${String(DESCRIPTION_MAX)} characters.` }),
  visibility: z.enum(['private', 'public']),
}).superRefine((v, ctx) => {
  if (!v.project && !v.projectKey) {
    ctx.addIssue({
      code: 'custom',
      path: ['project'],
      message: 'Choose the Bitbucket project. Bitbucket needs one for every repository.',
    })
  }
})

type CreateValues = z.infer<typeof createSchema>
const createIds = {
  owner: 'bitbucket-repo-owner',
  project: 'bitbucket-repo-project',
  projectKey: 'bitbucket-repo-project-key',
  name: 'bitbucket-repo-name',
  description: 'bitbucket-repo-description',
} as const
const blank: CreateValues = { owner: '', project: '', projectKey: '', name: '', description: '', visibility: 'private' }

/**
 * FR-135 "Create repository" on Bitbucket: the workspace, then one of its projects (Bitbucket requires one), name,
 * description and private. AIDE pushes a README to `main` after creating it, so the default branch exists; on success
 * the caller selects it.
 */
export function CreateBitbucketRepoDialog({
  open,
  connectionId,
  onClose,
  onCreated,
}: {
  open: boolean
  connectionId: string
  onClose: () => void
  onCreated: (repo: BitbucketRepo) => void
}) {
  const owners = useBitbucketOwners(connectionId, open)
  const create = useCreateBitbucketRepo()
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
  const owner = useWatch({ control, name: 'owner' })
  const slug = bitbucketSlug(useWatch({ control, name: 'name' }))

  const workspaces = owners.data?.workspaces ?? []
  const firstWorkspace = (workspaces.find((w) => w.projects.length > 0) ?? workspaces[0])?.slug ?? ''
  const workspace = workspaces.find((w) => w.slug === owner)
  const projects = workspace?.projects ?? []
  /** Bitbucket returned only the first page of the workspace's projects: a missing one can be typed by its key. */
  const projectsTruncated = workspace?.projectsTruncated === true
  const firstProject = projects[0]?.key ?? ''
  /** The chosen workspace's project keys, as a stable dependency. */
  const projectKeys = projects.map((p) => p.key).join('\n')

  // Preselect a workspace with projects, then the first project of the chosen workspace.
  useEffect(() => {
    if (firstWorkspace && !getValues('owner')) {
      setValue('owner', firstWorkspace)
    }
  }, [firstWorkspace, getValues, setValue])
  useEffect(() => {
    const current = getValues('project')
    if (current !== firstProject && !projectKeys.split('\n').includes(current)) {
      setValue('project', firstProject)
    }
  }, [projectKeys, firstProject, getValues, setValue])

  const close = () => {
    reset({ ...blank, owner: firstWorkspace })
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
        project: v.projectKey || v.project,
        name: v.name.trim(),
        private: v.visibility === 'private',
        ...(description ? { description } : {}),
      },
      {
        onSuccess: (repo) => {
          reset({ ...blank, owner: firstWorkspace })
          create.reset()
          onCreated(repo)
        },
        onError: (error) => {
          if (isApiError(error, 'REPO_NAME_TAKEN')) {
            setError('name', { type: 'server', message: errorMessage(error, 'createBitbucketRepo') }, { shouldFocus: true })
          } else if (isApiError(error, 'VALIDATION')) {
            for (const [key, messages] of Object.entries(error.fieldErrors)) {
              const field = (['owner', 'project', 'name', 'description'] as const).find((f) => f === key.toLowerCase())
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
    body = <LoadingState label="Loading your workspaces…" />
  } else if (owners.isError) {
    body = <GitHostErrorAlert error={owners.error} title="We couldn't load your workspaces" context="bitbucketRepos" />
  } else {
    body = (
      <form
        noValidate
        aria-label="New Bitbucket repository"
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          // The dialog is portalled, but React still bubbles submit to the wizard's form: keep it here.
          e.stopPropagation()
          void onSubmit(e)
        }}
      >
        <ErrorSummary title="Check the repository" errors={isSubmitted ? fieldErrorSummary(errors, createIds) : []} />
        {failure !== null && (
          <GitHostErrorAlert error={failure} title="The repository wasn't created" context="createBitbucketRepo" />
        )}
        {workspaces.length === 0 && (
          <Alert tone="warning">
            <p>This connection has no Bitbucket workspace. Create or join one on Bitbucket first.</p>
          </Alert>
        )}
        {owners.data.truncated && (
          <p className="text-sm text-muted-foreground" data-testid="bitbucket-workspaces-truncated">
            Showing your first 50 workspaces. If yours is missing, create the repository on Bitbucket and pick it from the
            list instead.
          </p>
        )}
        <SelectField
          id={createIds.owner}
          label="Workspace"
          error={errors.owner?.message}
          disabled={workspaces.length === 0}
          {...register('owner')}
        >
          {workspaces.length === 0 && <option value="">No workspace available</option>}
          {workspaces.map((w) => (
            <option key={w.slug} value={w.slug}>
              {w.name === w.slug ? w.slug : `${w.name} (${w.slug})`}
            </option>
          ))}
        </SelectField>
        <SelectField
          id={createIds.project}
          label="Project"
          hint="Bitbucket keeps every repository in a project of the workspace."
          error={errors.project?.message}
          disabled={projects.length === 0}
          {...register('project')}
        >
          {projects.length === 0 && <option value="">No project in this workspace</option>}
          {projects.map((p) => (
            <option key={p.key} value={p.key}>
              {p.name} ({p.key})
            </option>
          ))}
        </SelectField>
        {projectsTruncated && (
          <TextField
            id={createIds.projectKey}
            label="Project key (if it isn't listed)"
            hint="Showing this workspace's first projects. Type the key of a project that's missing, such as TODO."
            autoComplete="off"
            spellCheck={false}
            maxLength={PROJECT_KEY_MAX}
            error={errors.projectKey?.message}
            {...register('projectKey')}
          />
        )}
        {owner && projects.length === 0 && !projectsTruncated && (
          <p className="-mt-2 text-sm text-muted-foreground">
            This workspace has no projects you can use. Create one on Bitbucket, or pick another workspace.
          </p>
        )}
        <TextField
          id={createIds.name}
          label="Repository name"
          hint={slug ? `Its address: ${owner || 'workspace'}/${slug}` : "Bitbucket makes the repository's address from the name."}
          autoComplete="off"
          maxLength={NAME_MAX}
          error={errors.name?.message}
          {...register('name')}
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
            { value: 'private', label: 'Private', description: 'Only people with access to the workspace can see it.' },
            { value: 'public', label: 'Public', description: 'Anyone can see it.' },
          ]}
        />
        <p className="text-sm text-muted-foreground">
          AIDE pushes a first commit with a README to main, so the default branch exists.
        </p>
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" onClick={close} disabled={create.isPending}>
            Cancel
          </Button>
          <Button type="submit" pending={create.isPending} pendingLabel="Creating…" disabled={projects.length === 0 && !projectsTruncated}>
            Create repository
          </Button>
        </div>
      </form>
    )
  }

  return (
    <ModalDialog open={open} title="Create Bitbucket repository" busy={create.isPending} onClose={close} className="sm:max-w-lg">
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
 * FR-135: the Bitbucket repositories the connection's user can write to, across their workspaces, searchable and paged
 * with the API's opaque cursor, with "Create Bitbucket repository" (which selects the new repository).
 */
export function BitbucketRepoPicker({
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
  /** The selected repository's full name (`workspace/slug`). */
  selected: string
  error?: string | undefined
  onSelect: (repo: BitbucketRepo) => void
  /** Offer "Create Bitbucket repository" (off while the result can't be used, so no orphan repositories are created). */
  allowCreate?: boolean
}) {
  const [search, setSearch] = useState('')
  const term = useDebounced(search.trim())
  const repos = useBitbucketRepos(connectionId, term)
  const [creating, setCreating] = useState(false)
  const name = useId()
  const errorId = error ? `${searchId}-error` : undefined
  const items = repos.data?.pages.flatMap((p) => p.items) ?? []

  let list
  if (repos.isPending) {
    list = <LoadingState label="Loading your Bitbucket repositories…" />
  } else if (repos.isError) {
    list = <GitHostErrorAlert error={repos.error} title="We couldn't load your Bitbucket repositories" context="bitbucketRepos" />
  } else if (items.length === 0) {
    list = (
      <p className="rounded-lg border border-dashed border-border p-4 text-sm text-muted-foreground">
        {term
          ? `No repositories match "${term}".`
          : allowCreate
            ? 'You can’t write to any Bitbucket repository yet. Create one.'
            : 'You can’t write to any Bitbucket repository yet.'}
      </p>
    )
  } else {
    list = (
      <fieldset aria-describedby={errorId} className="flex flex-col gap-2">
        <legend className="sr-only">Bitbucket repositories</legend>
        <ul aria-label="Bitbucket repositories" className="flex max-h-80 flex-col gap-2 overflow-y-auto">
          {items.map((r) => {
            const inputId = `${name}-${r.id.replace(/[^A-Za-z0-9_-]/g, '')}`
            return (
              <li
                key={r.id}
                className="flex items-start gap-3 rounded-lg border border-border/60 bg-background/40 p-3 has-checked:border-primary/60 has-checked:bg-primary/10"
              >
                <input
                  type="radio"
                  id={inputId}
                  name={name}
                  value={r.fullName}
                  checked={selected === r.fullName}
                  onChange={() => {
                    onSelect(r)
                  }}
                  aria-describedby={`${inputId}-desc`}
                  className="mt-1 size-4 accent-primary"
                />
                <div className="min-w-0">
                  <label htmlFor={inputId} className="break-words font-medium">
                    {r.fullName}
                  </label>
                  <p id={`${inputId}-desc`} className="flex flex-wrap items-center gap-1 text-sm text-muted-foreground">
                    {r.isPrivate ? <Lock aria-hidden className="size-3.5" /> : <Globe aria-hidden className="size-3.5" />}
                    {r.isPrivate ? 'Private' : 'Public'}
                    {r.projectKey ? ` · project ${r.projectKey}` : ''}
                    {r.mainBranch ? ` · main branch ${r.mainBranch}` : ' · empty'}
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
              Load more repositories
            </Button>
          </div>
        )}
      </fieldset>
    )
  }

  return (
    <div className="flex flex-col gap-3">
      <div role="search" aria-label="Bitbucket repository search">
        <TextField
          id={searchId}
          label="Search Bitbucket repositories"
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
              Create Bitbucket repository
            </Button>
          </div>
          <CreateBitbucketRepoDialog
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
