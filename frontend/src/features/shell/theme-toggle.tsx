import { setThemePreference, useThemePreference, type ThemePreference } from '../../lib/theme'

/** Theme: System (default) / Light / Dark, persisted in localStorage (SPEC §7). */
export function ThemeToggle() {
  const preference = useThemePreference()
  return (
    <div className="flex items-center">
      <label htmlFor="theme-select" className="sr-only">
        Theme
      </label>
      <select
        id="theme-select"
        value={preference}
        onChange={(e) => {
          setThemePreference(e.target.value as ThemePreference)
        }}
        className="min-h-9 rounded-md border border-field-border bg-canvas px-2 text-sm text-fg"
      >
        <option value="system">System theme</option>
        <option value="light">Light theme</option>
        <option value="dark">Dark theme</option>
      </select>
    </div>
  )
}
