import dynamic from 'next/dynamic'
import { useEffect, useRef, useState, type ReactNode } from 'react'

import { useLocation } from '../../lib/navigation'
import { SkipLink } from '../../components/shared/skip-link'
import { ThemeSelector } from '../../components/ui/theme-selector'
import { cn } from '../../lib/utils'
import type { User } from '../auth/auth-api'
import { Header } from './header'
import { HeaderActionSlotProvider } from './header-actions'
import { HealthIndicator } from './health-indicator'
import { MobileDrawer } from './mobile-drawer'
import { Sidebar } from './sidebar'
import { useShellNav } from './use-shell-nav'
import { useSidebarPreference, type SidebarMode } from './use-sidebar-preference'

export { SkipLink }

/**
 * SPEC §8 bundle budget: the ⌘K palette (cmdk) is not part of the initial chunks. It is mounted on first open, and its
 * chunk is fetched while the browser is idle after the shell renders, so the first ⌘K or button press opens it at once
 * (and still works, after the fetch, if the idle preload hasn't run yet).
 */
const loadCommandPalette = () => import('./command-palette')
const CommandPalette = dynamic(() => loadCommandPalette().then((m) => m.CommandPalette), { ssr: false })

function preloadCommandPalette(): () => void {
  const preload = () => {
    void loadCommandPalette()
  }
  if (typeof window.requestIdleCallback === 'function') {
    const id = window.requestIdleCallback(preload, { timeout: 5000 })
    return () => {
      window.cancelIdleCallback(id)
    }
  }
  const id = window.setTimeout(preload, 2000)
  return () => {
    window.clearTimeout(id)
  }
}

const contentPadding: Record<SidebarMode, string> = {
  auto: 'lg:pl-14 xl:pl-60',
  collapsed: 'lg:pl-14',
  expanded: 'lg:pl-60',
}

/**
 * FR-53, FR-206 signed-in shell (SPEC §7.1): skip link (no ambient orbs since FR-202), the collapsible sidebar (lg and up),
 * the mobile drawer, the sticky opaque header with breadcrumbs, the page's primary action and the bell, the ⌘K palette, the main landmark and a footer with API readiness.
 */
export function AppShell({ user, children }: { user: User; children: ReactNode }) {
  const nav = useShellNav(user)
  const sidebar = useSidebarPreference()
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [paletteOpen, setPaletteOpen] = useState(false)
  // FR-209: where focus was when the palette opened (the search button, or the page for ⌘K), restored on close when
  // the dialog left focus nowhere (e.g. after a theme action).
  const paletteReturnFocus = useRef<HTMLElement | null>(null)
  const wasPaletteOpen = useRef(false)
  useEffect(() => {
    if (paletteOpen || !wasPaletteOpen.current) {
      wasPaletteOpen.current = paletteOpen
      return undefined
    }
    wasPaletteOpen.current = false
    const target = paletteReturnFocus.current
    const id = window.setTimeout(() => {
      if (target?.isConnected && document.activeElement === document.body) {
        target.focus()
      }
    }, 0)
    return () => {
      window.clearTimeout(id)
    }
  }, [paletteOpen])
  // Mounted (and its chunk loaded) only once it has been opened.
  const [paletteMounted, setPaletteMounted] = useState(false)
  // FR-206: the header's primary-action slot, filled by the page through HeaderAction.
  const [actionSlot, setActionSlot] = useState<HTMLElement | null>(null)
  // FR-53: any navigation (a nav link, the user menu, Back/Forward) closes the drawer.
  const { href } = useLocation()
  const [drawerHref, setDrawerHref] = useState(href)
  if (href !== drawerHref) {
    setDrawerHref(href)
    setDrawerOpen(false)
  }

  // ⌘K / Ctrl+K toggles the palette from anywhere.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        if (document.activeElement instanceof HTMLElement && !document.activeElement.closest('[role=dialog]')) {
          paletteReturnFocus.current = document.activeElement
        }
        setPaletteMounted(true)
        setPaletteOpen((v) => !v)
      }
    }
    window.addEventListener('keydown', onKey)
    const cancelPreload = preloadCommandPalette()
    return () => {
      window.removeEventListener('keydown', onKey)
      cancelPreload()
    }
  }, [])

  const openPalette = () => {
    paletteReturnFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    setPaletteMounted(true)
    setPaletteOpen(true)
  }

  return (
    <div className="relative isolate min-h-dvh overflow-x-clip">
      <SkipLink />
      <Sidebar
        user={user}
        nav={nav}
        mode={sidebar.mode}
        expanded={sidebar.expanded}
        onToggle={sidebar.toggle}
        onOpenPalette={openPalette}
      />
      <div className={cn('flex min-h-dvh min-w-0 flex-col', contentPadding[sidebar.mode])}>
        <Header
          nav={nav}
          actionSlotRef={setActionSlot}
          onOpenDrawer={() => {
            setDrawerOpen(true)
          }}
          onOpenPalette={openPalette}
        />
        <main id="main" tabIndex={-1} className="mx-auto w-full max-w-7xl min-w-0 flex-1 px-4 py-6 outline-none sm:px-6 lg:px-8">
          <HeaderActionSlotProvider element={actionSlot}>{children}</HeaderActionSlotProvider>
        </main>
        <footer className="border-t border-border">
          <div className="mx-auto flex max-w-7xl flex-wrap items-center justify-between gap-2 px-4 py-3 text-xs text-muted-foreground sm:px-6 lg:px-8">
            <span>Tevrin</span>
            <HealthIndicator />
          </div>
        </footer>
      </div>
      <MobileDrawer open={drawerOpen} onOpenChange={setDrawerOpen} user={user} nav={nav} onOpenPalette={openPalette} />
      {paletteMounted && <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} nav={nav} />}
    </div>
  )
}

/** The signed-out layout for /login and /register: the theme selector (FR-204), and the page's centred card (with the lockup). */
export function PublicShell({ children }: { children: ReactNode }) {
  return (
    <div className="relative isolate flex min-h-dvh flex-col overflow-x-clip">
      <SkipLink />
      <header>
        <div className="mx-auto flex h-16 max-w-6xl items-center justify-end gap-4 px-4">
          <ThemeSelector compact />
        </div>
      </header>
      <main id="main" tabIndex={-1} className="flex-1 outline-none">
        {children}
      </main>
    </div>
  )
}
