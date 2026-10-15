import type { FieldValues, Path, UseFormSetError } from "react-hook-form";

/**
 * The ONE place that maps API `errorCode`s to user copy (CLAUDE.md rule 4). Mirrors
 * backend/src/Aide.Domain/Common/ErrorCodes.cs (endpoint codes and agent-run codes; Guardian codes are separate).
 */
export const errorMessages = {
  // Common
  VALIDATION: "Some fields need attention.",
  UNAUTHENTICATED: "Your session has ended. Sign in again to continue.",
  FORBIDDEN: "You don't have permission to do that.",
  NOT_FOUND:
    "We couldn't find that. It may have been deleted, or you may not have access.",
  CONFLICT: "Someone else changed this at the same time. Reload and try again.",
  ANTIFORGERY: "Your security token expired. Reload the page and try again.",
  // Accounts and members
  EMAIL_TAKEN: "An account with this email already exists. Sign in instead.",
  INVALID_CREDENTIALS: "The email or password is incorrect.",
  GITHUB_AUTH_FAILED: "GitHub sign-in didn't complete. Try again.",
  USER_NOT_FOUND: "No user with that email exists.",
  LAST_OWNER: "A project needs at least one Owner. Add another Owner first.",
  // Lifecycle, gates, artifacts
  PHASE_PRECONDITION_FAILED:
    "The project isn't ready to move to the next phase yet.",
  GATE_PENDING: "An approval gate is waiting for a decision.",
  BUDGET_EXCEEDED:
    "This project has used its token budget — an Owner can raise it in Settings.",
  WRONG_PHASE: "That isn't available in the project's current phase.",
  ARTIFACT_NOT_DRAFT:
    "This draft can no longer be approved or rejected: it was already decided, or it is out of date because the project moved on since it was created.",
  ARTIFACT_LOCKED:
    "This artifact version is locked. Create a new version instead.",
  GATE_NOT_PENDING: "This gate has already been decided.",
  INVALID_DECISION: "That decision isn't valid for this gate.",
  REFINEMENT_IN_PROGRESS:
    "A refinement round is already queued or running. Wait for it to finish, then answer its questions.",
  // Review, release, rollback
  REVIEW_IN_PROGRESS: "A review is already running.",
  NOT_RELEASING:
    "The project isn't waiting to be released: it's not in the Release phase, or it was already released.",
  RELEASE_IN_PROGRESS:
    "A release is already queued or running. The page shows its outcome when it finishes.",
  MERGE_CONFLICT:
    "The project branch conflicts with the default branch, so it couldn't be merged. Resolve the conflict on the project branch, then retry the release.",
  INVALID_TARGET_VERSION:
    "You can't roll back to that checkpoint: pick a recorded checkpoint of the version in development, below the current one and not in the Release phase.",
  ROLLBACK_BLOCKED:
    "A rollback isn't possible while an execution step or agent run is in progress. Wait for it to finish (or pause execution), then try again.",
  RATE_LIMITED: "Too many attempts. Wait a moment and try again.",
  // Recorded on a failed release attempt, and returned when starting a version (FR-60, FR-61; replaces v1 TAG_CONFLICT)
  TAG_EXISTS: "A tag for this version already exists in the repository.",
  // Release versions (FR-60, FR-61)
  VERSION_INVALID: "Enter a version as MAJOR.MINOR.PATCH, for example 1.2.0.",
  VERSION_NOT_GREATER: "The version must be greater than the latest released version.",
  VERSION_IN_PROGRESS: "A version is already in development. Release it before starting another.",
  RELEASE_NOT_MERGED: "The latest release is waiting for its pull request to be merged. Merge it first.",
  NOT_RELEASED: "Release the current version before starting a new one.",
  // External services and presets
  UPSTREAM_ERROR: "An external service (such as GitHub) failed or couldn't be reached. Try again in a moment.",
  PRESET_NOT_FOUND: "That tech stack preset doesn't exist.",
  GIT_ERROR: "Git or GitHub refused the operation.",
  RELEASE_CANCELLED: "A rollback cancelled this release before it ran.",
  // Approval links
  LINK_EXPIRED: "This approval link has expired.",
  LINK_USED: "This approval link has already been used.",
  // Agent runs (shown on run details)
  MISSING_INPUT: "The agent was missing a required input.",
  INVALID_PLAN: "The agent produced an invalid plan.",
  MISSING_SECTIONS:
    "The agent's specification was still missing required sections after a retry.",
  INVALID_OUTPUT: "The agent's reply couldn't be read, even after a retry.",
  PROVIDER_NOT_CONFIGURED:
    "The run's LLM provider has no API key on this project or the server. An Owner can add one in Settings → LLM.",
  PROVIDER_ERROR: "The model provider returned an error.",
} as const satisfies Record<string, string>;

export type ErrorCode = keyof typeof errorMessages;

export const errorCodes = Object.keys(errorMessages) as ErrorCode[];

/**
 * Context-specific copy for codes whose generic message is misleading in a particular action (still the one place
 * for error copy, CLAUDE.md rule 4). Pass the context to `errorMessage(error, context)`.
 */
export const contextualErrorMessages = {
  /** POST /api/projects/:id/members — 409 CONFLICT means the user is already a member (FR-8). */
  addMember: {
    CONFLICT: "That user is already a member of this project.",
  },
  /** POST /api/projects/:id/artifacts/:type — a new draft from edited Markdown (FR-18). */
  createArtifactDraft: {
    WRONG_PHASE:
      "This artifact can't be edited in the project's current phase.",
    /** `baseVersion` is no longer the latest version; `errorMessage` names it when the details carry `latestVersion: N`. */
    CONFLICT:
      "Someone saved a newer version while you were editing. Your text is kept here: copy it, then cancel and edit again to start from their version.",
    /** FR-18, FR-29: an edited plan's `aide-plan` block breaks the plan rules; the reasons are in `details`. */
    INVALID_PLAN:
      "The plan in the aide-plan block is invalid, so it wasn't saved. Fix these problems and save again.",
  },
  /** POST /api/projects/:id/refinement/run (FR-27). */
  startRefinement: {
    WRONG_PHASE:
      "Refinement rounds run only while the project is in the Refinement phase.",
    CONFLICT:
      "Someone else started a round at the same time. The page now shows it.",
  },
  /** GET /api/projects/:id/artifacts/:type/diff — the page only sends valid version pairs, so 400 means too large (FR-21). */
  artifactDiff: {
    VALIDATION:
      "These versions are too large to compare here: together they have more than 100,000 lines.",
  },
  /** POST /api/projects/:id/artifacts/:artifactId/approve · /reject (FR-17). */
  decideArtifact: {
    /** Also a stale draft: one created at an older project version (e.g. before a rollback or a newer approval). */
    ARTIFACT_NOT_DRAFT:
      "This draft can no longer be decided: it was already decided, replaced by a newer draft, or is out of date because the project moved on since it was created. The page now shows the latest state; edit the current version to make a new draft.",
    ARTIFACT_LOCKED:
      "This version is already approved and locked, so it can no longer be rejected.",
    WRONG_PHASE:
      "Artifacts of this type can't be decided in the project's current phase.",
    /** FR-30: the plan's `aide-plan` block can't produce execution steps; the reasons are in `details`. */
    INVALID_PLAN:
      "This plan's aide-plan block is invalid, so it can't be approved. Edit it as a new draft and fix these problems.",
  },
  /** POST /api/projects/:id/execution/start · /pause · /resume (FR-31, FR-37); the unmet conditions are in `details`. */
  /** POST /api/projects/:id/gates/:gateId/approve on a step_blocked escalation (FR-33): accept is refused there. */
  stepEscalationDecision: {
    INVALID_DECISION:
      "That decision isn't available for a blocked step. Choose Continue to retry the step, or Roll back to a checkpoint below the current one.",
  },
  executionControl: {
    PHASE_PRECONDITION_FAILED:
      "Execution can't be started or changed right now.",
    BUDGET_EXCEEDED:
      "This project has used its token budget, so no new step can start. An Owner can raise it in Settings.",
    CONFLICT:
      "Someone else changed execution at the same time. The page now shows the latest state.",
    FORBIDDEN:
      "Only Contributors and Owners can start, pause or resume execution.",
  },
  /** POST /api/projects/:id/review — run a review cycle now (FR-38). */
  startReview: {
    WRONG_PHASE:
      "A review can run only while the project is in the Review phase. The page now shows the latest state.",
    REVIEW_IN_PROGRESS:
      "A review is already queued or running. The page shows its result when it finishes.",
    CONFLICT:
      "Someone else changed the project at the same time. The page now shows the latest state; try again if a review is still needed.",
    FORBIDDEN: "Only Contributors and Owners can run a review.",
  },
  /** POST /api/projects/:id/git/pull-request — queue the pull request job again (FR-36). */
  retryPullRequest: {
    PHASE_PRECONDITION_FAILED:
      "The pull request can't be retried right now: the project must be in Execution with every step completed, or in Review, with no pull request recorded yet. The page now shows the latest state.",
    CONFLICT:
      "Opening the pull request is already queued or running. The page shows the outcome when it finishes.",
    FORBIDDEN:
      "Only Contributors and Owners can retry opening the pull request.",
  },
  /** POST /api/projects/:id/release — retry a failed release (FR-43). */
  retryRelease: {
    CONFLICT:
      "Someone else changed the project at the same time. The page now shows the latest state; retry if the release is still needed.",
    FORBIDDEN: "Only project Owners can retry a release.",
  },
  /** A failed release attempt's recorded `errorCode` (FR-42, FR-43), shown on Overview → Release. */
  releaseAttempt: {
    TAG_EXISTS:
      "The release tag already exists in the repository and points at a different commit, so it wasn't moved. Check the tag on the repository, then retry the release.",
    GIT_ERROR:
      "Git or GitHub refused the merge, tag or push. Check the repository access and the project branch, then retry the release.",
    RELEASE_CANCELLED:
      "A rollback cancelled this release before it ran. Retry the release once the project is ready again.",
  },
  /** POST /api/projects/:id/versions — Start new version (FR-60, FR-61). */
  startVersion: {
    VERSION_INVALID:
      "Enter a version as MAJOR.MINOR.PATCH with an optional -prerelease, for example 1.2.0 or 2.0.0-beta.1.",
    TAG_EXISTS:
      "A tag for this version already exists in the repository. Choose another version.",
    CONFLICT:
      "Someone else changed the project at the same time. Close this dialog to see the latest state, then try again.",
    FORBIDDEN: "Only project Owners can start a new version.",
    UPSTREAM_ERROR:
      "We couldn't check the repository's tags (GitHub failed or couldn't be reached). Try again in a moment.",
  },
  /** POST /api/projects/:id/rollback (FR-44, FR-45). */
  rollback: {
    CONFLICT:
      "Someone else changed the project at the same time. Close this dialog to see the latest state, then try again.",
    FORBIDDEN: "Only project Owners can roll back.",
    RATE_LIMITED:
      "A rollback ran less than a minute ago. Only one rollback per minute is allowed.",
  },
  /** GET/POST /api/approvals/:token — the one-click approval link confirmation page (FR-48). */
  approvalLink: {
    LINK_USED: "This approval link has already been used, so nothing changed.",
    LINK_EXPIRED:
      "This approval link has expired (links last 72 hours), so nothing changed. Open the project to decide the gate there.",
    FORBIDDEN:
      "Only Owners of this project can use this approval link. Sign in as an Owner, or ask one to decide the gate.",
    NOT_FOUND:
      "This approval link isn't valid. Check that you opened the whole link from the notification.",
    GATE_NOT_PENDING:
      "This gate was already decided or cancelled, so the link no longer does anything. Nothing changed.",
    CONFLICT:
      "Someone else changed the project at the same time. Reload the page and try again.",
  },
  /** PATCH /api/me/notifications/:id/read — the notification was deleted with its project. */
  markNotificationRead: {
    NOT_FOUND: "That notification no longer exists. The list now shows the latest.",
  },
  /** POST /api/admin/dead-letters/:jobId/retry (FR-51). */
  retryDeadLetter: {
    NOT_FOUND:
      "That job is no longer a dead letter: someone already retried it. The list now shows the latest.",
    CONFLICT:
      "The project already has an execution job queued or running. Retry this dead letter after it finishes.",
    FORBIDDEN: "Only administrators can retry dead letters, and only from a browser session.",
  },
  /** PATCH /api/admin/users/:id/role (FR-3). */
  changeSystemRole: {
    CONFLICT:
      "AIDE needs at least one Admin, so the last Admin can't become a User. Make someone else an Admin first.",
    NOT_FOUND: "That user no longer exists. The list now shows the latest.",
    FORBIDDEN: "Only administrators can change system roles, and only from a browser session.",
  },
} as const satisfies Record<string, Partial<Record<ErrorCode, string>>>;

export type ErrorContext = keyof typeof contextualErrorMessages;

/** Copy for a code, preferring the context's override. */
export function messageFor(code: ErrorCode, context?: ErrorContext): string {
  const overrides: Partial<Record<ErrorCode, string>> | undefined = context
    ? contextualErrorMessages[context]
    : undefined;
  return overrides?.[code] ?? errorMessages[code];
}

/** FR-2: `reason` on `/login?error=GITHUB_AUTH_FAILED&reason=…` (see backend Auth/GitHubAuth.cs). */
export const githubFailureMessages: Readonly<Record<string, string>> = {
  link_required:
    "An account with this GitHub email already exists. Sign in with your password, then link GitHub from your account page.",
  oauth_failed: "GitHub sign-in didn't complete. Try again.",
  email_required:
    "GitHub didn't share a verified email address. Add one to your GitHub account and try again.",
  already_linked:
    "That GitHub account is already linked to a different AIDE account.",
  link_session:
    "Your session ended while linking GitHub. Sign in and try linking again.",
  link_failed: "We couldn't link your GitHub account. Try again.",
  account_create_failed:
    "We couldn't create an account from your GitHub profile. Try again, or register with email.",
};

export function githubFailureMessage(reason: string | undefined): string {
  return (
    (reason ? githubFailureMessages[reason] : undefined) ??
    errorMessages.GITHUB_AUTH_FAILED
  );
}

function isErrorCode(code: unknown): code is ErrorCode {
  return typeof code === "string" && Object.hasOwn(errorMessages, code);
}

/** Copy for an `errorCode` recorded on a resource (e.g. a failed release), or `null` when the code is unknown. */
export function copyForCode(code: string | null | undefined, context?: ErrorContext): string | null {
  return isErrorCode(code) ? messageFor(code, context) : null;
}

const statusFallback: Partial<Record<number, ErrorCode>> = {
  400: "VALIDATION",
  401: "UNAUTHENTICATED",
  403: "FORBIDDEN",
  404: "NOT_FOUND",
  409: "CONFLICT",
  429: "RATE_LIMITED",
};

interface ProblemBody {
  title?: string | null;
  errorCode?: string;
  errors?: Record<string, string[]>;
  details?: string[];
  traceId?: string;
}

/** A failed API call, normalised from a ProblemDetails body (or a bare status). */
export class ApiError extends Error {
  readonly status: number;
  readonly errorCode: ErrorCode | null;
  readonly fieldErrors: Record<string, string[]>;
  /** ProblemDetails `details`: the individual reasons (e.g. the unmet entry conditions of PHASE_PRECONDITION_FAILED). */
  readonly details: readonly string[];
  readonly retryAfterSeconds: number | null;
  readonly traceId: string | undefined;

  constructor(init: {
    status: number;
    errorCode: ErrorCode | null;
    title?: string | null;
    fieldErrors?: Record<string, string[]>;
    details?: readonly string[];
    retryAfterSeconds?: number | null;
    traceId?: string;
  }) {
    super(init.title ?? `Request failed with HTTP ${String(init.status)}`);
    this.name = "ApiError";
    this.status = init.status;
    this.errorCode = init.errorCode;
    this.fieldErrors = init.fieldErrors ?? {};
    this.details = init.details ?? [];
    this.retryAfterSeconds = init.retryAfterSeconds ?? null;
    this.traceId = init.traceId;
  }
}

/** Builds an ApiError from an openapi-fetch `{ error, response }` pair. */
export function toApiError(error: unknown, response: Response): ApiError {
  const body: ProblemBody =
    typeof error === "object" && error !== null ? error : {};
  const code = isErrorCode(body.errorCode)
    ? body.errorCode
    : (statusFallback[response.status] ?? null);
  const retryAfter = Number.parseInt(
    response.headers.get("Retry-After") ?? "",
    10,
  );
  return new ApiError({
    status: response.status,
    errorCode: code,
    title: body.title,
    fieldErrors: body.errors ?? {},
    details: Array.isArray(body.details)
      ? body.details.filter((d) => typeof d === "string")
      : [],
    retryAfterSeconds:
      Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter : null,
    traceId: body.traceId,
  });
}

/** The value of a `key: value` entry in ProblemDetails `details` (e.g. `latestVersion: 5`), if present. */
export function detailValue(error: ApiError, key: string): string | null {
  const prefix = `${key}:`;
  const entry = error.details.find((d) => d.startsWith(prefix));
  return entry === undefined ? null : entry.slice(prefix.length).trim();
}

/**
 * ROLLBACK_BLOCKED has several reasons (FR-45); the server names the release-related ones in its title or in a
 * `reason: …` detail. Only the reasons are matched here, never a tag (tags may carry a suffix such as `v6-aide.2`).
 */
export const rollbackBlockedMessages = {
  versionReleased:
    "This version is released and closed, so it can't be rolled back. Start a new version to make changes.",
  releasePartiallyApplied:
    "A rollback isn't possible because the release was partially applied (merged or tagged, but not finished). Retry the release first.",
  releaseRunning:
    "A rollback isn't possible while a release is merging and tagging. Wait for it to finish, then try again.",
} as const;

function rollbackBlockedMessage(error: ApiError): string | null {
  const reason = `${detailValue(error, "reason") ?? ""} ${error.message}`.toLowerCase();
  if (/version[ _-]released/.test(reason)) {
    return rollbackBlockedMessages.versionReleased;
  }
  if (/partially[ _-]applied/.test(reason)) {
    return rollbackBlockedMessages.releasePartiallyApplied;
  }
  if (/release is (merging|running)|release_running/.test(reason)) {
    return rollbackBlockedMessages.releaseRunning;
  }
  return null;
}

/** User-facing copy for any thrown error; `context` selects overrides from `contextualErrorMessages`. */
export function errorMessage(error: unknown, context?: ErrorContext): string {
  if (error instanceof ApiError) {
    if (error.errorCode === "RATE_LIMITED" && error.retryAfterSeconds) {
      const s = error.retryAfterSeconds;
      return `Too many attempts. Try again in ${String(s)} second${s === 1 ? "" : "s"}.`;
    }
    if (context === "createArtifactDraft" && error.errorCode === "CONFLICT") {
      const latest = detailValue(error, "latestVersion");
      if (latest && /^\d+$/.test(latest)) {
        return `Someone saved a newer version (v${latest}) while you were editing. Your text is kept here: copy it, then cancel and edit again to start from v${latest}.`;
      }
    }
    if (error.errorCode === "ROLLBACK_BLOCKED") {
      const blocked = rollbackBlockedMessage(error);
      if (blocked) {
        return blocked;
      }
    }
    if (error.errorCode) {
      return messageFor(error.errorCode, context);
    }
    return "Something went wrong on our side. Try again.";
  }
  if (error instanceof TypeError) {
    return "We couldn't reach the server. Check your connection and try again.";
  }
  return "Something went wrong. Try again.";
}

/**
 * The server's reasons behind an error, for codes where they help the user act (PHASE_PRECONDITION_FAILED lists the
 * unmet entry conditions, FR-11; INVALID_PLAN every broken plan rule, FR-29). Falls back to the ProblemDetails title
 * when there are no `details`.
 */
const codesWithReasons: readonly ErrorCode[] = [
  "PHASE_PRECONDITION_FAILED",
  "INVALID_PLAN",
];

export function errorReasons(error: unknown): string[] {
  if (
    !(error instanceof ApiError) ||
    error.errorCode === null ||
    !codesWithReasons.includes(error.errorCode)
  ) {
    return [];
  }
  if (error.details.length > 0) {
    return [...error.details];
  }
  return error.message ? [error.message] : [];
}

export function isApiError(
  error: unknown,
  code?: ErrorCode,
): error is ApiError {
  return (
    error instanceof ApiError &&
    (code === undefined || error.errorCode === code)
  );
}

/**
 * Maps a VALIDATION `errors` dictionary onto React Hook Form fields (keys compared case-insensitively).
 * Returns the messages that didn't match a known field so the caller can show them in the summary.
 */
export function applyFieldErrors<T extends FieldValues>(
  error: unknown,
  setError: UseFormSetError<T>,
  fields: readonly Path<T>[],
): string[] {
  if (!(error instanceof ApiError)) {
    return [];
  }
  const unmatched: string[] = [];
  for (const [key, messages] of Object.entries(error.fieldErrors)) {
    const field = fields.find((f) => f.toLowerCase() === key.toLowerCase());
    const message = messages.join(" ");
    if (field) {
      setError(field, { type: "server", message });
    } else if (message) {
      unmatched.push(message);
    }
  }
  return unmatched;
}
