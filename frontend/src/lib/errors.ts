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
  NOT_RELEASING: "The project isn't in the release phase.",
  RELEASE_IN_PROGRESS: "A release is already in progress.",
  MERGE_CONFLICT: "The release branch has merge conflicts that need resolving.",
  INVALID_TARGET_VERSION: "You can't roll back to that version.",
  ROLLBACK_BLOCKED: "A rollback isn't possible right now.",
  RATE_LIMITED: "Too many attempts. Wait a moment and try again.",
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
      "That decision isn't available for a blocked step. Choose Continue to retry the step, or Roll back to a version below the current one.",
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
  /** POST /api/projects/:id/git/pull-request — queue the pull request job again (FR-36). */
  retryPullRequest: {
    PHASE_PRECONDITION_FAILED:
      "The pull request can't be retried right now: the project must be in Execution with every step completed, or in Review, with no pull request recorded yet. The page now shows the latest state.",
    CONFLICT:
      "Opening the pull request is already queued or running. The page shows the outcome when it finishes.",
    FORBIDDEN:
      "Only Contributors and Owners can retry opening the pull request.",
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
