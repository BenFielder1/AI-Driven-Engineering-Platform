import type { Schemas } from '../../api/client'
import { toolCallErrorMessage } from '../../lib/errors'
import { formatNumber } from '../projects/labels'

type ToolCall = Schemas['ToolCallDto']

/** A tool call's arguments as compact JSON, cut for display (the full value is in the title). */
function argumentsText(value: unknown): string {
  if (value === undefined) {
    return ''
  }
  let text: string
  try {
    text = JSON.stringify(value)
  } catch {
    text = '(arguments could not be shown)'
  }
  return text.length > 160 ? `${text.slice(0, 159)}…` : text
}

function resultText(call: ToolCall): string {
  if (call.error) {
    return toolCallErrorMessage(call.error)
  }
  return call.truncated ? 'Cut to the tool limit' : 'Complete'
}

/** FR-71: a run's repository tool calls: tool, arguments, bytes returned and the result (complete, cut or refused). */
export function ToolCallsTable({ calls, caption }: { calls: readonly ToolCall[]; caption: string }) {
  if (calls.length === 0) {
    return <p className="text-xs text-muted-foreground">No repository tool calls.</p>
  }
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[28rem] border-collapse text-xs">
        <caption className="sr-only">{caption}</caption>
        <thead>
          <tr className="border-b border-border/60 text-left text-muted-foreground">
            <th scope="col" className="py-1 pr-2 font-medium">Tool</th>
            <th scope="col" className="py-1 pr-2 font-medium">Arguments</th>
            <th scope="col" className="py-1 pr-2 text-right font-medium">Bytes</th>
            <th scope="col" className="py-1 font-medium">Result</th>
          </tr>
        </thead>
        <tbody>
          {calls.map((c, i) => {
            const args = argumentsText(c.arguments)
            return (
              <tr key={`${c.tool}-${String(i)}`} className="border-b border-border/40 align-top last:border-b-0">
                <th scope="row" className="py-1 pr-2 text-left font-normal">
                  <code>{c.tool}</code>
                </th>
                <td className="py-1 pr-2 break-all">
                  <code title={args}>{args}</code>
                </td>
                <td className="py-1 pr-2 text-right tabular-nums">{formatNumber(c.bytes)}</td>
                <td className={c.error ? 'py-1 text-destructive' : 'py-1'}>{resultText(c)}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
