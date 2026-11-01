import type { Schemas } from '../../api/client'
import type { LlmProviderOption } from '../connections/connections-api'
import { agentKeys, llmProviderLabels } from './labels'
import { wizardAgentModelFields, type WizardValues } from './schemas'

/** FR-159: the AI model section's field ids (error-summary link targets). */
export const llmFieldIds = {
  llmProvider: 'project-llm-provider',
  llmCredentialId: 'project-llm-connection',
  llmDefaultModel: 'project-llm-default-model',
  llmModelIdeaRefinement: 'project-llm-model-ideaRefinement',
  llmModelSpecGenerator: 'project-llm-model-specGenerator',
  llmModelPlanGenerator: 'project-llm-model-planGenerator',
  llmModelCodeGenerator: 'project-llm-model-codeGenerator',
  llmModelCodeReview: 'project-llm-model-codeReview',
} as const

type LlmField = keyof typeof llmFieldIds

export const llmFields = Object.keys(llmFieldIds) as LlmField[]

/**
 * FR-159: the connection picker shows when the provider needs a key that the server doesn't supply (not `system_key`,
 * not `keyless`) and the user has connections for it.
 */
export const showsConnectionPicker = (p: LlmProviderOption) =>
  p.connections.length > 0 && p.reason !== 'system_key' && p.reason !== 'keyless'

/**
 * FR-159, SPEC §7: the preselected provider is the one the server marks `isDefault` (its DefaultForNewProject rule for
 * this user). An unusable provider is never preselected: `undefined` when none is the usable default, so the user chooses.
 */
export function defaultLlmProvider(providers: readonly LlmProviderOption[]): LlmProviderOption | undefined {
  return providers.find((p) => p.isDefault && p.usable)
}

/** The model a provider starts with: its recommended catalog model, else its system default model. */
export const initialModel = (p: LlmProviderOption) => p.catalog.find((m) => m.recommended)?.id ?? p.defaultModel

/** The section's values after choosing `p`: its initial model, its first connection when the picker shows, no overrides. */
export function llmValuesFor(p: LlmProviderOption): Pick<WizardValues, LlmField> {
  return {
    llmProvider: p.provider,
    llmCredentialId: showsConnectionPicker(p) ? (p.connections[0]?.id ?? '') : '',
    llmDefaultModel: initialModel(p),
    llmModelIdeaRefinement: '',
    llmModelSpecGenerator: '',
    llmModelPlanGenerator: '',
    llmModelCodeGenerator: '',
    llmModelCodeReview: '',
  }
}

/** Why a provider can or can't be used, as the radio's description says it. */
export function providerReasonText(p: LlmProviderOption): string {
  const name = llmProviderLabels[p.provider]
  switch (p.reason) {
    case 'system_key':
      return `Uses this server's ${name} key.`
    case 'own_connection':
      return p.connections.length === 1 ? 'Uses your connection.' : `Uses one of your ${String(p.connections.length)} connections.`
    case 'keyless':
      return 'No key needed.'
    case 'needs_connection':
      return `Needs a connection: add your ${name} API key in Connections to use it.`
  }
}

/** FR-159: the `llm` of the create request (credential only when one was picked, overrides only when set). */
export function toLlmRequest(v: WizardValues): Schemas['CreateProjectLlmRequest'] | null {
  if (!v.llmProvider) {
    return null
  }
  const modelsByAgent: Schemas['AgentModelsDto'] = {}
  let overridden = false
  for (const agent of agentKeys) {
    const model = v[wizardAgentModelFields[agent]].trim()
    if (model) {
      modelsByAgent[agent] = model
      overridden = true
    }
  }
  return {
    provider: v.llmProvider,
    defaultModel: v.llmDefaultModel.trim(),
    ...(v.llmCredentialId ? { credentialId: v.llmCredentialId } : {}),
    ...(overridden ? { modelsByAgent } : {}),
  }
}
