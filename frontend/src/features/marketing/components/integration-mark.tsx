import { Bell, Mail, MessageSquare, Users, Webhook, type LucideIcon } from 'lucide-react'

import { cn } from '../../../lib/utils'
import { brandIcons } from '../content/brand-icons'
import type { GenericIcon, IntegrationIcon } from '../content/integrations'

const genericIcons: Record<GenericIcon, LucideIcon> = {
  slack: MessageSquare,
  teams: Users,
  email: Mail,
  webhook: Webhook,
  'in-app': Bell,
}

/**
 * FR-222: a monochrome mark beside an integration's name. Brand marks are Simple Icons path data in `currentColor`;
 * brands without one get a neutral icon. Always decorative: the name is the text next to it.
 */
export function IntegrationMark({ icon, className }: { icon: IntegrationIcon; className?: string }) {
  const classes = cn('size-5 shrink-0', className)
  if ('generic' in icon) {
    const Icon = genericIcons[icon.generic]
    return <Icon aria-hidden="true" focusable="false" className={classes} />
  }
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false" className={classes} fill="currentColor">
      <path d={brandIcons[icon.brand]} />
    </svg>
  )
}
