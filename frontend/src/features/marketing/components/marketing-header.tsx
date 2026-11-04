import { useQuery } from '@tanstack/react-query'
import { MenuIcon } from 'lucide-react'
import { useState } from 'react'

import { BrandLockup } from '../../../components/brand'
import { buttonClasses } from '../../../components/ui/button-classes'
import { Sheet, SheetContent, SheetTitle, SheetTrigger } from '../../../components/ui/sheet'
import { Link } from '../../../lib/navigation'
import { cn } from '../../../lib/utils'
import { meQueryOptions } from '../../auth/auth-api'
import { githubLink, headerLinks } from '../content/site-nav'
import { SiteLink } from './site-link'

const navLinkClass =
  'inline-flex min-h-10 items-center rounded-lg px-3 text-sm text-muted-foreground transition-colors hover:text-foreground aria-[current=page]:text-foreground aria-[current=page]:font-medium'

/**
 * Whether the visitor is signed in, for the header buttons (FR-210, FR-211). Never blocks rendering: until `me`
 * resolves (and on the server) the visitor buttons show; a 401 resolves to `null` (expected, not an error, SPEC §6).
 */
function useSignedIn(): boolean {
  return useQuery(meQueryOptions).data != null
}

/** Sign in + Get started, or Open app when signed in. */
function AuthButtons({ signedIn, onNavigate, stacked = false }: { signedIn: boolean; onNavigate?: () => void; stacked?: boolean }) {
  if (signedIn) {
    return (
      <Link to="/home" className={buttonClasses('primary', cn(stacked && 'w-full'))} onClick={onNavigate}>
        Open app
      </Link>
    )
  }
  return (
    <>
      <Link to="/login" className={buttonClasses('ghost', cn('px-3', stacked && 'w-full border border-field-border'))} onClick={onNavigate}>
        Sign in
      </Link>
      <Link to="/register" className={buttonClasses('primary', cn('px-3 sm:px-4', stacked && 'w-full'))} onClick={onNavigate}>
        Get started
      </Link>
    </>
  )
}

/**
 * FR-210: the sticky, translucent marketing header. From `md` up the links sit inline; below it a menu button opens a
 * sheet with the same links (Radix dialog: focus trapped, Escape closes, focus returns to the button).
 */
export function MarketingHeader() {
  const signedIn = useSignedIn()
  const [menuOpen, setMenuOpen] = useState(false)
  const close = () => {
    setMenuOpen(false)
  }
  return (
    <header
      data-testid="marketing-header"
      className="sticky top-0 z-40 border-b border-border/70 bg-background/80 backdrop-blur-md supports-[backdrop-filter]:bg-background/70"
    >
      <div className="mx-auto flex h-16 max-w-6xl items-center gap-2 px-4 sm:gap-4">
        <Link to="/" aria-label="Tevrin home" className="inline-flex min-h-10 shrink-0 items-center rounded-lg">
          <BrandLockup wordmarkClassName="sr-only sm:not-sr-only sm:flex" />
        </Link>
        <nav aria-label="Primary" className="hidden md:block">
          <ul className="flex items-center gap-0.5">
            {headerLinks.map((link) => (
              <li key={link.label}>
                <SiteLink link={link} className={navLinkClass} />
              </li>
            ))}
          </ul>
        </nav>
        <div className="ml-auto flex items-center gap-1 sm:gap-2">
          <SiteLink link={githubLink} className={cn(navLinkClass, 'hidden lg:inline-flex')} />
          <AuthButtons signedIn={signedIn} />
          <Sheet open={menuOpen} onOpenChange={setMenuOpen}>
            <SheetTrigger
              className="inline-flex size-10 items-center justify-center rounded-lg text-foreground hover:bg-accent md:hidden"
              aria-label="Open menu"
            >
              <MenuIcon aria-hidden className="size-5" />
            </SheetTrigger>
            <SheetContent side="right" aria-describedby={undefined} className="p-4 pt-14">
              <SheetTitle className="sr-only">Menu</SheetTitle>
              <nav aria-label="Menu">
                <ul className="flex flex-col gap-1">
                  {[...headerLinks, githubLink].map((link) => (
                    <li key={link.label}>
                      <SiteLink link={link} onNavigate={close} className={cn(navLinkClass, 'w-full text-base')} />
                    </li>
                  ))}
                </ul>
              </nav>
              <div className="mt-2 flex flex-col gap-2 border-t border-border pt-4">
                <AuthButtons signedIn={signedIn} onNavigate={close} stacked />
              </div>
            </SheetContent>
          </Sheet>
        </div>
      </div>
    </header>
  )
}
