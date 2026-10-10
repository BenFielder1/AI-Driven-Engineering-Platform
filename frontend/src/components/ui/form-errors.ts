import type { FieldErrors, FieldValues } from 'react-hook-form'

import type { SummaryError } from './alert'

/** Turns RHF field errors (client or server-set) into error-summary entries, in field order. */
export function fieldErrorSummary<T extends FieldValues>(
  errors: FieldErrors<T>,
  fieldIds: Partial<Record<keyof T & string, string>>,
): SummaryError[] {
  const entries: SummaryError[] = []
  for (const [field, id] of Object.entries(fieldIds) as [string, string][]) {
    const message = errors[field]?.message
    if (typeof message === 'string' && message) {
      entries.push({ fieldId: id, message })
    }
  }
  return entries
}
