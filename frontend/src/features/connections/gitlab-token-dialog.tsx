import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { z } from 'zod'

import { ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { SelectField } from '../../components/ui/select-field'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError } from '../../lib/errors'
import {
  GITLAB_TOKEN_SCOPES,
  gitlabHost,
  gitlabTokenPageUrl,
  useCreateGitLabTokenConnection,
  type GitLabConnection,
  type GitLabInstanceSummary,
} from '../gitlab/gitlab-api'
import { GitLabLink } from '../gitlab/gitlab-link'

/** Backend GitLabRules.MaxTokenLength. */
const TOKEN_MAX = 1000

const schema = z.object({
  instanceId: z.string().min(1, { error: 'Choose the GitLab instance the token is for.' }),
  token: z
    .string()
    .min(1, { error: 'Paste the GitLab access token.' })
    .max(TOKEN_MAX, { error: `The token is at most ${String(TOKEN_MAX)} characters.` })
    .refine((v) => !/[\s\p{Cc}]/u.test(v), { error: "The token can't contain spaces or line breaks." }),
})

type Values = z.infer<typeof schema>
const ids = { instanceId: 'gitlab-token-instance', token: 'gitlab-token-value' } as const

/**
 * FR-131 token fallback: a personal, project or group access token with `api` and `write_repository`. GitLab checks it
 * first (nothing is saved on CREDENTIAL_INVALID or CONNECTION_SCOPE_INSUFFICIENT). The token is a password field,
 * cleared after every attempt that reached the server, and the mutation holding it is reset at once.
 */
export function GitLabTokenDialog({
  instances,
  initialInstanceId,
  onClose,
  onAdded,
}: {
  instances: readonly GitLabInstanceSummary[]
  initialInstanceId: string
  onClose: () => void
  onAdded: (c: GitLabConnection) => void
}) {
  const create = useCreateGitLabTokenConnection()
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    control,
    formState: { errors, isSubmitted },
  } = useForm<Values>({ resolver: zodResolver(schema), defaultValues: { instanceId: initialInstanceId, token: '' } })
  const instanceId = useWatch({ control, name: 'instanceId' })
  const instance = instances.find((i) => i.id === instanceId)

  const close = () => {
    create.reset()
    onClose()
  }

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    create.mutate(
      { instanceId: v.instanceId, token: v.token },
      {
        onSuccess: (created) => {
          create.reset()
          onAdded(created)
        },
        onError: (error) => {
          setValue('token', '')
          create.reset()
          if (isApiError(error, 'CREDENTIAL_INVALID') || isApiError(error, 'CONNECTION_SCOPE_INSUFFICIENT')) {
            setError('token', { type: 'server', message: errorMessage(error, 'gitlabConnection') }, { shouldFocus: true })
            return
          }
          const unmatched = applyFieldErrors(error, setError, ['instanceId', 'token'])
          setServerErrors(
            unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error, 'gitlabConnection')],
          )
        },
      },
    )
  })

  return (
    <ModalDialog
      open
      title="Connect GitLab with an access token"
      description="AIDE checks the token with GitLab before saving it. It's stored encrypted and never shown again."
      busy={create.isPending}
      onClose={close}
      className="sm:max-w-lg"
    >
      <form noValidate onSubmit={(e) => void onSubmit(e)} aria-label="GitLab access token" className="flex flex-col gap-4">
        <ErrorSummary
          title="We couldn't connect GitLab"
          errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, ids) : [])]}
        />
        <SelectField id={ids.instanceId} label="GitLab instance" error={errors.instanceId?.message} {...register('instanceId')}>
          {instances.map((i) => (
            <option key={i.id} value={i.id}>
              {i.name} ({gitlabHost(i.baseUrl)})
            </option>
          ))}
        </SelectField>
        <section aria-labelledby="gitlab-token-what-title" className="rounded-lg border border-border/60 bg-background/40 p-3 text-sm">
          <h3 id="gitlab-token-what-title" className="font-semibold">
            What to create on GitLab
          </h3>
          <ul className="mt-1 list-disc space-y-1 pl-5">
            <li>
              <strong>Token type:</strong> a personal access token (or a project or group access token for a single project or
              group).
            </li>
            <li>
              <strong>Scopes:</strong>{' '}
              {GITLAB_TOKEN_SCOPES.map((s, index) => (
                <span key={s}>
                  {index > 0 && ' and '}
                  <code className="font-mono">{s}</code>
                </span>
              ))}
              . GitLab has no narrower scope that lets AIDE push and open merge requests.
            </li>
            <li>
              <strong>Expiry:</strong> pick a date that suits you (GitLab allows up to a year). AIDE warns you 14 days before
              it expires, so you can replace it.
            </li>
          </ul>
          {instance && (
            <p className="mt-2">
              <GitLabLink href={gitlabTokenPageUrl(instance.baseUrl)}>{`Create a token on ${gitlabHost(instance.baseUrl)}`}</GitLabLink>
            </p>
          )}
        </section>
        <TextField
          id={ids.token}
          label="Access token"
          type="password"
          autoComplete="new-password"
          spellCheck={false}
          maxLength={TOKEN_MAX}
          hint="Starts with glpat- for personal access tokens."
          error={errors.token?.message}
          {...register('token')}
        />
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="ghost" onClick={close} disabled={create.isPending}>
            Cancel
          </Button>
          <Button type="submit" pending={create.isPending} pendingLabel="Checking the token…">
            Connect
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}
