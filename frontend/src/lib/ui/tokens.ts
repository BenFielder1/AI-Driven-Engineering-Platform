/**
 * Tevrin design tokens (SPEC §4.31, FR-202): the single source of truth for the colours, type scale and motion in
 * `src/index.css`. `tokens.test.ts` checks that index.css declares exactly these values and that every text pair meets
 * WCAG AA 4.5:1 and every UI pair 3:1 in both themes. Change a value here and in index.css together.
 *
 * Roles (§4.31): neutral slate surfaces, an indigo primary, cyan as the secondary accent and focus colour (`ring`),
 * emerald / amber / rose for success / warning / danger. The values are the Tailwind palette colours the spec names,
 * converted to oklch. Where they differ from the §4.31 hex, it is to pass the AA test (values may shift, roles may not):
 * - `destructive` (light) is rose-700 (`#BE123C`) instead of rose-600: rose-600 gives under 4.5:1 as text on `card`
 *   (`#F8FAFC`) and on its own tint.
 * - `primary` (dark) is indigo-300 (`#A5B4FC`) instead of indigo-400: indigo-400 text on its own 15% tint (avatar
 *   initials, selected rows) over the dark surfaces is under 4.5:1.
 * - `sidebar-accent` (light, the hovered sidebar row) is slate-100, not slate-200: indigo-600 avatar initials on their
 *   15% tint over slate-200 give 4.13:1.
 * - `field-border` is slate-500 in both themes: form-field outlines need 3:1 against their surroundings (WCAG 1.4.11);
 *   `border` / `input` (slate-200 / slate-800) are decorative.
 *
 * shadcn's `accent` keeps its meaning here (the neutral hover surface used by menus, buttons and rows); the spec's
 * "accent / ring" role is `ring`. `warning` is never text (only fills, borders and dots, with `warning-foreground` on
 * top). `brand-from` / `brand-to` are the brand gradient stops (FR-196), used only by the mark, the marketing hero glow,
 * the active lifecycle stage and decorative borders, and never under text.
 */

export const TOKEN_NAMES = [
  'background',
  'foreground',
  'card',
  'card-foreground',
  'popover',
  'popover-foreground',
  'primary',
  'primary-foreground',
  'secondary',
  'secondary-foreground',
  'muted',
  'muted-foreground',
  'accent',
  'accent-foreground',
  'destructive',
  'destructive-foreground',
  'success',
  'success-foreground',
  'warning',
  'warning-foreground',
  'border',
  'input',
  'field-border',
  'ring',
  'brand-from',
  'brand-to',
  'chart-1',
  'chart-2',
  'chart-3',
  'chart-4',
  'chart-5',
  'sidebar',
  'sidebar-foreground',
  'sidebar-primary',
  'sidebar-primary-foreground',
  'sidebar-accent',
  'sidebar-accent-foreground',
  'sidebar-border',
  'sidebar-ring',
] as const

export type TokenName = (typeof TOKEN_NAMES)[number]
export type ThemeName = 'dark' | 'light'
export type ThemeTokens = Record<TokenName, string>

// Tailwind palette colours in oklch (converted from the hex the spec gives).
const white = 'oklch(1 0 0)'
const slate50 = 'oklch(0.984 0.003 247.9)' // #F8FAFC
const slate100 = 'oklch(0.968 0.007 247.9)' // #F1F5F9
const slate200 = 'oklch(0.929 0.013 255.5)' // #E2E8F0
const slate400 = 'oklch(0.711 0.035 256.8)' // #94A3B8
const slate500 = 'oklch(0.554 0.041 257.4)' // #64748B
const slate600 = 'oklch(0.446 0.037 257.3)' // #475569
const slate800 = 'oklch(0.279 0.037 260)' // #1E293B
const slate900 = 'oklch(0.208 0.04 265.8)' // #0F172A
const surfaceDark = 'oklch(0.18 0.032 266.6)' // #0B1120
const slate950 = 'oklch(0.129 0.041 264.7)' // #020617
const indigo600 = 'oklch(0.511 0.23 277)' // #4F46E5
const indigo400 = 'oklch(0.68 0.158 276.9)' // #818CF8
const indigo300 = 'oklch(0.785 0.104 274.7)' // #A5B4FC
const cyan600 = 'oklch(0.609 0.111 221.7)' // #0891B2
const cyan500 = 'oklch(0.715 0.126 215.2)' // #06B6D4
const cyan400 = 'oklch(0.797 0.134 211.5)' // #22D3EE

export const tokens: Record<ThemeName, ThemeTokens> = {
  light: {
    background: white,
    foreground: slate900,
    card: slate50,
    'card-foreground': slate900,
    popover: white,
    'popover-foreground': slate900,
    primary: indigo600,
    'primary-foreground': white,
    secondary: slate100,
    'secondary-foreground': slate900,
    muted: slate100,
    'muted-foreground': slate600,
    accent: slate100,
    'accent-foreground': slate900,
    destructive: 'oklch(0.514 0.198 16.9)', // rose-700 #BE123C
    'destructive-foreground': white,
    success: 'oklch(0.508 0.105 165.6)', // emerald-700 #047857
    'success-foreground': white,
    warning: 'oklch(0.769 0.165 70.1)', // amber-500 #F59E0B
    'warning-foreground': slate900,
    border: slate200,
    input: slate200,
    'field-border': slate500,
    ring: cyan600,
    'brand-from': indigo600,
    'brand-to': cyan500,
    'chart-1': indigo600,
    'chart-2': cyan600,
    'chart-3': 'oklch(0.596 0.127 163.2)', // emerald-600
    'chart-4': 'oklch(0.666 0.157 58.3)', // amber-600
    'chart-5': 'oklch(0.541 0.247 293)', // violet-600
    sidebar: slate50,
    'sidebar-foreground': slate900,
    'sidebar-primary': indigo600,
    'sidebar-primary-foreground': white,
    'sidebar-accent': slate100,
    'sidebar-accent-foreground': slate900,
    'sidebar-border': slate200,
    'sidebar-ring': cyan600,
  },
  dark: {
    background: slate950,
    foreground: slate50,
    card: surfaceDark,
    'card-foreground': slate50,
    popover: surfaceDark,
    'popover-foreground': slate50,
    primary: indigo300,
    'primary-foreground': slate950,
    secondary: slate800,
    'secondary-foreground': slate50,
    muted: slate900,
    'muted-foreground': slate400,
    accent: slate800,
    'accent-foreground': slate50,
    destructive: 'oklch(0.719 0.169 13.4)', // rose-400 #FB7185
    'destructive-foreground': slate950,
    success: 'oklch(0.773 0.153 163.2)', // emerald-400 #34D399
    'success-foreground': slate950,
    warning: 'oklch(0.837 0.164 84.4)', // amber-400 #FBBF24
    'warning-foreground': slate950,
    border: slate800,
    input: slate800,
    'field-border': slate500,
    ring: cyan400,
    'brand-from': indigo400,
    'brand-to': cyan400,
    'chart-1': indigo400,
    'chart-2': cyan400,
    'chart-3': 'oklch(0.773 0.153 163.2)', // emerald-400
    'chart-4': 'oklch(0.837 0.164 84.4)', // amber-400
    'chart-5': 'oklch(0.709 0.159 293.5)', // violet-400
    sidebar: surfaceDark,
    'sidebar-foreground': slate50,
    'sidebar-primary': indigo300,
    'sidebar-primary-foreground': slate950,
    'sidebar-accent': slate800,
    'sidebar-accent-foreground': slate50,
    'sidebar-border': slate800,
    'sidebar-ring': cyan400,
  },
}

export const RADIUS = '0.75rem'

/**
 * FR-203: the type scale (12/14/16/20/24/30/36/48/60 px), defined once; index.css `@theme` maps each step to Tailwind's
 * `text-*`. There is no 18 px step: `text-lg` is 20 px like `text-xl`, so existing `text-lg` headings land on the scale.
 */
export const TYPE_SCALE = {
  xs: '0.75rem',
  sm: '0.875rem',
  base: '1rem',
  lg: '1.25rem',
  xl: '1.25rem',
  '2xl': '1.5rem',
  '3xl': '1.875rem',
  '4xl': '2.25rem',
  '5xl': '3rem',
  '6xl': '3.75rem',
} as const

/** FR-203: Geist Sans for UI and headings, Geist Mono for IDs, hashes, versions, code and eyebrow labels (self-hosted). */
export const FONT_FAMILIES = {
  sans: "'Geist Sans', ui-sans-serif, system-ui, sans-serif",
  mono: "'Geist Mono', ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
} as const

/** FR-205: motion is 150–250 ms, opacity and translate only, and off under prefers-reduced-motion. */
export const MOTION = {
  fast: '150ms',
  base: '200ms',
  slow: '250ms',
} as const

/** Text on background pairs that must meet WCAG AA (4.5:1) in both themes. */
export const TEXT_PAIRS: readonly (readonly [fg: TokenName, bg: TokenName])[] = [
  ['foreground', 'background'],
  ['foreground', 'card'],
  ['foreground', 'muted'],
  ['foreground', 'popover'],
  ['card-foreground', 'card'],
  ['popover-foreground', 'popover'],
  ['primary-foreground', 'primary'],
  ['secondary-foreground', 'secondary'],
  ['accent-foreground', 'accent'],
  ['muted-foreground', 'background'],
  ['muted-foreground', 'card'],
  ['muted-foreground', 'muted'],
  ['muted-foreground', 'secondary'],
  ['muted-foreground', 'sidebar'],
  ['muted-foreground', 'sidebar-accent'],
  ['destructive-foreground', 'destructive'],
  ['success-foreground', 'success'],
  ['warning-foreground', 'warning'],
  // Coloured text: links and active icons (primary), errors (destructive), success messages.
  ['primary', 'background'],
  ['primary', 'card'],
  // Links on muted surfaces: unread notifications (`bg-muted`), hovered/selected rows and menus (accent, secondary).
  ['primary', 'muted'],
  ['primary', 'accent'],
  ['primary', 'secondary'],
  ['primary', 'popover'],
  ['primary', 'sidebar'],
  ['primary', 'sidebar-accent'],
  // Muted text in menus, the command palette and dialogs (popover) and on hovered items (accent).
  ['muted-foreground', 'popover'],
  ['muted-foreground', 'accent'],
  ['destructive', 'background'],
  ['destructive', 'card'],
  ['destructive', 'popover'],
  ['success', 'background'],
  ['success', 'card'],
  ['sidebar-foreground', 'sidebar'],
  ['sidebar-accent-foreground', 'sidebar-accent'],
  ['sidebar-primary-foreground', 'sidebar-primary'],
]

/**
 * Text on a translucent tint (`bg-<tint>/<alpha>`) composited over an opaque surface, as the UI really renders it.
 * Checked at 4.5:1 in both themes: `[fg, tint, alpha, surface]`.
 */
export const TINTED_TEXT_PAIRS: readonly (readonly [fg: TokenName, tint: TokenName, alpha: number, surface: TokenName])[] = [
  // Avatar initials (`bg-primary/15 text-primary`) in the sidebar, its hovered row, the mobile drawer and menus.
  ['primary', 'primary', 0.15, 'sidebar'],
  ['primary', 'primary', 0.15, 'sidebar-accent'],
  ['primary', 'primary', 0.15, 'background'],
  ['primary', 'primary', 0.15, 'card'],
  ['primary', 'primary', 0.15, 'popover'],
  // Selected rows (`bg-primary/10`, e.g. the selected review cycle) with a primary link.
  ['primary', 'primary', 0.1, 'card'],
  ['primary', 'primary', 0.1, 'background'],
  // Descriptions under a checked radio option (`has-checked:bg-primary/10`).
  ['muted-foreground', 'primary', 0.1, 'background'],
  ['muted-foreground', 'primary', 0.1, 'card'],
  ['muted-foreground', 'primary', 0.1, 'muted'],
  ['foreground', 'primary', 0.15, 'card'],
  // FR-205 status badges (`bg-<tone>/15`) and alerts (`bg-<tone>/10`): a `foreground` label on the tone's tint.
  ['foreground', 'success', 0.15, 'background'],
  ['foreground', 'success', 0.15, 'card'],
  ['foreground', 'warning', 0.15, 'background'],
  ['foreground', 'warning', 0.15, 'card'],
  ['foreground', 'destructive', 0.15, 'background'],
  ['foreground', 'destructive', 0.15, 'card'],
  ['foreground', 'primary', 0.15, 'background'],
  ['muted-foreground', 'destructive', 0.1, 'background'],
  ['muted-foreground', 'success', 0.1, 'background'],
  ['muted-foreground', 'warning', 0.1, 'background'],
  // Empty-state error icon (`bg-destructive/10 text-destructive`).
  ['destructive', 'destructive', 0.1, 'card'],
]

/** Non-text UI pairs that must meet WCAG AA (3:1): form-field outlines, focus rings, the active nav icon. */
export const NON_TEXT_PAIRS: readonly (readonly [fg: TokenName, bg: TokenName])[] = [
  ['sidebar-primary', 'sidebar-accent'],
  ['field-border', 'background'],
  ['field-border', 'card'],
  ['field-border', 'muted'],
  ['field-border', 'popover'],
  // FR-205: the 2 px focus ring, offset, in the focus colour, on every surface it can sit on.
  ['ring', 'background'],
  ['ring', 'card'],
  ['ring', 'muted'],
  ['ring', 'popover'],
  ['ring', 'sidebar'],
  ['ring', 'sidebar-accent'],
  ['sidebar-ring', 'sidebar'],
  // Filled controls against the page: primary buttons, checked radios.
  ['primary', 'background'],
  ['primary', 'card'],
]

/**
 * FR-196: the mark's `T` and the wordmark are `currentColor`, which is the text colour of where they sit: the sidebar,
 * the auth card and the marketing header (the page background). Each must reach 3:1 in both themes.
 */
export const BRAND_INK_PAIRS: readonly (readonly [ink: TokenName, surface: TokenName, where: string])[] = [
  ['sidebar-foreground', 'sidebar', 'sidebar'],
  ['card-foreground', 'card', 'auth card'],
  ['foreground', 'background', 'marketing header'],
]

/**
 * FR-196: the brand colours as hex, for the static assets (docs/v6/brand, public/) that can't read CSS variables.
 * `brand-from` / `brand-to` in `tokens` are these colours in oklch (tokens.test.ts checks they agree). Ink is the `T`
 * and the wordmark: light files are for light backgrounds, dark files for dark ones.
 */
export const BRAND_COLORS = {
  light: { from: '#4F46E5', to: '#06B6D4', ink: '#0F172A', surface: '#FFFFFF' },
  dark: { from: '#818CF8', to: '#22D3EE', ink: '#F8FAFC', surface: '#020617' },
} as const
