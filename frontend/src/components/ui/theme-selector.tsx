import { Monitor, Moon, Sun, type LucideIcon } from 'lucide-react'
import { useId } from 'react'

import { isThemePreference, useTheme, type ThemePreference } from '../../lib/theme'
import { cn } from '../../lib/utils'

export const THEME_OPTIONS: readonly { value: ThemePreference; label: string; icon: LucideIcon }[] = [
  { value: 'system', label: 'System', icon: Monitor },
  { value: 'light', label: 'Light', icon: Sun },
  { value: 'dark', label: 'Dark', icon: Moon },
]

/**
 * FR-204: the theme choice as a segmented radio group (System, Light, Dark; System by default), for the user menu and
 * the marketing footer. Native radios, so arrow keys move between options and the group is announced as "Theme".
 * `compact` shows the icons only (the labels stay for assistive tech). Each option is at least 32 × 32 px (FR-205).
 */
export function ThemeSelector({ className, compact = false, label = 'Theme' }: { className?: string; compact?: boolean; label?: string }) {
  const { preference, setPreference } = useTheme()
  const name = `theme-${useId()}`
  return (
    <fieldset data-slot="theme-selector" className={cn('inline-flex min-w-0 items-center', className)}>
      <legend className="sr-only">{label}</legend>
      <div className="inline-flex items-center gap-0.5 rounded-lg border border-border bg-muted p-0.5">
        {THEME_OPTIONS.map((option) => (
          <label
            key={option.value}
            title={compact ? option.label : undefined}
            className={cn(
              'relative inline-flex min-h-8 min-w-8 cursor-pointer items-center justify-center gap-1.5 rounded-md px-2 text-sm text-muted-foreground transition-colors',
              'hover:text-foreground has-checked:bg-background has-checked:text-foreground has-checked:shadow-xs',
              'has-focus-visible:outline-2 has-focus-visible:outline-offset-2 has-focus-visible:outline-ring',
            )}
          >
            <input
              type="radio"
              name={name}
              value={option.value}
              checked={preference === option.value}
              onChange={(e) => {
                if (isThemePreference(e.currentTarget.value)) {
                  setPreference(e.currentTarget.value)
                }
              }}
              className="sr-only"
            />
            <option.icon aria-hidden className="size-4" />
            <span className={cn(compact && 'sr-only')}>{option.label}</span>
          </label>
        ))}
      </div>
    </fieldset>
  )
}
