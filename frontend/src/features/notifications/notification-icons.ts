import {
  AlarmClock,
  Ban,
  CircleCheck,
  CircleX,
  Clock,
  CloudAlert,
  Eye,
  FileText,
  Gauge,
  GitMerge,
  KeyRound,
  OctagonAlert,
  Rocket,
  ShieldCheck,
  Undo2,
  Unlink,
  Unplug,
  type LucideIcon,
} from 'lucide-react'

import type { Schemas } from '../../api/client'

/**
 * FR-47, FR-92: a decorative icon per notification type. Always paired with the type's text label
 * (`notificationTypeLabels`), so the icon never carries meaning on its own.
 */
export const notificationTypeIcons: Record<Schemas['NotificationType'], LucideIcon> = {
  gate_required: ShieldCheck,
  gate_reminder: Clock,
  gate_overdue: AlarmClock,
  artifact_ready: FileText,
  step_blocked: Ban,
  run_failed: CircleX,
  budget_exceeded: Gauge,
  review_completed: CircleCheck,
  release_completed: Rocket,
  release_failed: OctagonAlert,
  release_awaiting_merge: GitMerge,
  rollback: Undo2,
  deployment_failed: CloudAlert,
  preview_ready: Eye,
  connection_invalid: Unplug,
  llm_binding_cleared: KeyRound,
  git_binding_cleared: Unlink,
}
