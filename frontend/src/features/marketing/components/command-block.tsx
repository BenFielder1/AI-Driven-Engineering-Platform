import type { Command } from '../content/self-host-commands'
import { CopyButton } from './copy-button'

/**
 * FR-216, FR-212: a terminal-style block of commands (from self-host-commands.ts, the shared source) with each line's
 * note as a `#` comment and one button that copies the commands (without the notes).
 */
export function CommandBlock({ title, commands }: { title: string; commands: readonly Command[] }) {
  const text = commands.map((c) => c.command).join('\n')
  return (
    <div className="overflow-hidden rounded-xl border border-border bg-muted" data-testid="command-block">
      <div className="flex items-center justify-between gap-2 border-b border-border px-4 py-1.5">
        <span className="font-mono text-xs text-muted-foreground">{title}</span>
        <CopyButton text={text} label={`Copy the ${title} commands`} />
      </div>
      <pre className="overflow-x-auto p-4 text-sm leading-relaxed" tabIndex={0} role="region" aria-label={`${title} commands`}>
        <code>
          {commands.map((c) => (
            <span key={c.command} className="block">
              <span aria-hidden="true" className="text-muted-foreground select-none">
                ${' '}
              </span>
              {c.command}
              {c.note && <span className="block pl-4 text-muted-foreground"># {c.note}</span>}
            </span>
          ))}
        </code>
      </pre>
    </div>
  )
}
