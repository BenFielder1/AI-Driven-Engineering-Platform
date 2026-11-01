import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'

import { Alert, ErrorSummary, type SummaryError } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage, gitHostFailureMessage, githubFailureMessage } from '../../lib/errors'
import { Link, useAppRouter } from '../../lib/navigation'
import { safeReturnUrl } from '../../lib/return-url'
import { useLogin, useMe } from './auth-api'
import { AuthCard, SocialSignIn } from './auth-layout'
import type { GitHostLinkProvider } from './auth-search'
import { GitHostLinkPanel, useGitHostPendingLink } from './git-host-link-panel'
import { loginSchema, type LoginValues } from './schemas'

const fieldIds = { email: 'login-email', password: 'login-password' } as const

interface LoginPageProps {
  returnUrl?: string
  /** `error=GITHUB_AUTH_FAILED` came back from the OAuth flow, with its `reason`. */
  githubError?: { reason?: string }
  /** FR-131, FR-134: `error=GIT_AUTH_FAILED` from a Git host's sign-in (GitLab, Bitbucket), with its `reason` and `provider`. */
  gitHostError?: { reason?: string; provider?: string }
  /**
   * FR-131, FR-134: `link=gitlab|bitbucket`: that host's sign-in is waiting to be linked to an existing account (confirm
   * or cancel).
   */
  linkProvider?: GitHostLinkProvider | undefined
}

/** /login (FR-1, FR-2, FR-131, FR-134). */
export function LoginPage({ returnUrl, githubError, gitHostError, linkProvider }: LoginPageProps) {
  const router = useAppRouter()
  const login = useLogin()
  const me = useMe().data ?? null
  /** While a Git host sign-in waits to be linked, signing in here stays on the page to confirm it. */
  const [linking, setLinking] = useState(linkProvider !== undefined)
  const pendingLink = useGitHostPendingLink(linkProvider ?? 'gitlab', linking)
  const destination = safeReturnUrl(returnUrl)
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitted },
  } = useForm<LoginValues>({ resolver: zodResolver(loginSchema), defaultValues: { email: '', password: '' } })

  const onSubmit = handleSubmit((values) => {
    setServerErrors([])
    login.mutate(values, {
      onSuccess: () => {
        if (!linking) {
          router.history.push(destination)
          return
        }
        // Stay to confirm only while a link is still waiting (it may have expired meanwhile).
        void pendingLink.refetch().then(({ data }) => {
          if (!data) {
            setLinking(false)
            router.history.push(destination)
          }
        })
      },
      onError: (error) => {
        const unmatched = applyFieldErrors(error, setError, ['email', 'password'])
        setServerErrors(unmatched.length > 0 ? unmatched : [errorMessage(error)])
      },
    })
  })

  const summary: SummaryError[] = [
    ...serverErrors.map((message) => ({ message })),
    ...(isSubmitted ? fieldErrorSummary(errors, fieldIds) : []),
  ]

  return (
    <AuthCard title="Sign in to AIDE">
      {githubError && (
        <Alert tone="error" role="alert" title="GitHub sign-in failed" className="mb-4">
          <p>{githubFailureMessage(githubError.reason)}</p>
        </Alert>
      )}
      {gitHostError && (
        <Alert tone="error" role="alert" title="Sign-in didn't complete" className="mb-4">
          <p>{gitHostFailureMessage(gitHostError.provider, gitHostError.reason)}</p>
        </Alert>
      )}
      {linking && linkProvider && (
        <GitHostLinkPanel
          key={linkProvider}
          provider={linkProvider}
          user={me}
          onDone={() => {
            setLinking(false)
            if (me) {
              router.history.push(destination)
            }
          }}
        />
      )}
      {!(linking && me) && (
        <>
          <ErrorSummary title="We couldn't sign you in" errors={summary} />
          <form noValidate onSubmit={(e) => void onSubmit(e)} className="mt-2 flex flex-col gap-4">
            <TextField
              id={fieldIds.email}
              label="Email"
              type="email"
              autoComplete="email"
              error={errors.email?.message}
              {...register('email')}
            />
            <TextField
              id={fieldIds.password}
              label="Password"
              type="password"
              autoComplete="current-password"
              error={errors.password?.message}
              {...register('password')}
            />
            <Button type="submit" pending={login.isPending} pendingLabel="Signing in…" className="w-full">
              Sign in
            </Button>
          </form>
          {!linking && <SocialSignIn returnUrl={destination} />}
        </>
      )}
      <p className="mt-6 text-sm text-muted-foreground">
        New to AIDE?{' '}
        <Link to="/register" search={returnUrl ? { returnUrl } : {}} className="font-medium text-primary underline">
          Create an account
        </Link>
      </p>
    </AuthCard>
  )
}
