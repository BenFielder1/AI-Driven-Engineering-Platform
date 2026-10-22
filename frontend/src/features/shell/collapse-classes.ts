import type { SidebarMode } from './use-sidebar-preference'

/**
 * Class sets for the collapsible sidebar: `auto` collapses below xl only, `collapsed` / `expanded` are user pins.
 * Literal strings so Tailwind generates them. The drawer always renders `expanded`.
 */
export const whenCollapsed = {
  srOnly: { auto: 'max-xl:sr-only', collapsed: 'sr-only', expanded: '' },
  hidden: { auto: 'max-xl:hidden', collapsed: 'hidden', expanded: '' },
  center: { auto: 'max-xl:justify-center max-xl:px-0', collapsed: 'justify-center px-0', expanded: '' },
  stack: { auto: 'max-xl:flex-col', collapsed: 'flex-col', expanded: '' },
  corner: {
    auto: 'max-xl:absolute max-xl:top-1 max-xl:right-1',
    collapsed: 'absolute top-1 right-1',
    expanded: '',
  },
} as const satisfies Record<string, Record<SidebarMode, string>>
