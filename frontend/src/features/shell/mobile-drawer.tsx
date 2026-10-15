import { BrandLockup } from '../../components/brand'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '../../components/ui/sheet'
import type { User } from '../auth/auth-api'
import { SearchButton, ShellNavigation } from './sidebar'
import { ThemeMenu } from './theme-toggle'
import type { ShellNav } from './use-shell-nav'
import { UserMenu } from './user-menu'

/**
 * FR-53: below lg the navigation opens as a left drawer (same nav-config as the sidebar, always expanded). Width is
 * capped to the viewport so no horizontal scroll appears at 375 px. Choosing a page closes it.
 */
export function MobileDrawer({
  open,
  onOpenChange,
  user,
  nav,
  onOpenPalette,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  user: User
  nav: ShellNav
  onOpenPalette: () => void
}) {
  const close = () => {
    onOpenChange(false)
  }
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent
        side="left"
        data-testid="mobile-drawer"
        className="flex w-[85vw] max-w-xs flex-col gap-0 overflow-x-hidden border-sidebar-border/70 bg-sidebar/95 p-0 text-sidebar-foreground backdrop-blur-xl"
      >
        <SheetHeader className="gap-3 border-b border-sidebar-border/70 p-4 pr-14">
          <SheetTitle asChild>
            <div>
              <BrandLockup />
              <span className="sr-only">Navigation</span>
            </div>
          </SheetTitle>
          <SheetDescription className="sr-only">Pages in AIDE</SheetDescription>
          <SearchButton
            mode="expanded"
            onClick={() => {
              close()
              onOpenPalette()
            }}
          />
        </SheetHeader>
        <div className="min-h-0 flex-1 overflow-y-auto p-3">
          <ShellNavigation nav={nav} mode="expanded" onNavigate={close} />
        </div>
        <div className="flex items-center gap-1 border-t border-sidebar-border/70 p-3">
          <UserMenu user={user} />
          <ThemeMenu />
        </div>
      </SheetContent>
    </Sheet>
  )
}
