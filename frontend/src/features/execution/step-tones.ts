import type { BadgeTone } from '../../components/ui/badge'
import type { StepStatus } from './execution-api'

/** Badge tone per step status (text carries the meaning; colour only reinforces it). */
export const stepTones: Record<StepStatus, BadgeTone> = {
  pending: 'neutral',
  in_progress: 'info',
  completed: 'success',
  rejected: 'danger',
  blocked: 'warning',
  invalidated: 'warning',
}
