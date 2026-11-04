import { Check, Copy } from 'lucide-react'
import { useEffect, useState } from 'react'

import { cn } from '../../../lib/utils'
import { copyFeedback } from '../content/headings'

type CopyState = 'idle' | 'copied' | 'failed'

/**
 * FR-216: copies `text` with `navigator.clipboard` (no third-party code). The result is announced in a polite live
 * region next to the button ("Copied" / "Couldn't copy, select the text instead") and resets after 2 seconds.
 */
export function CopyButton({ text, label, className }: { text: string; label: string; className?: string }) {
  const [state, setState] = useState<CopyState>('idle')

  useEffect(() => {
    if (state === 'idle') {
      return undefined
    }
    const timer = window.setTimeout(() => {
      setState('idle')
    }, 2000)
    return () => {
      window.clearTimeout(timer)
    }
  }, [state])

  async function copy() {
    try {
      await navigator.clipboard.writeText(text)
      setState('copied')
    } catch {
      setState('failed')
    }
  }

  return (
    <span className={cn('inline-flex items-center gap-2', className)}>
      <span role="status" aria-live="polite" className="text-xs text-muted-foreground">
        {state === 'copied' ? copyFeedback.copied : state === 'failed' ? copyFeedback.failed : ''}
      </span>
      <button
        type="button"
        onClick={() => {
          void copy()
        }}
        aria-label={label}
        className="inline-flex size-8 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-foreground"
      >
        {state === 'copied' ? <Check aria-hidden="true" className="size-4" /> : <Copy aria-hidden="true" className="size-4" />}
      </button>
    </span>
  )
}
