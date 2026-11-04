import { CircleCheckIcon, InfoIcon, Loader2Icon, OctagonXIcon, TriangleAlertIcon } from 'lucide-react'
import { useEffect, type CSSProperties } from 'react'
import { Toaster as Sonner, type ToasterProps } from 'sonner'

import { useResolvedTheme } from '../../lib/theme'
import { replayHandedOffToasts } from '../../lib/toast-handoff'

/**
 * sonner toasts for async results, themed from the tokens and following the theme in effect. Bottom-right (FR-205).
 * On mount it replays toasts handed over from the other route group's Toaster (src/lib/toast-handoff.ts).
 */
export function Toaster(props: ToasterProps) {
  const theme = useResolvedTheme()
  // Runs after sonner's own subscription effect (a child's effects run first), so the replay reaches this Toaster.
  useEffect(() => {
    replayHandedOffToasts()
  }, [])
  return (
    <Sonner
      theme={theme}
      position="bottom-right"
      className="toaster group"
      icons={{
        success: <CircleCheckIcon className="size-4" />,
        info: <InfoIcon className="size-4" />,
        warning: <TriangleAlertIcon className="size-4" />,
        error: <OctagonXIcon className="size-4" />,
        loading: <Loader2Icon className="size-4 animate-spin" />,
      }}
      style={
        {
          '--normal-bg': 'var(--popover)',
          '--normal-text': 'var(--popover-foreground)',
          '--normal-border': 'var(--border)',
          '--border-radius': 'var(--radius)',
        } as CSSProperties
      }
      {...props}
    />
  )
}
