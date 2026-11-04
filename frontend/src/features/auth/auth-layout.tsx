import { CheckIcon } from 'lucide-react'
import type { ReactNode } from 'react'

import { BrandLockup } from '../../components/brand'
import { panelClass } from '../../components/shared/panel-class'
import { Link } from '../../lib/navigation'
import { useDocumentTitle } from '../../lib/use-document-title'
import { cn } from '../../lib/utils'
import { TAGLINE } from '../marketing/content/pages'
import { LifecycleDiagram } from '../marketing/components/lifecycle-diagram'
import { bitbucketStartHref } from '../bitbucket/bitbucket-api'
import { BitbucketLogo } from '../bitbucket/bitbucket-logo'
import { GitLabLogo } from '../gitlab/gitlab-logo'
import { gitlabStartHref } from '../gitlab/gitlab-api'
import { githubSignInHref, useAuthProviders } from './auth-api'

/** The lockup as a link home (FR-223). */
function HomeLogo({ className }: { className?: string }) {
  return (
    <Link to="/" aria-label="Tevrin home" className={cn('inline-flex min-h-10 items-center rounded-lg', className)}>
      <BrandLockup className="scale-125" />
    </Link>
  )
}

/**
 * /login (FR-223): a centred card on a subtle brand glow (decorative, behind the card): the logo (→ `/`), the page
 * title (h1) and the form.
 */
export function AuthCard({ title, description, children }: { title: string; description?: ReactNode; children: ReactNode }) {
  useDocumentTitle(title)
  return (
    <div className="relative isolate flex min-h-[calc(100dvh-4rem)] items-center justify-center overflow-hidden px-4 py-10">
      <div
        aria-hidden="true"
        data-testid="auth-glow"
        className="bg-brand-gradient pointer-events-none absolute top-1/2 left-1/2 -z-10 size-[32rem] -translate-x-1/2 -translate-y-1/2 rounded-full opacity-15 blur-3xl"
      />
      <div className="relative w-full max-w-md">
        <div className={cn(panelClass, 'p-6 shadow-lg sm:p-8')}>
          <div className="mb-6 flex flex-col items-center gap-4 text-center">
            <HomeLogo />
            <div>
              <h1 className="text-2xl">{title}</h1>
              {description && <p className="mt-1 text-sm text-muted-foreground">{description}</p>}
            </div>
          </div>
          {children}
        </div>
      </div>
    </div>
  )
}

/** FR-223: the register page's three bullets (gates, versioned artifacts, rollback). */
const registerBullets = [
  'Approval gates between every phase, so nothing moves on without your say.',
  'Versioned, content-hashed artifacts you can compare and trace.',
  'Rollback and an audit trail for everything an agent did.',
] as const

/**
 * /register (FR-223): split screen from `lg` up. The left panel (hidden below `lg`) has the lockup, the tagline, three
 * bullets and a static lifecycle diagram; the right panel has the title (h1) and the form.
 */
export function AuthSplit({ title, children }: { title: string; children: ReactNode }) {
  useDocumentTitle(title)
  return (
    <div className="grid min-h-[calc(100dvh-4rem)] lg:grid-cols-2">
      <div
        data-testid="register-intro"
        className="relative isolate hidden overflow-hidden border-r border-border bg-muted/40 px-12 py-16 lg:flex lg:flex-col lg:justify-center"
      >
        <div
          aria-hidden="true"
          className="bg-brand-gradient pointer-events-none absolute -top-32 -left-32 -z-10 size-[28rem] rounded-full opacity-10 blur-3xl"
        />
        <div className="mx-auto w-full max-w-md">
          <HomeLogo className="origin-left" />
          <p className="mt-8 text-3xl font-semibold tracking-tight text-balance text-foreground">{TAGLINE}</p>
          <ul className="mt-6 flex flex-col gap-3 text-muted-foreground">
            {registerBullets.map((bullet) => (
              <li key={bullet} className="flex gap-3">
                <CheckIcon aria-hidden className="mt-0.5 size-5 shrink-0 text-primary" />
                <span>{bullet}</span>
              </li>
            ))}
          </ul>
          <LifecycleDiagram className="mt-10" />
        </div>
      </div>
      <div className="flex items-center justify-center px-4 py-10">
        <div className="w-full max-w-md">
          <div className="mb-6 flex flex-col items-center gap-4 text-center lg:items-start lg:text-left">
            <HomeLogo className="lg:hidden" />
            <h1 className="text-2xl">{title}</h1>
          </div>
          {children}
        </div>
      </div>
    </div>
  )
}


/** The "or" divider between the provider buttons and the email form (decorative). */
function OrDivider() {
  return (
    <div className="my-5 flex items-center gap-3 text-sm text-muted-foreground" aria-hidden="true">
      <span className="h-px flex-1 bg-border" />
      or
      <span className="h-px flex-1 bg-border" />
    </div>
  )
}

const socialButtonClass =
  'inline-flex min-h-11 w-full items-center justify-center gap-2 rounded-lg border border-field-border bg-background px-4 py-2 text-sm font-medium text-foreground transition-colors hover:bg-accent'

/**
 * "Continue with GitHub" (FR-2, FR-80) and "Continue with GitLab" per enabled instance with an OAuth application
 * (FR-131), and "Continue with Bitbucket" when the OAuth consumer is configured (FR-134): full-page links to the API's sign-in flows, which come back to `returnUrl`. GitHub is hidden when
 * `githubMode` is `none`. FR-223: the buttons sit above an "or" divider and the email form; the divider shows only when
 * there is at least one button.
 */
export function SocialSignIn({ returnUrl }: { returnUrl: string }) {
  const providers = useAuthProviders()
  const githubMode = providers.data?.githubMode ?? 'none'
  const gitlab = providers.data?.gitlab ?? []
  const bitbucket = providers.data?.bitbucket === true
  // FR-223: nothing renders while the providers load. With none configured (the default) the form never moves; when
  // some are configured the buttons and divider appear above it once, as soon as the (cached) providers resolve.
  if (providers.isPending) {
    return null
  }
  if (githubMode === 'none' && gitlab.length === 0 && !bitbucket) {
    return null
  }
  const single = gitlab.length === 1
  return (
    <>
      <div className="flex flex-col gap-3">
        {githubMode !== 'none' && (
          <a href={githubSignInHref(returnUrl)} className={socialButtonClass}>
            <svg aria-hidden="true" viewBox="0 0 16 16" className="h-4 w-4" fill="currentColor">
              <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z" />
            </svg>
            Continue with GitHub
          </a>
        )}
        {gitlab.map((i) => (
          <a key={i.id} href={gitlabStartHref('signin', i.id, returnUrl)} className={socialButtonClass}>
            <GitLabLogo />
            {single && i.isGitLabCom ? 'Continue with GitLab' : `Continue with ${i.name}`}
          </a>
        ))}
        {bitbucket && (
          <a href={bitbucketStartHref('signin', returnUrl)} className={socialButtonClass}>
            <BitbucketLogo />
            Continue with Bitbucket
          </a>
        )}
      </div>
      <OrDivider />
    </>
  )
}
