import { zodResolver } from '@hookform/resolvers/zod'
import { Link, useRouter } from '@tanstack/react-router'
import { useState } from 'react'
import { useForm } from 'react-hook-form'

import { Alert, ErrorSummary, type SummaryError } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage, githubFailureMessage } from '../../lib/errors'
import { safeReturnUrl } from '../../lib/return-url'
import { githubSignInHref, useLogin } from './auth-api'
import { AuthCard, GitHubButton } from './auth-layout'
import { loginSchema, type LoginValues } from './schemas'

const fieldIds = { email: 'login-email', password: 'login-password' } as const

interface LoginPageProps {
  returnUrl?: string
  /** `error=GITHUB_AUTH_FAILED` came back from the OAuth flow, with its `reason`. */
  githubError?: { reason?: string }
}

/** /login (FR-1, FR-2). */
export function LoginPage({ returnUrl, githubError }: LoginPageProps) {
  const router = useRouter()
  const login = useLogin()
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
        router.history.push(destination)
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
      <GitHubButton href={githubSignInHref(destination)} />
      <p className="mt-6 text-sm text-muted">
        New to AIDE?{' '}
        <Link to="/register" search={returnUrl ? { returnUrl } : {}} className="font-medium text-link underline">
          Create an account
        </Link>
      </p>
    </AuthCard>
  )
}
