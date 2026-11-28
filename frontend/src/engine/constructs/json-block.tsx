/** JSON text, pretty-printed when it parses (shown as stored otherwise). */
export function prettyJson(text: string): string {
  try {
    return JSON.stringify(JSON.parse(text), null, 2)
  } catch {
    return text
  }
}

/**
 * FR-364: a long JSON payload (tool arguments, an external action's payload), pretty-printed in a scrolling monospace
 * block like the artifact viewer's JSON. Focusable, so keyboard users can scroll it.
 */
export function JsonBlock({ label, text }: { label: string; text: string }) {
  return (
    <pre
      role="group"
      tabIndex={0}
      aria-label={label}
      className="max-h-[24rem] overflow-auto rounded-lg border border-border/60 bg-muted p-3 font-mono text-xs whitespace-pre-wrap break-words"
    >
      {prettyJson(text)}
    </pre>
  )
}
