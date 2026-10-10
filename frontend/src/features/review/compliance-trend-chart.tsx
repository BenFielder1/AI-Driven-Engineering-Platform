import { useId } from 'react'

import { driftLevelLabels, formatPercent, reviewDecisionLabels } from '../projects/labels'
import type { ComplianceTrend } from './review-api'

const W = 480
const H = 200
const PAD = { top: 12, right: 16, bottom: 32, left: 44 }
const plotW = W - PAD.left - PAD.right
const plotH = H - PAD.top - PAD.bottom
const yTicks = [0, 0.25, 0.5, 0.75, 1]

const y = (score: number) => PAD.top + (1 - Math.min(Math.max(score, 0), 1)) * plotH
const x = (index: number, count: number) => PAD.left + (count <= 1 ? plotW / 2 : (index / (count - 1)) * plotW)

/** A stable key per trend point: cycle numbers restart after a rollback, so the cycle alone can repeat. */
const pointKey = (p: { projectVersion: number; cycle: number; createdAt: string }) =>
  `${String(p.projectVersion)}-${String(p.cycle)}-${p.createdAt}`

/**
 * FR-41: the overall compliance score per review cycle (one point each) against the threshold. Each point is judged
 * against the threshold its report was decided with (the project's threshold may have changed since): a constant
 * threshold is one dashed line, a changing one is a dashed step line. The SVG is an image with a title and a text
 * description; the table under it carries the same data, threshold included, for every reader. Colours come from the
 * theme tokens, so it follows light and dark mode; the threshold line is also dashed, so it doesn't rely on colour.
 */
export function ComplianceTrendChart({ trend }: { trend: ComplianceTrend }) {
  const titleId = useId()
  const descId = useId()
  const { series } = trend
  const n = series.length
  const points = series.map((p, i) => ({
    ...p,
    key: pointKey(p),
    meets: p.overallScore >= p.threshold,
    cx: x(i, n),
    cy: y(p.overallScore),
  }))
  const distinct = new Set(series.map((p) => p.threshold))
  const varies = distinct.size > 1
  // With one threshold across the series (or none yet), that is the line; it falls back to the project's current one.
  const threshold = series[0]?.threshold ?? trend.threshold
  const lastThreshold = series.at(-1)?.threshold ?? trend.threshold
  const description = varies
    ? `${series
        .map((p) => `Cycle ${String(p.cycle)}: ${formatPercent(p.overallScore)} against a ${formatPercent(p.threshold)} threshold`)
        .join(', ')}.`
    : `${series.map((p) => `Cycle ${String(p.cycle)}: ${formatPercent(p.overallScore)}`).join(', ')}. Threshold ${formatPercent(threshold)}.`
  // The step line: each point's threshold spans halfway to its neighbours.
  const stepPath = points
    .map((p, i) => {
      const start = i === 0 ? PAD.left : ((points[i - 1]?.cx ?? p.cx) + p.cx) / 2
      const end = i === n - 1 ? W - PAD.right : (p.cx + (points[i + 1]?.cx ?? p.cx)) / 2
      const ty = y(p.threshold)
      return `${i === 0 ? 'M' : 'L'}${String(start)},${String(ty)} H${String(end)}`
    })
    .join(' ')

  return (
    <div className="flex flex-col gap-3">
      <svg
        role="img"
        aria-labelledby={`${titleId} ${descId}`}
        viewBox={`0 0 ${String(W)} ${String(H)}`}
        className="h-auto w-full max-w-xl text-fg"
      >
        <title id={titleId}>Compliance score by review cycle</title>
        <desc id={descId}>{description}</desc>
        <g aria-hidden="true">
          {yTicks.map((t) => (
            <g key={t}>
              <line x1={PAD.left} x2={W - PAD.right} y1={y(t)} y2={y(t)} className="stroke-border" strokeWidth={1} />
              <text x={PAD.left - 6} y={y(t)} textAnchor="end" dominantBaseline="middle" className="fill-muted text-[11px]">
                {formatPercent(t)}
              </text>
            </g>
          ))}
          {points.map((p) => (
            <text key={p.key} x={p.cx} y={H - PAD.bottom + 16} textAnchor="middle" className="fill-muted text-[11px]">
              {`Cycle ${String(p.cycle)}`}
            </text>
          ))}
          {varies ? (
            <>
              <path
                data-testid="threshold-steps"
                d={stepPath}
                fill="none"
                className="stroke-warning"
                strokeWidth={2}
                strokeDasharray="6 4"
              />
              <text x={W - PAD.right} y={y(lastThreshold) - 6} textAnchor="end" className="fill-fg text-[11px]">
                {`Threshold ${formatPercent(lastThreshold)} (varies by cycle)`}
              </text>
            </>
          ) : (
            <>
              <line
                x1={PAD.left}
                x2={W - PAD.right}
                y1={y(threshold)}
                y2={y(threshold)}
                className="stroke-warning"
                strokeWidth={2}
                strokeDasharray="6 4"
              />
              <text x={W - PAD.right} y={y(threshold) - 6} textAnchor="end" className="fill-fg text-[11px]">
                {`Threshold ${formatPercent(threshold)}`}
              </text>
            </>
          )}
          {n > 1 && (
            <polyline
              points={points.map((p) => `${String(p.cx)},${String(p.cy)}`).join(' ')}
              fill="none"
              className="stroke-primary"
              strokeWidth={2}
            />
          )}
          {points.map((p) => (
            <circle
              key={p.key}
              data-cycle={p.cycle}
              data-meets={p.meets}
              cx={p.cx}
              cy={p.cy}
              r={5}
              className={p.meets ? 'fill-success stroke-fg' : 'fill-primary stroke-fg'}
              strokeWidth={1}
            />
          ))}
        </g>
      </svg>
      {/* NFR-A11Y: at 375 px the table may be wider than the page; it scrolls inside a keyboard-reachable region
          instead of the page scrolling horizontally. */}
      <div
        role="region"
        aria-label="Compliance score table"
        tabIndex={0}
        data-testid="trend-table-region"
        className="max-w-xl overflow-x-auto"
      >
        <table className="w-full text-left text-sm">
          <caption className="mb-1 text-left text-sm text-muted">Compliance score by review cycle, oldest first</caption>
          <thead>
            <tr className="border-b border-border">
              <th scope="col" className="py-1 pr-3 font-medium">Cycle</th>
              <th scope="col" className="py-1 pr-3 font-medium">Version</th>
              <th scope="col" className="py-1 pr-3 font-medium">Score</th>
              <th scope="col" className="py-1 pr-3 font-medium">Threshold</th>
              <th scope="col" className="py-1 pr-3 font-medium">Drift</th>
              <th scope="col" className="py-1 font-medium">Decision</th>
            </tr>
          </thead>
          <tbody>
            {points.map((p) => (
              <tr key={p.key} className="border-b border-border last:border-0">
                <th scope="row" className="py-1 pr-3 font-normal">
                  {p.cycle}
                </th>
                <td className="py-1 pr-3">v{p.projectVersion}</td>
                <td className="py-1 pr-3">
                  {formatPercent(p.overallScore)}{' '}
                  <span className="text-muted">({p.meets ? 'meets threshold' : 'below threshold'})</span>
                </td>
                <td className="py-1 pr-3">{formatPercent(p.threshold)}</td>
                <td className="py-1 pr-3">{p.driftLevel ? driftLevelLabels[p.driftLevel] : '—'}</td>
                <td className="py-1">{p.decision ? reviewDecisionLabels[p.decision] : '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
