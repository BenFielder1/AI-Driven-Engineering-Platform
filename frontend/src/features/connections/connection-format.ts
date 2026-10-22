import type { Schemas } from '../../api/client'
import { llmProviderLabels } from '../projects/labels'

export const credentialStatusLabels: Record<Schemas['LlmCredentialStatus'], string> = {
  valid: 'Valid',
  invalid: 'Invalid',
  unchecked: 'Not checked',
}

/** Whether an ISO time is in the past (token expiry, FR-87). */
export const isPast = (iso: string, now: number = Date.now()) => Date.parse(iso) <= now

/** "••••a1b2": only the last 4 characters of a key are ever known to the UI. */
export const maskedKey = (last4: string) => `••••${last4}`

/** "Work key (••••a1b2)", the same shape as AgentRunDto.credentialLabel. */
export const connectionLabel = (c: { label: string; last4: string }) => `${c.label} (${maskedKey(c.last4)})`

/** FR-79: "Anthropic · connected by Ana (••••a1b2)". */
export const bindingSummary = (b: Schemas['LlmBindingDto']) =>
  `${llmProviderLabels[b.provider]} · connected by ${b.boundBy.name} (${maskedKey(b.last4)})`

/** "Team acme" or "Personal account (ada)" (FR-87 scope). */
export function vercelScopeLabel(c: Pick<Schemas['VercelConnectionDto'], 'scope' | 'teamSlug' | 'accountName'>): string {
  if (c.scope === 'team') {
    return `Team ${c.teamSlug ?? 'unknown'}${c.accountName ? ` · account ${c.accountName}` : ''}`
  }
  return c.accountName ? `Personal account (${c.accountName})` : 'Personal account'
}
