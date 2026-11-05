import { CircleCheck, Circle, PartyPopper } from 'lucide-react'
import { useId } from 'react'

import { panelClass } from '../../components/shared/panel-class'
import { Skeleton } from '../../components/ui/skeleton'
import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { ErrorState } from '../../components/ui/states'
import { Link } from '../../lib/navigation'
import { cn } from '../../lib/utils'
import type { User } from '../auth/auth-api'
import { setChecklistDismissed } from './checklist-dismissed'
import { useHomeChecklist } from './home-api'

/** FR-207 / §4.30 "Get set up": four items with progress `done/4`; the first required item not done is highlighted. */
export function SetupChecklist({ user }: { user: User }) {
  const headingId = useId()
  const list = useHomeChecklist(user)
  const dismiss = (
    <Button
      variant="secondary"
      onClick={() => {
        setChecklistDismissed(true)
      }}
    >
      Dismiss
    </Button>
  )

  return (
    <section aria-labelledby={headingId} data-testid="setup-checklist" className={cn(panelClass, 'p-5')}>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 id={headingId} className="text-lg">
            Get set up
          </h2>
          {list.data && (
            <p className="text-sm text-muted-foreground" data-testid="checklist-progress">
              {list.data.done}/{list.data.items.length} done
            </p>
          )}
        </div>
        {list.data && dismiss}
      </div>

      {list.isPending && (
        <div role="status" aria-busy className="mt-4 space-y-3">
          <span className="sr-only">Loading your setup…</span>
          {[0, 1, 2, 3].map((i) => (
            <Skeleton key={i} className="h-10 w-full" />
          ))}
        </div>
      )}
      {list.isError && (
        <div className="mt-4">
          <ErrorState error={list.error} onRetry={list.refetch} title="Your setup couldn't load" />
        </div>
      )}
      {list.data?.complete && (
        <p className="mt-4 flex items-center gap-2 text-sm" data-testid="checklist-all-set">
          <PartyPopper aria-hidden className="size-4 text-success" />
          <span>
            <strong className="font-medium">You&apos;re all set.</strong>{' '}
            {list.data.items.some((i) => !i.done) && 'Connecting a deployment platform is optional.'}
          </span>
        </p>
      )}
      {list.data && !list.data.complete && (
        <ol className="mt-4 flex flex-col gap-2">
          {list.data.items.map((item) => {
            const highlighted = list.data?.highlighted === item.key
            return (
              <li
                key={item.key}
                data-done={item.done || undefined}
                data-highlighted={highlighted || undefined}
                className={cn(
                  'flex flex-wrap items-center gap-3 rounded-lg border border-border p-3',
                  highlighted && 'border-primary bg-primary/10',
                )}
              >
                {item.done ? (
                  <CircleCheck aria-hidden className="size-5 shrink-0 text-success" />
                ) : (
                  <Circle aria-hidden className="size-5 shrink-0 text-muted-foreground" />
                )}
                <div className="min-w-0 flex-1">
                  <p className={cn('text-sm font-medium', item.done && 'text-muted-foreground line-through')}>
                    {item.label}
                    {item.optional && <span className="font-normal text-muted-foreground"> (optional)</span>}
                    <span className="sr-only">{item.done ? ', done' : highlighted ? ', next step' : ', not done'}</span>
                  </p>
                  <p className="text-sm text-muted-foreground">{item.description}</p>
                </div>
                {!item.done && (
                  <Link
                    to={item.href}
                    className={buttonClasses(highlighted ? 'primary' : 'secondary')}
                    aria-label={`${item.actionLabel}: ${item.label}`}
                  >
                    {item.actionLabel}
                  </Link>
                )}
              </li>
            )
          })}
        </ol>
      )}
    </section>
  )
}
