import { useId } from 'react'
import { Area, AreaChart, ResponsiveContainer, YAxis } from 'recharts'

import { cn } from '../../lib/utils'
import { cssVar } from '../charts/chart-theme'

/** A decorative trend line for metric cards (never animated, hidden from assistive tech). */
export function Sparkline({
  data,
  color = cssVar('chart-1'),
  height = 40,
  className,
}: {
  data: readonly number[]
  color?: string
  height?: number
  className?: string
}) {
  const id = `spark-${useId().replace(/[^a-zA-Z0-9_-]/g, '')}`
  if (data.length < 2) {
    return null
  }
  const points = data.map((v, i) => ({ i, v }))
  return (
    <div aria-hidden data-testid="sparkline" className={cn('w-full', className)} style={{ height }}>
      <ResponsiveContainer width="100%" height="100%" initialDimension={{ width: 200, height }}>
        <AreaChart data={points} margin={{ top: 2, right: 0, bottom: 2, left: 0 }} accessibilityLayer={false}>
          <defs>
            <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={color} stopOpacity={0.3} />
              <stop offset="100%" stopColor={color} stopOpacity={0} />
            </linearGradient>
          </defs>
          <YAxis hide domain={['dataMin', 'dataMax']} />
          <Area type="monotone" dataKey="v" stroke={color} strokeWidth={1.5} fill={`url(#${id})`} isAnimationActive={false} dot={false} />
        </AreaChart>
      </ResponsiveContainer>
    </div>
  )
}
