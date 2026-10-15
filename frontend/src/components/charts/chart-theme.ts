/**
 * Shared recharts theme (SPEC §7.1 Charts). Colours are CSS variables, so charts follow the active theme without
 * re-rendering: no axis lines, a dashed horizontal grid, a frosted tooltip (chart-tooltip.tsx) and 260 px height.
 */

export const cssVar = (token: string) => `var(--${token})`

/** Series palette in order: cyan, violet, emerald, amber, rose. */
export const CHART_COLORS = [cssVar('chart-1'), cssVar('chart-2'), cssVar('chart-3'), cssVar('chart-4'), cssVar('chart-5')] as const

export function seriesColor(index: number): string {
  return CHART_COLORS[index % CHART_COLORS.length] ?? cssVar('chart-1')
}

export const chartTheme = {
  colors: CHART_COLORS,
  height: 260,
  margin: { top: 8, right: 8, bottom: 0, left: 0 },
  /** Spread onto <XAxis> / <YAxis>. */
  axis: {
    stroke: cssVar('muted-foreground'),
    tick: { fill: cssVar('muted-foreground'), fontSize: 12 },
    tickLine: false,
    axisLine: false,
    tickMargin: 8,
  },
  yAxisWidth: 48,
  /** Spread onto <CartesianGrid>. */
  grid: { stroke: cssVar('border'), strokeDasharray: '3 3', vertical: false },
  cursor: { fill: cssVar('muted'), opacity: 0.6 },
  lineCursor: { stroke: cssVar('muted-foreground'), strokeDasharray: '3 3' },
  referenceLine: { stroke: cssVar('muted-foreground'), strokeDasharray: '4 4' },
  legend: { fontSize: 12, color: cssVar('muted-foreground') },
  animationDuration: 400,
} as const
