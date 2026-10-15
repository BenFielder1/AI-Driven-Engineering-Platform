import { Monitor, Moon, Sun } from 'lucide-react'

import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuTrigger,
} from '../../components/ui/dropdown-menu'
import { setThemePreference, useThemePreference, type ThemePreference } from '../../lib/theme'
import { cn } from '../../lib/utils'

const options = [
  { value: 'system', label: 'System', icon: Monitor },
  { value: 'light', label: 'Light', icon: Sun },
  { value: 'dark', label: 'Dark', icon: Moon },
] as const satisfies readonly { value: ThemePreference; label: string; icon: typeof Sun }[]

const isPreference = (value: string): value is ThemePreference => options.some((o) => o.value === value)

/** The theme menu (SPEC §7.1): System (default) / Light / Dark, persisted in localStorage (src/lib/theme.ts). */
export function ThemeMenu({ className }: { className?: string }) {
  const preference = useThemePreference()
  const current = options.find((o) => o.value === preference) ?? options[0]
  const Icon = current.icon
  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        aria-label={`Theme: ${current.label}`}
        className={cn(
          'flex size-11 shrink-0 items-center justify-center rounded-lg text-muted-foreground hover:bg-sidebar-accent/60 hover:text-foreground',
          className,
        )}
      >
        <Icon className="size-5" aria-hidden />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" side="top" className="w-44">
        <DropdownMenuLabel>Theme</DropdownMenuLabel>
        <DropdownMenuRadioGroup
          value={preference}
          onValueChange={(value) => {
            if (isPreference(value)) {
              setThemePreference(value)
            }
          }}
        >
          {options.map((o) => (
            <DropdownMenuRadioItem key={o.value} value={o.value}>
              <o.icon className="size-4" aria-hidden /> {o.label}
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
