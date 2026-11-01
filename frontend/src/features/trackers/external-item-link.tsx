import { ExternalLink } from 'lucide-react'

import { safeExternalUrl } from './tracker-picks'

/** An item's key, linked to the tracker in a new tab (SPEC §7). Plain text when the URL isn't http(s). */
export function ExternalItemLink({ url, itemKey, tracker }: { url: string; itemKey: string; tracker: string }) {
  const href = safeExternalUrl(url)
  if (!href) {
    return <span className="font-mono text-xs break-all">{itemKey}</span>
  }
  return (
    <a
      href={href}
      target="_blank"
      rel="noopener noreferrer"
      className="inline-flex min-h-6 items-center gap-1 font-mono text-xs break-all text-primary underline underline-offset-2"
    >
      {itemKey}
      <ExternalLink aria-hidden className="size-3 shrink-0" />
      <span className="sr-only">{` (opens ${tracker} in a new tab)`}</span>
    </a>
  )
}
