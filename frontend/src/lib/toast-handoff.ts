import { toast } from 'sonner'

/**
 * Toasts that must survive a move between the `(public)` and `(app)` route groups (v4 FR-131/FR-134: "account linked"
 * on /login, then the page navigates into the app). Each group's layout mounts its own `Providers` and so its own
 * `<Toaster/>` (marketing routes load neither, SPEC §8), so a toast raised just before the switch could be lost with the
 * unmounting Toaster. The toast is shown right away and also kept here for a few seconds; the next Toaster to mount
 * replays it under the same id, which sonner treats as an update, so it never shows twice. A toast the user closed or
 * that timed out isn't replayed.
 */
export type HandoffToastKind = 'success' | 'info' | 'error'

interface PendingToast {
  id: string
  kind: HandoffToastKind
  message: string
  raisedAt: number
}

/** How long a handed-off toast stays eligible for replay: sonner's default display time. */
export const HANDOFF_TTL_MS = 4000

let pending: PendingToast[] = []
let counter = 0

function forget(id: string): void {
  pending = pending.filter((entry) => entry.id !== id)
}

function show({ id, kind, message }: PendingToast): void {
  // Once closed or timed out on screen there's nothing left to hand over.
  toast[kind](message, {
    id,
    onDismiss: () => {
      forget(id)
    },
    onAutoClose: () => {
      forget(id)
    },
  })
}

/** Shows a toast now and replays it in the next Toaster that mounts within `HANDOFF_TTL_MS` (e.g. after a group switch). */
export function toastAcrossNavigation(kind: HandoffToastKind, message: string): void {
  counter += 1
  const entry: PendingToast = { id: `handoff-${String(counter)}`, kind, message, raisedAt: Date.now() }
  pending = [...pending, entry]
  show(entry)
}

/** Called by the Toaster wrapper on mount: re-raises recent handed-off toasts, then forgets them. */
export function replayHandedOffToasts(): void {
  const now = Date.now()
  const recent = pending.filter((entry) => now - entry.raisedAt < HANDOFF_TTL_MS)
  pending = []
  recent.forEach(show)
}
