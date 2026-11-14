/**
 * FR-235, FR-236/FR-237, FR-259: a multi-line log excerpt (build or CI output) in a scrolling monospace block that keeps
 * its lines and uses the app scrollbar. Focusable, so keyboard users can scroll it, and named by `label`.
 */
export function EvidenceBlock({ text, label, testId }: { text: string; label: string; testId?: string }) {
  return (
    <pre
      data-testid={testId}
      tabIndex={0}
      aria-label={label}
      className="max-h-64 max-w-full overflow-auto rounded-lg border border-border/60 bg-background px-3 py-2 font-mono text-xs whitespace-pre"
    >
      {text}
    </pre>
  )
}
