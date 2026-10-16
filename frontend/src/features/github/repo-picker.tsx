import { zodResolver } from '@hookform/resolvers/zod'
import { Lock, Globe } from 'lucide-react'
import { useEffect, useId, useState } from 'react'
import { useForm } from 'react-hook-form'
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
import { isApiError, messageFor } from '../../lib/errors'
import { cn } from '../../lib/utils'
import { useCreateGitHubRepo, useGitHubOwners, useGitHubRepos, type GitHubOwner, type GitHubRepo } from './github-api'
import { GitHubErrorAlert, GitHubLink } from './github-error-alert'

/** GitHub's repository name rules (letters, digits, `.`, `-`, `_`; at most 100; not `.` or `..`). */
const REPO_NAME = /^[A-Za-z0-9._-]{1,100}$/
const DESCRIPTION_MAX = 350

/** Why an owner can't take a new repository (FR-81), or null when it can. */
function ownerBlockedReason(owner: GitHubOwner): string | null {
  if (!owner.appInstalled) {
    return 'AIDE isn’t installed here'
  }
  return owner.canCreate ? null : 'AIDE can’t create repositories here'
}

const createSchema = z.object({
  owner: z.string().min(1, { error: 'Choose an owner where AIDE is installed.' }),
  name: z
    .string()
    .trim()
    .min(1, { error: 'Enter a repository name, such as "todo-app".' })
    .refine((v) => REPO_NAME.test(v) && v !== '.' && v !== '..', {
      error: 'Use up to 100 letters, digits, dots, hyphens or underscores.',
    }),
  description: z.string().max(DESCRIPTION_MAX, { error: `Descriptions are at most ${String(DESCRIPTION_MAX)} characters.` }),
  visibility: z.enum(['private', 'public']),
})

type CreateValues = z.infer<typeof createSchema>
const createIds = { owner: 'github-repo-owner', name: 'github-repo-name', description: 'github-repo-description' } as const

/**
 * FR-81 "Create repository": owner (the user, or an org where the App is installed), name, description, visibility.
 * GitHub initialises it with a README so the default branch exists; on success the caller selects it.
 */
export function CreateRepoDialog({
  open,
  installUrl,
  onClose,
  onCreated,
}: {
  open: boolean
  installUrl: string | null
  onClose: () => void
  onCreated: (repo: GitHubRepo) => void
}) {
  const owners = useGitHubOwners(open)
  const create = useCreateGitHubRepo()
  const [failure, setFailure] = useState<{ error: unknown; owner: string } | null>(null)
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    getValues,
    reset,
    formState: { errors, isSubmitted },
  } = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { owner: '', name: '', description: '', visibility: 'private' },
  })

  const creatable = (owners.data ?? []).filter((o) => ownerBlockedReason(o) === null)
  const blocked = (owners.data ?? []).filter((o) => ownerBlockedReason(o) !== null)
  const firstCreatable = creatable[0]?.login ?? ''

  // Preselect the first owner AIDE can create repositories for, once the owners load.
  useEffect(() => {
    if (firstCreatable && !getValues('owner')) {
      setValue('owner', firstCreatable)
    }
  }, [firstCreatable, getValues, setValue])

  const close = () => {
    reset({ owner: firstCreatable, name: '', description: '', visibility: 'private' })
    setFailure(null)
    create.reset()
    onClose()
  }

  const onSubmit = handleSubmit((v) => {
    setFailure(null)
    const description = v.description.trim()
    create.mutate(
      { owner: v.owner, name: v.name.trim(), private: v.visibility === 'private', ...(description ? { description } : {}) },
      {
        onSuccess: (repo) => {
          reset({ owner: firstCreatable, name: '', description: '', visibility: 'private' })
          create.reset()
          onCreated(repo)
        },
        onError: (error) => {
          if (isApiError(error, 'REPO_NAME_TAKEN')) {
            setError('name', { type: 'server', message: messageFor('REPO_NAME_TAKEN') }, { shouldFocus: true })
          } else if (isApiError(error, 'VALIDATION')) {
            for (const [key, messages] of Object.entries(error.fieldErrors)) {
              const field = (['owner', 'name', 'description'] as const).find((f) => f === key.toLowerCase())
              setError(field ?? 'name', { type: 'server', message: messages.join(' ') })
            }
          } else {
            setFailure({ error, owner: v.owner })
          }
        },
      },
    )
  })

  let body
  if (owners.isPending) {
    body = <LoadingState label="Loading where you can create repositories…" />
  } else if (owners.isError) {
    body = <GitHubErrorAlert error={owners.error} title="We couldn't load the owners" context="githubRepos" returnUrl="/projects/new" />
  } else {
    body = (
      <form
        noValidate
        aria-label="New repository"
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          // The dialog is portalled, but React still bubbles submit to the wizard's form: keep it here.
          e.stopPropagation()
          void onSubmit(e)
        }}
      >
        <ErrorSummary title="Check the repository" errors={isSubmitted ? fieldErrorSummary(errors, createIds) : []} />
        {failure && (
          <GitHubErrorAlert
            error={failure.error}
            title="The repository wasn't created"
            context="createGitHubRepo"
            installTarget={failure.owner}
            returnUrl="/projects/new"
          />
        )}
        {creatable.length === 0 && (
          <Alert tone="warning">
            <p>
              AIDE can&apos;t create a repository for any of your accounts: install the AIDE GitHub App on your account or
              an organisation first.
            </p>
            {installUrl && (
              <p className="mt-2">
                <GitHubLink href={installUrl} variant="link">
                  Install AIDE on more repositories
                </GitHubLink>
              </p>
            )}
          </Alert>
        )}
        <SelectField
          id={createIds.owner}
          label="Owner"
          error={errors.owner?.message}
          disabled={creatable.length === 0}
          {...register('owner')}
        >
          {creatable.length === 0 && <option value="">No owner available</option>}
          {owners.data.map((o) => {
            const reason = ownerBlockedReason(o)
            return (
              <option key={o.login} value={o.login} disabled={reason !== null}>
                {o.login}
                {o.type === 'organization' ? ' (organisation)' : ''}
                {reason ? ` (${reason})` : ''}
              </option>
            )
          })}
        </SelectField>
        {blocked.length > 0 && (
          <ul aria-label="Owners you can't use" className="-mt-2 flex flex-col gap-1 text-sm text-muted-foreground">
            {blocked.map((o) => (
              <li key={o.login}>
                {o.login}: {ownerBlockedReason(o)}.{' '}
                {!o.appInstalled && installUrl && (
                  <GitHubLink href={installUrl} variant="link">
                    {`Install AIDE on ${o.login}`}
                  </GitHubLink>
                )}
                {o.appInstalled && 'It needs the Administration permission, and you need permission to create repositories there.'}
              </li>
            ))}
          </ul>
        )}
        <TextField
          id={createIds.name}
          label="Repository name"
          autoComplete="off"
          spellCheck={false}
          maxLength={100}
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
            { value: 'private', label: 'Private', description: 'Only people you choose can see it.' },
            { value: 'public', label: 'Public', description: 'Anyone on the internet can see it.' },
          ]}
        />
        <p className="text-sm text-muted-foreground">GitHub creates it with a README, so the default branch exists.</p>
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" onClick={close} disabled={create.isPending}>
            Cancel
          </Button>
          <Button type="submit" pending={create.isPending} pendingLabel="Creating…" disabled={creatable.length === 0}>
            Create repository
          </Button>
        </div>
      </form>
    )
  }

  return (
    <ModalDialog open={open} title="Create repository" busy={create.isPending} onClose={close} className="sm:max-w-lg">
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
 * FR-81, FR-86: the repositories the AIDE GitHub App can access for the user, searchable and paged, with "Install AIDE on
 * more repositories" and "Create repository" (which selects the new repo).
 */
export function RepoPicker({
  searchId,
  selected,
  installUrl,
  error,
  onSelect,
}: {
  /** id of the search input (the error-summary link target). */
  searchId: string
  selected: string
  installUrl: string | null
  error?: string | undefined
  onSelect: (repo: GitHubRepo) => void
}) {
  const [search, setSearch] = useState('')
  const term = useDebounced(search.trim())
  const repos = useGitHubRepos(term)
  const [creating, setCreating] = useState(false)
  const name = useId()
  const errorId = error ? `${searchId}-error` : undefined
  const items = repos.data?.pages.flatMap((p) => p.items) ?? []

  let list
  if (repos.isPending) {
    list = <LoadingState label="Loading your repositories…" />
  } else if (repos.isError) {
    list = <GitHubErrorAlert error={repos.error} title="We couldn't load your repositories" context="githubRepos" returnUrl="/projects/new" />
  } else if (items.length === 0) {
    list = (
      <p className="rounded-lg border border-dashed border-border p-4 text-sm text-muted-foreground">
        {term
          ? `No repositories match "${term}".`
          : 'AIDE can’t access any of your repositories yet. Install AIDE on more repositories, or create one.'}
      </p>
    )
  } else {
    list = (
      <fieldset aria-describedby={errorId} className="flex flex-col gap-2">
        <legend className="sr-only">Repositories</legend>
        <ul aria-label="Repositories" className="flex max-h-80 flex-col gap-2 overflow-y-auto">
          {items.map((r) => {
            const inputId = `${name}-${String(r.id)}`
            return (
              <li
                key={r.id}
                className={cn(
                  'flex items-start gap-3 rounded-lg border border-border/60 bg-background/40 p-3 has-checked:border-primary/60 has-checked:bg-primary/10',
                  !r.canPush && 'opacity-70',
                )}
              >
                <input
                  type="radio"
                  id={inputId}
                  name={name}
                  value={r.fullName}
                  checked={selected === r.fullName}
                  disabled={!r.canPush}
                  onChange={() => {
                    onSelect(r)
                  }}
                  aria-describedby={r.canPush ? `${inputId}-desc` : `${inputId}-desc ${inputId}-reason`}
                  className="mt-1 size-4 accent-primary"
                />
                <div className="min-w-0">
                  <label htmlFor={inputId} className="break-words font-medium">
                    {r.fullName}
                  </label>
                  <p id={`${inputId}-desc`} className="flex items-center gap-1 text-sm text-muted-foreground">
                    {r.private ? <Lock aria-hidden className="size-3.5" /> : <Globe aria-hidden className="size-3.5" />}
                    {r.private ? 'Private' : 'Public'} · default branch {r.defaultBranch}
                  </p>
                  {!r.canPush && (
                    <p id={`${inputId}-reason`} className="text-sm text-muted-foreground">
                      You need write access to this repository
                    </p>
                  )}
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
      <div role="search" aria-label="Repository search">
        <TextField
          id={searchId}
          label="Search repositories"
          type="search"
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
      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="secondary"
          onClick={() => {
            setCreating(true)
          }}
        >
          Create repository
        </Button>
        {installUrl && <GitHubLink href={installUrl} variant="ghost">Install AIDE on more repositories</GitHubLink>}
      </div>
      <CreateRepoDialog
        open={creating}
        installUrl={installUrl}
        onClose={() => {
          setCreating(false)
        }}
        onCreated={(repo) => {
          setCreating(false)
          onSelect(repo)
        }}
      />
    </div>
  )
}
