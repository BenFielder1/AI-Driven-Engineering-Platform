import type { Schemas } from '../../api/client'
import { llmProviderLabels } from '../projects/labels'

/** LLM keys (FR-78) and platform connections (FR-142; only those can be `insufficient_scope`). */
export const credentialStatusLabels: Record<Schemas['LlmCredentialStatus'] | Schemas['PlatformConnectionStatus'], string> = {
  valid: 'Valid',
  invalid: 'Invalid',
  insufficient_scope: 'Missing permissions',
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
