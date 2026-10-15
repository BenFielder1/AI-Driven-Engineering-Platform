/**
 * Design tokens: the single source of truth for the colours in `src/index.css` (SPEC §7.1).
 * `tokens.test.ts` checks that index.css declares exactly these values and that every text pair meets WCAG AA in both
 * themes (NFR-A11Y2). Change a value here and in index.css together.
 *
 * Values are Space Worm Analytics' `globals.css` (the reference design), except where they fail AIDE's contrast test:
 * - `muted-foreground` (dark) is `oklch(0.59 0.01 260)` instead of 0.57: 0.57 gives 4.35:1 on `secondary` and 4.46:1
 *   on `sidebar-accent` (hovered sidebar items).
 * - `destructive-foreground` (dark) is a dark red-black, `oklch(0.10 0.02 25)`: the reference's near-white gives
 *   3.89:1 on the dark `destructive` fill (danger buttons).
 * - `success` (light) is `oklch(0.50 0.15 155)` instead of 0.55 / 0.20: the reference gives 4.04:1 as text on the
 *   background and 3.93:1 under `success-foreground`.
 * - `field-border` is an AIDE addition (v1 had it too): form-field outlines need 3:1 against their surroundings
 *   (WCAG 1.4.11); the reference's `input` / `border` are decorative (about 1.3:1).
 * `warning` is never used as text (only fills, borders and strokes, with `warning-foreground` text on top).
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

export const tokens: Record<ThemeName, ThemeTokens> = {
  dark: {
    background: 'oklch(0.06 0.01 260)',
    foreground: 'oklch(0.97 0.005 260)',
    card: 'oklch(0.10 0.015 260)',
    'card-foreground': 'oklch(0.97 0.005 260)',
    popover: 'oklch(0.10 0.015 260)',
    'popover-foreground': 'oklch(0.97 0.005 260)',
    primary: 'oklch(0.78 0.18 195)',
    'primary-foreground': 'oklch(0.10 0.02 195)',
    secondary: 'oklch(0.16 0.015 260)',
    'secondary-foreground': 'oklch(0.97 0.005 260)',
    muted: 'oklch(0.13 0.01 260)',
    'muted-foreground': 'oklch(0.59 0.01 260)',
    accent: 'oklch(0.16 0.015 260)',
    'accent-foreground': 'oklch(0.97 0.005 260)',
    destructive: 'oklch(0.62 0.25 25)',
    'destructive-foreground': 'oklch(0.10 0.02 25)',
    success: 'oklch(0.72 0.19 155)',
    'success-foreground': 'oklch(0.10 0.02 155)',
    warning: 'oklch(0.80 0.16 80)',
    'warning-foreground': 'oklch(0.15 0.02 80)',
    border: 'oklch(0.18 0.015 260)',
    input: 'oklch(0.18 0.015 260)',
    'field-border': 'oklch(0.50 0.015 260)',
    ring: 'oklch(0.78 0.18 195)',
    'chart-1': 'oklch(0.78 0.18 195)',
    'chart-2': 'oklch(0.72 0.22 300)',
    'chart-3': 'oklch(0.74 0.18 165)',
    'chart-4': 'oklch(0.78 0.18 55)',
    'chart-5': 'oklch(0.70 0.22 340)',
    sidebar: 'oklch(0.08 0.012 260)',
    'sidebar-foreground': 'oklch(0.97 0.005 260)',
    'sidebar-primary': 'oklch(0.78 0.18 195)',
    'sidebar-primary-foreground': 'oklch(0.10 0.02 195)',
    'sidebar-accent': 'oklch(0.14 0.015 260)',
    'sidebar-accent-foreground': 'oklch(0.97 0.005 260)',
    'sidebar-border': 'oklch(0.18 0.015 260)',
    'sidebar-ring': 'oklch(0.78 0.18 195)',
  },
  light: {
    background: 'oklch(0.99 0.002 260)',
    foreground: 'oklch(0.10 0.02 260)',
    card: 'oklch(1 0 0)',
    'card-foreground': 'oklch(0.10 0.02 260)',
    popover: 'oklch(1 0 0)',
    'popover-foreground': 'oklch(0.10 0.02 260)',
    primary: 'oklch(0.50 0.19 195)',
    'primary-foreground': 'oklch(0.98 0.005 195)',
    secondary: 'oklch(0.96 0.006 260)',
    'secondary-foreground': 'oklch(0.10 0.02 260)',
    muted: 'oklch(0.97 0.006 260)',
    'muted-foreground': 'oklch(0.40 0.015 260)',
    accent: 'oklch(0.96 0.006 260)',
    'accent-foreground': 'oklch(0.10 0.02 260)',
    destructive: 'oklch(0.55 0.26 25)',
    'destructive-foreground': 'oklch(0.98 0.005 25)',
    success: 'oklch(0.50 0.15 155)',
    'success-foreground': 'oklch(0.98 0.005 155)',
    warning: 'oklch(0.68 0.18 80)',
    'warning-foreground': 'oklch(0.15 0.02 80)',
    border: 'oklch(0.93 0.006 260)',
    input: 'oklch(0.93 0.006 260)',
    'field-border': 'oklch(0.58 0.015 260)',
    ring: 'oklch(0.50 0.19 195)',
    'chart-1': 'oklch(0.58 0.14 195)',
    'chart-2': 'oklch(0.55 0.22 300)',
    'chart-3': 'oklch(0.58 0.15 165)',
    'chart-4': 'oklch(0.62 0.17 55)',
    'chart-5': 'oklch(0.58 0.22 340)',
    sidebar: 'oklch(0.98 0.003 260)',
    'sidebar-foreground': 'oklch(0.10 0.02 260)',
    'sidebar-primary': 'oklch(0.50 0.19 195)',
    'sidebar-primary-foreground': 'oklch(0.98 0.005 195)',
    'sidebar-accent': 'oklch(0.95 0.006 260)',
    'sidebar-accent-foreground': 'oklch(0.10 0.02 260)',
    'sidebar-border': 'oklch(0.93 0.006 260)',
    'sidebar-ring': 'oklch(0.50 0.19 195)',
  },
}

export const RADIUS = '0.75rem'

/** Text on background pairs that must meet WCAG AA (4.5:1) in both themes. */
export const TEXT_PAIRS: readonly (readonly [fg: TokenName, bg: TokenName])[] = [
  ['foreground', 'background'],
  ['foreground', 'card'],
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
  ['destructive', 'background'],
  ['destructive', 'card'],
  ['success', 'background'],
  ['success', 'card'],
  ['sidebar-foreground', 'sidebar'],
  ['sidebar-accent-foreground', 'sidebar-accent'],
  ['sidebar-primary-foreground', 'sidebar-primary'],
]

/** Non-text UI pairs that must meet WCAG AA (3:1): form-field outlines, focus rings, the active nav icon. */
export const NON_TEXT_PAIRS: readonly (readonly [fg: TokenName, bg: TokenName])[] = [
  ['sidebar-primary', 'sidebar-accent'],
  ['field-border', 'background'],
  ['field-border', 'card'],
  ['field-border', 'muted'],
  ['ring', 'background'],
  ['ring', 'card'],
]
