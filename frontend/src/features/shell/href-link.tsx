import type { ComponentProps, MouseEvent } from 'react'

import { useAppRouter } from '../../lib/navigation'

/**
 * A link to a concrete href built by nav-config (breadcrumbs, palette): client-side navigation on a plain click; the
 * browser handles modified clicks (new tab, etc.) itself.
 */
export function HrefLink({ href, onClick, ...props }: ComponentProps<'a'> & { href: string }) {
  const router = useAppRouter()
  function handleClick(event: MouseEvent<HTMLAnchorElement>) {
    onClick?.(event)
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
      return
    }
    event.preventDefault()
    void router.navigate({ href })
  }
  return <a href={href} onClick={handleClick} {...props} />
}
