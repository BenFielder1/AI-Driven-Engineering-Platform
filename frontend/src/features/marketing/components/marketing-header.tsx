import { MenuIcon } from 'lucide-react'
import { lazy, Suspense, useRef, useState } from 'react'

import { BrandLockup } from '../../../components/brand'
import { buttonClasses } from '../../../components/ui/button-classes'
import { Link } from '../../../lib/navigation'
import { cn } from '../../../lib/utils'
import { useMarketingSignedIn } from '../marketing-session'
import { githubLink, headerLinks } from '../content/site-nav'
import { SiteLink } from './site-link'

const navLinkClass =
  'inline-flex min-h-10 items-center rounded-lg px-3 text-sm text-muted-foreground transition-colors hover:text-foreground aria-[current=page]:text-foreground aria-[current=page]:font-medium'

// The menu sheet (Radix dialog) loads on first use, outside `/`'s first-load JS (SPEC §8).
const loadMenuSheet = () => import('./marketing-menu-sheet')
const MarketingMenuSheet = lazy(loadMenuSheet)

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
 * sheet with the same links (Radix dialog: focus trapped, Escape closes, focus returns to the button), loaded on first use.
 */
export function MarketingHeader() {
  const signedIn = useMarketingSignedIn()
  const [menuOpen, setMenuOpen] = useState(false)
  // Mounted from the first open on, so the sheet can animate out and later opens are instant.
  const [menuUsed, setMenuUsed] = useState(false)
  const menuButton = useRef<HTMLButtonElement>(null)
  const preload = () => {
    void loadMenuSheet()
  }
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
          <button
            ref={menuButton}
            type="button"
            className="inline-flex size-10 items-center justify-center rounded-lg text-foreground hover:bg-accent md:hidden"
            aria-label="Open menu"
            aria-haspopup="dialog"
            aria-expanded={menuOpen}
            onPointerEnter={preload}
            onFocus={preload}
            onClick={() => {
              setMenuUsed(true)
              setMenuOpen(true)
            }}
          >
            <MenuIcon aria-hidden className="size-5" />
          </button>
          {menuUsed && (
            <Suspense fallback={null}>
              <MarketingMenuSheet open={menuOpen} onOpenChange={setMenuOpen} triggerRef={menuButton}>
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
              </MarketingMenuSheet>
            </Suspense>
          )}
        </div>
      </div>
    </header>
  )
}
