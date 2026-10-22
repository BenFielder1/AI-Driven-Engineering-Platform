import { useRouterState } from '@tanstack/react-router'
import { useEffect, useState, type ReactNode } from 'react'

import { cn } from '../../lib/utils'
import type { User } from '../auth/auth-api'
import { CommandPalette } from './command-palette'
import { Header } from './header'
import { HealthIndicator } from './health-indicator'
import { MobileDrawer } from './mobile-drawer'
import { Sidebar } from './sidebar'
import { ThemeMenu } from './theme-toggle'
import { useShellNav } from './use-shell-nav'
import { useSidebarPreference, type SidebarMode } from './use-sidebar-preference'

export function SkipLink() {
  return (
    <a
      href="#main"
      className="sr-only z-50 rounded-lg bg-primary px-4 py-3 text-primary-foreground focus:not-sr-only focus:fixed focus:top-2 focus:left-2"
    >
      Skip to content
    </a>
  )
}

/** SPEC §7.1 ambient blurred orbs behind the shell (decorative, static). */
export function AmbientOrbs() {
  return (
    <div aria-hidden className="pointer-events-none fixed inset-0 -z-10 overflow-hidden">
      <div className="absolute -top-40 -left-32 size-[28rem] rounded-full bg-primary/10 blur-3xl" />
      <div className="absolute top-1/3 -right-40 size-[32rem] rounded-full bg-chart-2/10 blur-3xl" />
      <div className="absolute -bottom-48 left-1/3 size-[26rem] rounded-full bg-chart-3/5 blur-3xl" />
    </div>
  )
}

const contentPadding: Record<SidebarMode, string> = {
  auto: 'lg:pl-16 xl:pl-64',
  collapsed: 'lg:pl-16',
  expanded: 'lg:pl-64',
}

/**
 * FR-53 signed-in shell (SPEC §7.1): skip link, ambient orbs, the collapsible sidebar (lg and up), the mobile drawer,
 * the sticky header with breadcrumbs and the bell, the ⌘K palette, the main landmark and a footer with API readiness.
 */
export function AppShell({ user, children }: { user: User; children: ReactNode }) {
  const nav = useShellNav(user)
  const sidebar = useSidebarPreference()
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [paletteOpen, setPaletteOpen] = useState(false)
  // FR-53: any navigation (a nav link, the user menu, Back/Forward) closes the drawer.
  const href = useRouterState({ select: (s) => s.location.href })
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
        setPaletteOpen((v) => !v)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('keydown', onKey)
    }
  }, [])

  const openPalette = () => {
    setPaletteOpen(true)
  }

  return (
    <div className="relative isolate min-h-dvh overflow-x-clip">
      <SkipLink />
      <AmbientOrbs />
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
          onOpenDrawer={() => {
            setDrawerOpen(true)
          }}
          onOpenPalette={openPalette}
        />
        <main id="main" tabIndex={-1} className="mx-auto w-full max-w-7xl min-w-0 flex-1 px-4 py-6 outline-none sm:px-6 lg:px-8">
          {children}
        </main>
        <footer className="border-t border-border/60">
          <div className="mx-auto flex max-w-7xl flex-wrap items-center justify-between gap-2 px-4 py-3 text-xs text-muted-foreground sm:px-6 lg:px-8">
            <span>AIDE Platform</span>
            <HealthIndicator />
          </div>
        </footer>
      </div>
      <MobileDrawer open={drawerOpen} onOpenChange={setDrawerOpen} user={user} nav={nav} onOpenPalette={openPalette} />
      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} nav={nav} />
    </div>
  )
}

/** The signed-out layout for /login and /register: the theme menu, and the page's centred frosted card (with the lockup). */
export function PublicShell({ children }: { children: ReactNode }) {
  return (
    <div className="relative isolate flex min-h-dvh flex-col overflow-x-clip">
      <SkipLink />
      <AmbientOrbs />
      <header>
        <div className="mx-auto flex h-16 max-w-6xl items-center justify-end gap-4 px-4">
          <ThemeMenu />
        </div>
      </header>
      <main id="main" tabIndex={-1} className="flex-1 outline-none">
        {children}
      </main>
    </div>
  )
}
