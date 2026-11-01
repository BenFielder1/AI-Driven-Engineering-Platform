import { wizardDefaults, wizardShape, type WizardValues } from './schemas'
import { hostSecretFields } from './wizard-host-fields'

/**
 * FR-159, SPEC §7 UI rules: "Add a connection" opens Connections in the same tab and the wizard comes back with its
 * state, kept in session storage (this tab only, cleared when the tab closes). The Git access token and External
 * connection strings are never stored.
 */
export const WIZARD_DRAFT_KEY = 'aide.new-project-wizard.draft'

export interface WizardDraft {
  values: WizardValues
  step: number
}

function storage(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.sessionStorage
  } catch {
    // Storage blocked (privacy settings): the wizard just starts afresh.
    return null
  }
}

function read(): string | null {
  try {
    return storage()?.getItem(WIZARD_DRAFT_KEY) ?? null
  } catch {
    return null
  }
}

const withoutSecrets = (values: WizardValues): WizardValues => ({
  ...values,
  gitToken: '',
  ...Object.fromEntries(hostSecretFields.map((f) => [f, ''])),
})

/** Saves the wizard's values (without the access token or connection strings) and step. */
export function saveWizardDraft(values: WizardValues, step: number): void {
  try {
    storage()?.setItem(WIZARD_DRAFT_KEY, JSON.stringify({ values: withoutSecrets(values), step }))
  } catch {
    // Quota or blocked storage: nothing is kept.
  }
}

/** Whether a saved wizard is waiting (Connections offers the way back). */
export const hasWizardDraft = (): boolean => read() !== null

/** Removes the saved wizard (once the wizard has restored it). */
export function clearWizardDraft(): void {
  try {
    storage()?.removeItem(WIZARD_DRAFT_KEY)
  } catch {
    // Blocked storage: nothing to remove.
  }
}

/** The saved wizard, or `null` when there is none or it doesn't parse. Doesn't remove it. */
export function readWizardDraft(): WizardDraft | null {
  const raw = read()
  if (!raw) {
    return null
  }
  try {
    const parsed = JSON.parse(raw) as { values?: unknown; step?: unknown }
    const stored = typeof parsed.values === 'object' && parsed.values !== null ? parsed.values : {}
    const values = wizardShape.safeParse({ ...wizardDefaults, ...stored })
    const step = typeof parsed.step === 'number' && Number.isInteger(parsed.step) && parsed.step >= 0 ? parsed.step : 0
    return values.success ? { values: withoutSecrets(values.data), step } : null
  } catch {
    return null
  }
}
