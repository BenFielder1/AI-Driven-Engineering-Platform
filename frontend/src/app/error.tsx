'use client'

import { buttonClasses } from '../components/ui/button-classes'
import { Link } from '../lib/navigation'

/**
 * The 0.2.0 root error state ("Something went wrong" with Try again), for errors outside the `(app)` and `(public)` groups
 * (marketing pages, the site-wide 404) and in those groups' layouts. Next loads this file on every route, `/` included,
 * so it stays plain markup: no error copy table or shared states (SPEC §8 first-load JS budget). Errors here are render
 * failures, not API errors (the app's error boundaries map those to copy through src/lib/errors.ts).
 */
export default function RootError({ retry }: { error: unknown; retry: () => void }) {
  return (
    <main id="main" className="mx-auto max-w-xl px-4 py-12">
      <h1 className="mb-4 text-2xl font-semibold tracking-tight">Something went wrong</h1>
      <div role="alert" className="rounded-lg border border-l-4 border-destructive bg-destructive/10 p-3 text-sm text-foreground">
        <p className="font-semibold">The page couldn&apos;t load</p>
        <p>Something went wrong. Try again.</p>
        <div className="mt-2 flex flex-wrap gap-2">
          <button type="button" className={buttonClasses('secondary')} onClick={retry}>
            Try again
          </button>
          <Link to="/" className={buttonClasses('ghost')}>
            Go to the home page
          </Link>
        </div>
      </div>
    </main>
  )
}
