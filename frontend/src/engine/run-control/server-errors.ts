import type { SummaryError } from '../../components/ui/alert'
import { errorMessage, isApiError, type ErrorContext } from '../../lib/errors'

/**
 * A failed start or submit for the dialog's error summary: the shared copy for its code (lib/errors), then for
 * INPUT_INVALID the failing paths the API listed in `details`.
 */
export function runControlErrors(error: unknown, context: ErrorContext): SummaryError[] {
  const entries: SummaryError[] = [{ message: errorMessage(error, context) }]
  if (isApiError(error, 'INPUT_INVALID')) {
    entries.push(...error.details.map((message) => ({ message })))
  }
  return entries
}

/** The approval modes by name (a definition lists the ones a run may choose, FR-331). */
export const approvalModeLabels = {
  strict: 'Strict',
  advisory: 'Advisory',
  autonomous: 'Autonomous',
} as const
