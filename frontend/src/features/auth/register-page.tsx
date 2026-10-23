import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'

import { ErrorSummary, type SummaryError } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage, errorMessages, isApiError } from '../../lib/errors'
import { Link, useAppRouter } from '../../lib/navigation'
import { safeReturnUrl } from '../../lib/return-url'
import { githubSignInHref, useRegister } from './auth-api'
import { AuthCard, GitHubButton } from './auth-layout'
import { registerSchema, type RegisterValues } from './schemas'

const fieldIds = { email: 'register-email', name: 'register-name', password: 'register-password' } as const

/** /register (FR-1). The first account becomes Admin (FR-3), decided by the server. */
export function RegisterPage({ returnUrl }: { returnUrl?: string }) {
  const router = useAppRouter()
  const registerUser = useRegister()
  const destination = safeReturnUrl(returnUrl)
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitted },
  } = useForm<RegisterValues>({
    resolver: zodResolver(registerSchema),
    defaultValues: { email: '', name: '', password: '' },
  })

  const onSubmit = handleSubmit((values) => {
    setServerErrors([])
    registerUser.mutate(values, {
      onSuccess: () => {
        router.history.push(destination)
      },
      onError: (error) => {
        if (isApiError(error, 'EMAIL_TAKEN')) {
          setError('email', { type: 'server', message: errorMessages.EMAIL_TAKEN })
          return
        }
        const unmatched = applyFieldErrors(error, setError, ['email', 'name', 'password'])
        if (unmatched.length > 0) {
          setServerErrors(unmatched)
        } else if (!isApiError(error, 'VALIDATION')) {
          setServerErrors([errorMessage(error)])
        }
      },
    })
  })

  const summary: SummaryError[] = [
    ...serverErrors.map((message) => ({ message })),
    ...(isSubmitted ? fieldErrorSummary(errors, fieldIds) : []),
  ]

  return (
    <AuthCard title="Create your AIDE account">
      <ErrorSummary title="We couldn't create your account" errors={summary} />
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
          id={fieldIds.name}
          label="Name"
          autoComplete="name"
          error={errors.name?.message}
          {...register('name')}
        />
        <TextField
          id={fieldIds.password}
          label="Password"
          type="password"
          autoComplete="new-password"
          hint="At least 8 characters, with a letter and a digit."
          error={errors.password?.message}
          {...register('password')}
        />
        <Button type="submit" pending={registerUser.isPending} pendingLabel="Creating account…" className="w-full">
          Create account
        </Button>
      </form>
      <GitHubButton href={githubSignInHref(destination)} />
      <p className="mt-6 text-sm text-muted-foreground">
        Already have an account?{' '}
        <Link to="/login" search={returnUrl ? { returnUrl } : {}} className="font-medium text-primary underline">
          Sign in
        </Link>
      </p>
    </AuthCard>
  )
}
