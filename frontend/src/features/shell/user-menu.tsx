import { BookOpen, ExternalLink, LogOut, Plug, User as UserIcon } from 'lucide-react'
import { toast } from 'sonner'

import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '../../components/ui/dropdown-menu'
import { THEME_OPTIONS } from '../../components/ui/theme-selector'
import { errorMessage } from '../../lib/errors'
import { Link, useAppRouter } from '../../lib/navigation'
import { DOCS_URL } from '../../lib/site'
import { isThemePreference, useTheme } from '../../lib/theme'
import { cn } from '../../lib/utils'
import { useLogout, type User } from '../auth/auth-api'
import { whenCollapsed } from './collapse-classes'
import { initials } from './initials'
import type { SidebarMode } from './use-sidebar-preference'

const roleLabel = { admin: 'Admin', user: 'User' } as const

/**
 * The user menu in the sidebar footer (SPEC §7.1, FR-206): avatar initials, name and email; Account, Connections, Theme
 * (FR-204), Docs (new tab), Sign out (FR-1). Shows the system role (FR-3).
 *
 * The theme choice uses the same options and store as `ThemeSelector` (`THEME_OPTIONS`, `useTheme`), as menu radio items:
 * a menu keeps focus on its items (Tab doesn't move inside it), so the selector's native radios couldn't be reached by
 * keyboard here.
 */
export function UserMenu({ user, mode = 'expanded' }: { user: User; mode?: SidebarMode }) {
  const router = useAppRouter()
  const logout = useLogout()
  const theme = useTheme()

  function signOut() {
    logout.mutate(undefined, {
      onSuccess: () => {
        void router.navigate({ to: '/login' })
      },
      onError: (error) => {
        toast.error(errorMessage(error))
      },
    })
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        aria-label={`Account menu for ${user.name}`}
        className={cn(
          'flex min-h-11 min-w-0 flex-1 items-center gap-2.5 rounded-lg px-2 text-left hover:bg-sidebar-accent/60',
          whenCollapsed.center[mode],
        )}
      >
        {user.avatarUrl ? (
          <img src={user.avatarUrl} alt="" className="size-8 shrink-0 rounded-full" />
        ) : (
          <span
            aria-hidden
            className="flex size-8 shrink-0 items-center justify-center rounded-full bg-primary/15 text-xs font-semibold text-primary"
          >
            {initials(user.name, user.email)}
          </span>
        )}
        <span className={cn('flex min-w-0 flex-1 flex-col leading-tight', whenCollapsed.hidden[mode])} aria-hidden>
          <span className="truncate text-sm font-medium">{user.name}</span>
          <span className="truncate text-xs text-muted-foreground">{user.email}</span>
        </span>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" side="top" className="w-64">
        <DropdownMenuLabel className="font-normal">
          <span className="block truncate font-medium">{user.name}</span>
          <span className="block truncate text-xs text-muted-foreground">{user.email}</span>
          <span className="mt-1 block text-xs text-muted-foreground">Role: {roleLabel[user.systemRole]}</span>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem asChild>
          <Link to="/account">
            <UserIcon aria-hidden /> Account
          </Link>
        </DropdownMenuItem>
        <DropdownMenuItem asChild>
          <Link to="/connections">
            <Plug aria-hidden /> Connections
          </Link>
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuLabel className="text-xs text-muted-foreground">Theme</DropdownMenuLabel>
        <DropdownMenuRadioGroup
          aria-label="Theme"
          value={theme.preference}
          onValueChange={(value) => {
            if (isThemePreference(value)) {
              theme.setPreference(value)
            }
          }}
        >
          {THEME_OPTIONS.map((option) => (
            <DropdownMenuRadioItem key={option.value} value={option.value}>
              <option.icon aria-hidden /> {option.label}
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
        <DropdownMenuSeparator />
        <DropdownMenuItem asChild>
          <a href={DOCS_URL} target="_blank" rel="noopener noreferrer">
            <BookOpen aria-hidden /> Docs
            <ExternalLink aria-hidden className="ml-auto" />
            <span className="sr-only"> (opens in a new tab)</span>
          </a>
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem
          disabled={logout.isPending}
          onSelect={(e) => {
            e.preventDefault()
            signOut()
          }}
        >
          <LogOut aria-hidden /> {logout.isPending ? 'Signing out…' : 'Sign out'}
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
