import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'

import { ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, connectionScopeMessage, errorMessage, isApiError } from '../../lib/errors'
import {
  ATLASSIAN_API_TOKEN_PAGE,
  BITBUCKET_TOKEN_SCOPES,
  useCreateBitbucketTokenConnection,
  type BitbucketConnection,
} from '../bitbucket/bitbucket-api'
import { GitHostLink } from '../git-hosts/git-host-link'

/** Backend BitbucketRules.MaxTokenLength and MaxEmailLength. */
const TOKEN_MAX = 1000
const EMAIL_MAX = 320

const schema = z.object({
  email: z
    .string()
    .trim()
    .min(1, { error: 'Enter the email address of your Atlassian account.' })
    .max(EMAIL_MAX, { error: `The email is at most ${String(EMAIL_MAX)} characters.` })
    .pipe(z.email({ error: 'Enter an email address, such as ada@example.com.' })),
  token: z
    .string()
    .min(1, { error: 'Paste the Atlassian API token.' })
    .max(TOKEN_MAX, { error: `The token is at most ${String(TOKEN_MAX)} characters.` })
    .refine((v) => !/[\s\p{Cc}]/u.test(v), { error: "The token can't contain spaces or line breaks." }),
})

type Values = z.infer<typeof schema>
const ids = { email: 'bitbucket-token-email', token: 'bitbucket-token-value' } as const

/**
 * FR-134 token fallback: an Atlassian API token with scopes, and the Atlassian account email it belongs to. Bitbucket
 * checks it and every scope first (nothing is saved on CREDENTIAL_INVALID or CONNECTION_SCOPE_INSUFFICIENT, which lists
 * the missing scopes). The token is a password field, cleared after every attempt that reached the server, and the
 * mutation holding it is reset at once.
 */
export function BitbucketTokenDialog({
  oauthAvailable,
  onClose,
  onAdded,
}: {
  /** The OAuth consumer is configured, so copy may suggest "Connect with Bitbucket" instead. */
  oauthAvailable: boolean
  onClose: () => void
  onAdded: (c: BitbucketConnection) => void
}) {
  const create = useCreateBitbucketTokenConnection()
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    formState: { errors, isSubmitted },
  } = useForm<Values>({ resolver: zodResolver(schema), defaultValues: { email: '', token: '' } })

  const close = () => {
    create.reset()
    onClose()
  }

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    create.mutate(
      { email: v.email, token: v.token },
      {
        onSuccess: (created) => {
          create.reset()
          onAdded(created)
        },
        onError: (error) => {
          setValue('token', '')
          create.reset()
          if (isApiError(error, 'CONNECTION_SCOPE_INSUFFICIENT')) {
            const message = connectionScopeMessage(error.details, error.unverifiableScopes, oauthAvailable)
            setError('token', { type: 'server', message }, { shouldFocus: true })
            return
          }
          if (isApiError(error, 'CREDENTIAL_INVALID')) {
            setError('token', { type: 'server', message: errorMessage(error, 'bitbucketConnection') }, { shouldFocus: true })
            return
          }
          const unmatched = applyFieldErrors(error, setError, ['email', 'token'])
          setServerErrors(
            unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error, 'bitbucketConnection')],
          )
        },
      },
    )
  })

  return (
    <ModalDialog
      open
      title="Connect Bitbucket with an API token"
      description="AIDE checks the token and its scopes with Bitbucket before saving it. It's stored encrypted and never shown again."
      busy={create.isPending}
      onClose={close}
      className="sm:max-w-lg"
    >
      <form noValidate onSubmit={(e) => void onSubmit(e)} aria-label="Bitbucket API token" className="flex flex-col gap-4">
        <ErrorSummary
          title="We couldn't connect Bitbucket"
          errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, ids) : [])]}
        />
        <section
          aria-labelledby="bitbucket-token-what-title"
          className="rounded-lg border border-border/60 bg-background/40 p-3 text-sm"
        >
          <h3 id="bitbucket-token-what-title" className="font-semibold">
            What to create on Atlassian
          </h3>
          <ul className="mt-1 list-disc space-y-1 pl-5">
            <li>
              <strong>Token type:</strong> an API token <em>with scopes</em> (&quot;Create API token with scopes&quot;, app
              Bitbucket). App passwords don&apos;t work: Atlassian has removed them.
            </li>
            <li>
              <strong>Scopes:</strong>
              <ul aria-label="Required scopes" className="mt-1 flex flex-wrap gap-x-3 gap-y-1">
                {BITBUCKET_TOKEN_SCOPES.map((s) => (
                  <li key={s}>
                    <code className="font-mono">{s}</code>
                  </li>
                ))}
              </ul>
            </li>
            <li>
              <strong>Expiry:</strong> Atlassian allows up to a year. Pick a date that suits you and put a reminder in your
              calendar: when it expires, AIDE marks the connection invalid until you connect a new token.
            </li>
          </ul>
          <p className="mt-2">
            <GitHostLink site="Atlassian" href={ATLASSIAN_API_TOKEN_PAGE}>
              Create a token on Atlassian
            </GitHostLink>
          </p>
        </section>
        <TextField
          id={ids.email}
          label="Atlassian account email"
          type="email"
          autoComplete="email"
          spellCheck={false}
          maxLength={EMAIL_MAX}
          hint="The email you sign in to Atlassian with. Bitbucket checks the token with it; it's stored encrypted with the token."
          error={errors.email?.message}
          {...register('email')}
        />
        <TextField
          id={ids.token}
          label="API token"
          type="password"
          autoComplete="new-password"
          spellCheck={false}
          maxLength={TOKEN_MAX}
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
