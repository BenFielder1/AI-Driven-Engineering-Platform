import { useId } from 'react'
import { CartesianGrid, Line, LineChart, ReferenceLine, Tooltip, XAxis, YAxis } from 'recharts'

import { ChartTooltip } from '../../components/charts/chart-tooltip'
import { chartTheme, cssVar } from '../../components/charts/chart-theme'
import { useChartWidth } from '../../components/charts/use-chart-width'
import { ChartCard, type ChartTableData } from '../../components/shared/chart-card'
import { driftLevelLabels, formatPercent, reviewDecisionLabels } from '../projects/labels'
import { reportKey } from './report-key'
import type { ComplianceTrend, ComplianceTrendPoint } from './review-api'

type Decision = NonNullable<ComplianceTrendPoint['decision']>

/** SPEC 7.1: points are coloured by the review decision (the legend and the table say it in words too). */
const decisionColor: Record<Decision, string> = {
  release_eligible: cssVar('success'),
  review_required: cssVar('warning'),
  escalation_required: cssVar('destructive'),
}
const noDecisionColor = cssVar('chart-1')

/** A stable key per trend point: cycle numbers restart after a rollback, so the cycle alone can repeat. */
const pointKey = reportKey

interface Point extends ComplianceTrendPoint {
  key: string
  label: string
  meets: boolean
}

interface DotProps {
  cx?: number
  cy?: number
  payload?: Point
}

/** A point coloured by decision; the selected cycle's point is larger and ringed (FR-56). Clicking selects it. */
function DecisionDot({
  cx,
  cy,
  payload,
  selected,
  onSelect,
}: DotProps & { selected: boolean; onSelect?: (point: ComplianceTrendPoint) => void }) {
  if (cx === undefined || cy === undefined || !payload) {
    return null
  }
  return (
    <circle
      data-cycle={payload.cycle}
      data-meets={payload.meets}
      data-decision={payload.decision ?? 'none'}
      data-selected={selected}
      cx={cx}
      cy={cy}
      r={selected ? 8 : 5}
      fill={payload.decision ? decisionColor[payload.decision] : noDecisionColor}
      stroke={selected ? cssVar('foreground') : cssVar('background')}
      strokeWidth={selected ? 3 : 2}
      style={onSelect ? { cursor: 'pointer' } : undefined}
      onClick={
        onSelect
          ? () => {
              onSelect(payload)
            }
          : undefined
      }
    />
  )
}

/**
 * FR-41 compliance trend on recharts (SPEC 7.1): the overall score per review cycle, a threshold line and points
 * coloured by decision. Each point is judged against the threshold its report was decided with: a constant threshold
 * is a dashed reference line, a changing one a dashed step line. The chart is a described image (its text names every
 * point); "View as table" swaps in the same data, threshold, drift and decision included.
 */
export function ComplianceTrendChart({
  trend,
  headingLevel = 3,
  selectedKey = null,
  onSelectPoint,
}: {
  trend: ComplianceTrend
  headingLevel?: 2 | 3
  /** FR-56: the report on screen (its `reportKey`), highlighted. */
  selectedKey?: string | null
  /** FR-56: clicking a point selects its cycle's report (the cycles table is the keyboard path). */
  onSelectPoint?: (point: ComplianceTrendPoint) => void
}) {
  const titleId = useId()
  const descId = useId()
  const { setElement, width } = useChartWidth()
  const { series } = trend
  const points: Point[] = series.map((p) => ({
    ...p,
    key: pointKey(p),
    label: `Cycle ${String(p.cycle)}`,
    meets: p.overallScore >= p.threshold,
  }))
  const varies = new Set(series.map((p) => p.threshold)).size > 1
  // With one threshold across the series (or none yet), that is the line; it falls back to the project's current one.
  const threshold = series[0]?.threshold ?? trend.threshold
  const description = varies
    ? `${series
        .map((p) => `Cycle ${String(p.cycle)}: ${formatPercent(p.overallScore)} against a ${formatPercent(p.threshold)} threshold`)
        .join(', ')}.`
    : `${series.map((p) => `Cycle ${String(p.cycle)}: ${formatPercent(p.overallScore)}`).join(', ')}. Threshold ${formatPercent(threshold)}.`
  const selectedPoint = selectedKey === null ? undefined : points.find((p) => p.key === selectedKey)
  const selectedText = selectedPoint ? ` Cycle ${String(selectedPoint.cycle)} is selected.` : ''

  const table: ChartTableData = {
    caption: 'Compliance score by review cycle, oldest first',
    columns: [
      { key: 'cycle', header: 'Cycle' },
      { key: 'version', header: 'Checkpoint' },
      { key: 'score', header: 'Score' },
      { key: 'threshold', header: 'Threshold' },
      { key: 'drift', header: 'Drift' },
      { key: 'decision', header: 'Decision' },
    ],
    rows: points.map((p) => ({
      cycle: p.cycle,
      version: String(p.checkpoint),
      score: (
        <>
          {formatPercent(p.overallScore)}{' '}
          <span className="text-muted-foreground">({p.meets ? 'meets threshold' : 'below threshold'})</span>
        </>
      ),
      threshold: formatPercent(p.threshold),
      drift: p.driftLevel ? driftLevelLabels[p.driftLevel] : '—',
      decision: p.decision ? reviewDecisionLabels[p.decision] : '—',
    })),
  }

  const legend: { label: string; color: string }[] = [
    { label: reviewDecisionLabels.release_eligible, color: decisionColor.release_eligible },
    { label: reviewDecisionLabels.review_required, color: decisionColor.review_required },
    { label: reviewDecisionLabels.escalation_required, color: decisionColor.escalation_required },
  ]

  return (
    <ChartCard
      title="Compliance trend"
      description="Overall score per review cycle against the threshold."
      table={table}
      headingLevel={headingLevel}
      className="bg-transparent"
    >
      <div role="img" aria-labelledby={`${titleId} ${descId}`} data-testid="compliance-trend-chart" className="min-w-0">
        <span id={titleId} className="sr-only">
          Compliance score by review cycle
        </span>
        <span id={descId} className="sr-only">
          {description}
          {selectedText}
        </span>
        <div ref={setElement} aria-hidden className="w-full overflow-hidden" style={{ height: chartTheme.height }}>
          <LineChart
            width={width}
            height={chartTheme.height}
            data={points}
            margin={{ ...chartTheme.margin, right: 16 }}
            accessibilityLayer={false}
          >
              <CartesianGrid {...chartTheme.grid} />
              <XAxis dataKey="label" {...chartTheme.axis} allowDuplicatedCategory />
              <YAxis
                domain={[0, 1]}
                ticks={[0, 0.25, 0.5, 0.75, 1]}
                tickFormatter={(v: number) => formatPercent(v)}
                width={chartTheme.yAxisWidth}
                {...chartTheme.axis}
              />
              <Tooltip
                cursor={chartTheme.lineCursor}
                content={<ChartTooltip valueFormatter={formatPercent} />}
                isAnimationActive={false}
              />
              {varies ? (
                <Line
                  data-testid="threshold-steps"
                  name="Threshold"
                  dataKey="threshold"
                  type="stepAfter"
                  stroke={cssVar('warning')}
                  strokeWidth={2}
                  strokeDasharray="6 4"
                  dot={false}
                  activeDot={false}
                  isAnimationActive={false}
                />
              ) : (
                <ReferenceLine
                  y={threshold}
                  stroke={cssVar('warning')}
                  strokeWidth={2}
                  strokeDasharray="6 4"
                  label={{
                    value: `Threshold ${formatPercent(threshold)}`,
                    position: 'insideTopRight',
                    fill: cssVar('foreground'),
                    fontSize: 11,
                  }}
                />
              )}
              <Line
                name="Score"
                dataKey="overallScore"
                type="monotone"
                stroke={cssVar('chart-1')}
                strokeWidth={2}
                dot={(props) => {
                const dot = props as DotProps
                return (
                  <DecisionDot
                    key={dot.payload?.key}
                    {...dot}
                    selected={dot.payload?.key === selectedKey}
                    onSelect={onSelectPoint}
                  />
                )
              }}
                activeDot={{ r: 6 }}
                isAnimationActive={false}
              />
          </LineChart>
        </div>
      </div>
      <ul aria-label="Decision colours" className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
        {legend.map((l) => (
          <li key={l.label} className="flex items-center gap-1.5">
            <span aria-hidden className="size-2.5 rounded-full" style={{ backgroundColor: l.color }} />
            {l.label}
          </li>
        ))}
        <li className="flex items-center gap-1.5">
          <span aria-hidden className="h-0 w-4 border-t-2 border-dashed border-warning" />
          Threshold{varies ? ' (varies by cycle)' : ''}
        </li>
      </ul>
    </ChartCard>
  )
}
