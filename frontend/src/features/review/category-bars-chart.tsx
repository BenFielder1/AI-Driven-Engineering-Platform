import { useId } from 'react'
import { Bar, BarChart, CartesianGrid, Tooltip, XAxis, YAxis } from 'recharts'

import { ChartTooltip } from '../../components/charts/chart-tooltip'
import { chartTheme, cssVar } from '../../components/charts/chart-theme'
import { useChartWidth } from '../../components/charts/use-chart-width'
import { ChartCard, type ChartTableData } from '../../components/shared/chart-card'
import { formatPercent, reviewCategoryLabels } from '../projects/labels'
import type { CategoryScore } from './review-api'

interface Row {
  label: string
  /** The final score (agent score less the deterministic penalty). */
  final: number
  /** The penalty, drawn as an overlay at the end of the agent score's bar. */
  penalty: number
}

/**
 * FR-56, SPEC 7.1: the category breakdown as horizontal bars: the bar's full length is the review agent's score, its
 * last segment the deterministic penalty (so the solid part is the final score). The chart is a described image; "View
 * as table" shows weight, agent score, penalty and final score in words.
 */
export function CategoryBarsChart({ scores }: { scores: readonly CategoryScore[] }) {
  const titleId = useId()
  const descId = useId()
  const { setElement, width } = useChartWidth()
  const rows: Row[] = scores.map((s) => ({
    label: reviewCategoryLabels[s.category],
    final: s.score,
    penalty: Math.max(0, s.agentScore - s.score),
  }))
  const height = Math.max(160, rows.length * 36 + 24)
  const description = scores
    .map(
      (s) =>
        `${reviewCategoryLabels[s.category]}: agent ${formatPercent(s.agentScore)}${s.penalty > 0 ? `, penalty ${formatPercent(s.penalty)}` : ''}, final ${formatPercent(s.score)}`,
    )
    .join('; ')
  const table: ChartTableData = {
    caption: 'Score per category',
    columns: [
      { key: 'category', header: 'Category' },
      { key: 'weight', header: 'Weight', align: 'right' },
      { key: 'agent', header: 'Agent score', align: 'right' },
      { key: 'penalty', header: 'Penalty', align: 'right' },
      { key: 'final', header: 'Final score', align: 'right' },
    ],
    rows: scores.map((s) => ({
      category: reviewCategoryLabels[s.category],
      weight: formatPercent(s.weight),
      agent: formatPercent(s.agentScore),
      penalty: s.penalty > 0 ? `−${formatPercent(s.penalty)}` : 'None',
      final: formatPercent(s.score),
    })),
  }
  return (
    <ChartCard
      title="Category scores"
      description="Agent score per category; the hatched end of a bar is the deterministic penalty."
      table={table}
      headingLevel={3}
      className="bg-transparent"
    >
      <div role="img" aria-labelledby={`${titleId} ${descId}`} data-testid="category-bars-chart" className="min-w-0">
        <span id={titleId} className="sr-only">
          Category scores
        </span>
        <span id={descId} className="sr-only">
          {description}.
        </span>
        <div ref={setElement} aria-hidden className="w-full overflow-hidden" style={{ height }}>
          <BarChart
            width={width}
            height={height}
            data={rows}
            layout="vertical"
            margin={{ ...chartTheme.margin, right: 16 }}
            accessibilityLayer={false}
          >
            <defs>
              <pattern id={`${titleId}-hatch`} width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
                <rect width="6" height="6" fill={cssVar('destructive')} opacity={0.35} />
                <line x1="0" y1="0" x2="0" y2="6" stroke={cssVar('destructive')} strokeWidth={3} />
              </pattern>
            </defs>
            <CartesianGrid {...chartTheme.grid} horizontal={false} vertical />
            <XAxis type="number" domain={[0, 1]} ticks={[0, 0.25, 0.5, 0.75, 1]} tickFormatter={(v: number) => formatPercent(v)} {...chartTheme.axis} />
            <YAxis type="category" dataKey="label" width={120} {...chartTheme.axis} />
            <Tooltip cursor={chartTheme.cursor} content={<ChartTooltip valueFormatter={formatPercent} />} isAnimationActive={false} />
            <Bar name="Final score" dataKey="final" stackId="score" fill={cssVar('chart-1')} isAnimationActive={false} />
            <Bar
              name="Penalty"
              dataKey="penalty"
              stackId="score"
              fill={`url(#${titleId}-hatch)`}
              radius={[0, 4, 4, 0]}
              isAnimationActive={false}
            />
          </BarChart>
        </div>
      </div>
    </ChartCard>
  )
}
