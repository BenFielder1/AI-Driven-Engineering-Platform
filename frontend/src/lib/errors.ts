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
  // LLM connections (FR-78, FR-79)
  CREDENTIAL_INVALID:
    "The provider rejected this key. Check that you copied the whole key and that it is active, then try again.",
  CREDENTIAL_NOT_OWNER: "You can only bind one of your own connections. Add your own key under Connections.",
  // Tech stack presets (FR-74)
  PRESET_NOT_FOUND: "That tech stack preset doesn't exist.",
  PRESET_LOCKED:
    "The implementation plan for this version is locked. Change the preset when you start the next version.",
  // GitHub App and connections (FR-80–FR-86, §4.14)
  GITHUB_NOT_CONNECTED:
    "Connect your GitHub account first, under Connections → GitHub.",
  GITHUB_APP_NOT_INSTALLED:
    "The AIDE GitHub App isn't installed there. Install it on the account or repository, then try again.",
  REPO_NAME_TAKEN: "A repository with this name already exists for that owner. Choose another name.",
  GITHUB_RATE_LIMITED: "GitHub's rate limit was reached. Wait a moment and try again.",
  GIT_ERROR: "Git or GitHub refused the operation.",
  // Vercel deployments (FR-87–FR-91, §4.15)
  VERCEL_GIT_NOT_CONNECTED:
    "Vercel's GitHub App can't access this repository. Install it on the repository's owner (or give it access to the repository), then try again.",
  VERCEL_PLAN_LIMIT:
    "Your Vercel plan doesn't allow this: a Hobby account can't deploy a repository owned by a GitHub organisation. Use a token for a Pro team, or a repository on your personal account.",
  DEPLOYMENT_NOT_CONFIGURED:
    "Deployment isn't set up for this project. An Owner can set up Vercel in Settings → Deployment.",
  INVALID_DEPLOYMENT: "Only a ready production deployment can be promoted.",
  DEPLOYMENT_TIMEOUT: "The deployment didn't finish within 30 minutes, so AIDE stopped waiting for it.",
  VERCEL_ERROR: "Vercel reported the build or deployment as failed. The build log says why.",
  // Import an existing repository (FR-73)
  TAG_NOT_FOUND: "That tag isn't in the repository. Pick another released version, or choose Never released.",
  RELEASE_CANCELLED: "A rollback cancelled this release before it ran.",
  // Recorded on a release attempt that was awaiting its merge (FR-65, §4.14)
  PR_CLOSED: "The pull request was closed without merging. Reopen it on GitHub, then retry.",
  MERGE_TIMEOUT:
    "The pull request wasn't merged within 14 days, so AIDE stopped waiting. Merge it on GitHub or fix what blocks it, then retry the release.",
  // Approval links
  LINK_EXPIRED: "This approval link has expired.",
  LINK_USED: "This approval link has already been used.",
  // Agent runs (shown on run details)
  MISSING_INPUT: "The agent was missing a required input.",
  INVALID_PLAN: "The agent produced an invalid plan.",
  MISSING_SECTIONS:
    "The agent's specification was still missing required sections after a retry.",
  INVALID_OUTPUT: "The agent's reply couldn't be read, even after a retry.",
  // Spec and plan validation, repo tools (FR-71, FR-75, FR-76, §4.13)
  MISSING_REQUIREMENT_IDS:
    "The agent's specification still had functional requirements without an FR-n ID or a Given/When/Then acceptance criterion after a retry.",
  INVALID_REQUIREMENT_TAGS:
    "The agent's specification still had invalid requirement tags after a retry: every FR-n needs exactly one of [new], [changed], [unchanged] or [removed], at least one must be [new] or [changed], and [changed] or [removed] IDs must exist in the previous version's spec.",
  UNCOVERED_REQUIREMENTS:
    "Some new or changed requirements aren't covered by any plan step. This is a warning: the plan can still be approved.",
  AGENT_SCOPE:
    "Some plan steps declare files outside the paths their specialist agent owns. This is a warning: the plan can still be approved.",
  PLAN_ORDER:
    "Some plan steps break the preset's recommended order. This is a warning: the plan can still be approved.",
  MISSING_VERCEL_CONFIG:
    "This plan doesn't create vercel.json, so Vercel would deploy AIDE branches automatically, on top of the deployments AIDE makes. Add vercel.json to a step with git.deploymentEnabled set to { \"aide/**\": false }. This is a warning: the plan can still be approved.",
  TOOL_LIMIT:
    "The agent reached its repository tool limit (25 calls or 400 KB read per run) and answered with what it had read.",
  PROVIDER_NOT_CONFIGURED:
    "The project has no LLM connection bound for this provider, and system keys aren't allowed. An Owner can bind one of their connections in Settings → LLM.",
  PROVIDER_ERROR: "The model provider returned an error.",
  // FR-157, FR-159: a provider turned off on this server, or one the user can't use yet.
  LLM_PROVIDER_UNAVAILABLE: "This AI provider isn't available here. Choose another provider or add a connection.",
  // Restore (FR-58); the specific copy per `reason` is restoreNotAllowedMessage().
  RESTORE_NOT_ALLOWED: "This version can't be restored right now.",
  // FR-116: a stack composition breaks a compatibility rule. The map doesn't interpolate, so callers append the
  // ProblemDetails `violations[].reason` list themselves (M25 Stack UI).
  STACK_INCOMPATIBLE: "These stack choices don't work together.",
  // FR-117: the stack is pinned once the version's implementation plan is locked; the VCS layer is the repo's host.
  STACK_LOCKED:
    "The stack can't change after the implementation plan is locked. Start a new version to change it.",
  VCS_MISMATCH: "The version control choice must match the project's repository host.",
  // FR-118 and Git hosts (§6): no usable Git connection for the project or request.
  GIT_NOT_CONNECTED:
    "Connect your Git host account (or ask an Owner to re-bind the project) to continue.",
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
  /** POST /api/projects/:id/artifacts/:type/versions/:v/restore (FR-58); RESTORE_NOT_ALLOWED uses its `reason`. */
  restoreArtifact: {
    VALIDATION: "This is already the latest version, so there is nothing to restore.",
    CONFLICT:
      "Someone saved a newer version at the same time. The page now shows the latest versions; try again if you still need this one.",
    FORBIDDEN: "Only Contributors and Owners can restore a version.",
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
  /** PATCH /api/projects/:id/settings/git — the release settings: merge strategy and tag prefix (FR-64, FR-65, §4.10). */
  releaseSettings: {
    CONFLICT:
      "The tag prefix can't change while a release is in progress or awaiting its merge on GitHub. Try again once the release finishes.",
    FORBIDDEN: "Only project Owners can change the release settings.",
  },
  /** GET /api/projects/:id/git/merge-readiness (FR-84). */
  mergeReadiness: {
    WRONG_PHASE: "Merge readiness is checked once the project reaches Review, and until it's released.",
  },
  /** POST /api/projects/:id/versions — Start new version (FR-60, FR-61). */
  startVersion: {
    PRESET_NOT_FOUND: "That preset is no longer available. Pick another one, keep the current preset, or choose No preset.",
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
  /** POST /api/me/connections/llm · /:id/test — the provider couldn't be reached (FR-78). */
  llmConnection: {
    UPSTREAM_ERROR: "We couldn't reach the provider to check the key. Nothing changed; try again in a moment.",
    FORBIDDEN: "Connections can only be changed from a signed-in browser session, not with an API token.",
    CREDENTIAL_INVALID:
      "The provider rejected this key, so it wasn't saved. Check that you copied the whole key and that it is active.",
  },
  /** POST /api/me/connections/vercel · /:id/test (FR-87). */
  vercelConnection: {
    UPSTREAM_ERROR: "We couldn't reach Vercel to check the token. Nothing changed; try again in a moment.",
    FORBIDDEN: "Connections can only be changed from a signed-in browser session, not with an API token.",
    CREDENTIAL_INVALID:
      "Vercel rejected this token, so it wasn't saved. Check that you copied the whole token, that it hasn't expired and, for a team token, that it can access the team.",
  },
  /** PATCH /api/projects/:id/settings/deployment (FR-88). */
  deploymentSettings: {
    FORBIDDEN: "Only project Owners can change the deployment settings.",
    CREDENTIAL_NOT_OWNER:
      "Changing the Vercel project uses your own Vercel connection: pick one of yours, which rebinds the project to it. Add a token under Connections → Vercel if you have none.",
    VALIDATION: "Deployment needs a GitHub-backed project: a Local repository can't deploy to Vercel.",
    UPSTREAM_ERROR: "Vercel failed or couldn't be reached, so nothing changed. Try again in a moment.",
    CONFLICT: "Someone else changed the deployment settings at the same time. The page now shows the latest; try again.",
  },
  /** PUT /api/projects/:id/settings/deployment/env/:key (FR-88): 502 means saved in AIDE but not pushed to Vercel. */
  deploymentEnvSave: {
    FORBIDDEN: "Only project Owners can edit environment variables.",
    UPSTREAM_ERROR:
      "Saved in AIDE, but pushing it to Vercel failed. AIDE pushes it again on the next save or deployment.",
  },
  /** DELETE /api/projects/:id/settings/deployment/env/:key (FR-88): nothing is deleted when Vercel can't delete it. */
  deploymentEnvDelete: {
    FORBIDDEN: "Only project Owners can delete environment variables.",
    UPSTREAM_ERROR: "Vercel couldn't delete it, so nothing was deleted. Try again in a moment.",
  },
  /** POST /api/projects/:id/deployments/preview · /:deploymentId/redeploy (FR-90, FR-91). */
  deploy: {
    FORBIDDEN: "Only Contributors and Owners can deploy a preview, and only Owners can redeploy production.",
    UPSTREAM_ERROR: "Vercel failed or couldn't be reached, so no deployment was created. Try again in a moment.",
  },
  /** POST /api/projects/:id/deployments/:deploymentId/promote (FR-91). */
  promoteDeployment: {
    FORBIDDEN: "Only project Owners can promote a production deployment.",
    INVALID_DEPLOYMENT:
      "This deployment can't be promoted: only a ready production deployment can. The list now shows the latest.",
    UPSTREAM_ERROR: "Vercel failed or couldn't be reached, so nothing was promoted. Try again in a moment.",
  },
  /** PATCH /api/projects/:id/settings/llm (FR-79). */
  llmBinding: {
    FORBIDDEN: "Only project Owners can change the LLM settings or the bound connection.",
    VALIDATION: "The connection must be for the chosen provider.",
  },
  /** POST /api/admin/integrations/github/manifest (FR-80). */
  githubAppRegistration: {
    CONFLICT: "An AIDE GitHub App is already registered. The page now shows it.",
    GITHUB_AUTH_FAILED:
      "GitHub didn't finish registering the App (the registration may have expired or been cancelled). Nothing was saved; start again.",
    FORBIDDEN: "Only administrators can register the GitHub App, and only from a browser session.",
  },
  /** GET/DELETE /api/me/connections/github (FR-81). */
  githubConnection: {
    FORBIDDEN: "Your GitHub connection can only be changed from a signed-in browser session, not with an API token.",
  },
  /** GET /api/me/github/repos · /owners · /refs (FR-81, FR-86). */
  githubRepos: {
    UPSTREAM_ERROR: "We couldn't load your repositories from GitHub. Try again in a moment.",
  },
  /** POST /api/me/github/repos (FR-81). */
  createGitHubRepo: {
    FORBIDDEN:
      "The AIDE GitHub App can't create repositories: it needs the Administration permission, or you may not create repositories for that owner.",
    GITHUB_APP_NOT_INSTALLED:
      "The AIDE GitHub App isn't installed on that owner, so it can't create the repository there. Install it first.",
    UPSTREAM_ERROR: "GitHub couldn't create the repository right now. Nothing was created; try again in a moment.",
  },
  /** POST /api/projects/:id/settings/git/switch-to-app (FR-83). */
  switchGitToApp: {
    FORBIDDEN:
      "Only project Owners with write access to the repository on GitHub can switch it to the GitHub App.",
    GITHUB_NOT_CONNECTED:
      "Connect your GitHub account first (under Connections), so AIDE can check your access to the repository.",
    GITHUB_APP_NOT_INSTALLED:
      "The AIDE GitHub App can't access this repository. Install it on the repository, then try again.",
  },
  /** PATCH /api/projects/:id/preset (FR-74). */
  changePreset: {
    PRESET_NOT_FOUND: "That preset is no longer available. Pick another one, or No preset (general).",
    FORBIDDEN: "Only project Owners can change the preset.",
    CONFLICT: "Someone else changed the project at the same time. Reload to see the latest, then try again.",
  },
  /** POST /api/projects (FR-86): a github_app repo needs push access for the caller; FR-73 import versions. */
  createProject: {
    PRESET_NOT_FOUND: "That preset is no longer available. Pick another one, or No preset (general).",
    FORBIDDEN: "You need write access to this repository on GitHub to create a project on it. Pick another repository.",
    VERSION_INVALID:
      "Enter a version as MAJOR.MINOR.PATCH with an optional -prerelease, for example 1.2.0 or 2.0.0-beta.1.",
    VERSION_NOT_GREATER: "The version to develop must be greater than the currently released version.",
    TAG_EXISTS: "The repository already has a tag for this version. Choose a higher version to develop.",
    TAG_NOT_FOUND:
      "That tag isn't in the repository (it may have been deleted). Pick another released version, or choose Never released.",
    NOT_FOUND:
      "We couldn't find that repository, or the access token can't see it. Check the URL and the token's repository access.",
    CREDENTIAL_NOT_OWNER: "You can only use one of your own Vercel connections. Pick another, or skip deployment.",
  },
  /** POST /api/projects `llm` (FR-159): the AI model section's connection. */
  createProjectLlm: {
    CREDENTIAL_NOT_OWNER: "You can only use one of your own connections. Pick another, or add a key under Connections.",
    NOT_FOUND: "That connection no longer exists. Pick another, or add a key under Connections.",
    VALIDATION: "Enter the model id, and pick a connection for the chosen provider.",
  },
  /** POST /api/stack/detect (FR-118): detection never blocks, so the copy says what to do instead. */
  detectStack: {
    GIT_NOT_CONNECTED:
      "Connect your GitHub account under Connections to detect the stack, or choose each layer yourself.",
    NOT_FOUND: "We couldn't read that repository or ref, so nothing was detected. Choose each layer yourself.",
    VALIDATION: "We couldn't read that repository, so nothing was detected. Choose each layer yourself.",
    FORBIDDEN: "GitHub refused to list the repository's files, so nothing was detected. Choose each layer yourself.",
  },
  /** PUT /api/projects/:id/stack and the start-version stack change (FR-117). */
  changeStack: {
    FORBIDDEN: "Only project Owners can change the stack.",
    CONFLICT: "Someone else changed the project at the same time. Reload to see the latest, then try again.",
    STACK_LOCKED:
      "The stack can't change after the implementation plan is locked. Start a new version to change it.",
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
    "This GitHub account is already linked to another AIDE user.",
  link_session:
    "Your session ended while linking GitHub. Sign in and try linking again.",
  link_failed: "We couldn't link your GitHub account. Try again.",
  access_denied: "GitHub authorization was cancelled.",
  account_create_failed:
    "We couldn't create an account from your GitHub profile. Try again, or register with email.",
};

/**
 * FR-71: the codes a refused repo tool call records (`ToolCallDto.error`, backend RepoTools), as the result shown in a
 * run's tool log. They are tool results, not endpoint errors, so they live beside `errorMessages` rather than in it.
 */
export const toolCallErrorMessages = {
  UNSAFE_PATH: "Refused: the path is outside the repository or hidden",
  TOOL_LIMIT: "Refused: the run reached its tool limit (25 calls or 400 KB read)",
  NOT_FOUND: "Refused: no such file or directory",
  INVALID_ARGUMENT: "Refused: the arguments were invalid",
  SEARCH_TIMEOUT: "Refused: the search took too long",
  FILE_TOO_LARGE: "Refused: the file is too large to read",
} as const satisfies Record<string, string>;

/** A refused tool call's result in words, with its code, e.g. "Refused: … (UNSAFE_PATH)". */
export function toolCallErrorMessage(code: string): string {
  const copy: string | undefined = (toolCallErrorMessages as Readonly<Record<string, string>>)[code];
  return copy ? `${copy} (${code})` : `Refused (${code})`;
}

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

/** STACK_INCOMPATIBLE `violations[]` (FR-116): the choice (`layer`), the option, the rule id and its reason. */
export interface StackViolation {
  layer: string;
  option: string;
  rule: string;
  reason: string;
}

interface ProblemBody {
  title?: string | null;
  violations?: unknown;
  errorCode?: string;
  reason?: string;
  installUrl?: string;
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
  /** ProblemDetails `reason`: the machine-readable sub-reason of some codes (RESTORE_NOT_ALLOWED, FR-58). */
  readonly reason: string | null;
  /** ProblemDetails `installUrl` (GITHUB_APP_NOT_INSTALLED, FR-81/FR-83): the AIDE GitHub App's install link. */
  readonly installUrl: string | null;
  /** ProblemDetails `violations` (STACK_INCOMPATIBLE, FR-116): each broken compatibility rule. */
  readonly violations: readonly StackViolation[];

  constructor(init: {
    status: number;
    errorCode: ErrorCode | null;
    title?: string | null;
    fieldErrors?: Record<string, string[]>;
    details?: readonly string[];
    retryAfterSeconds?: number | null;
    traceId?: string;
    reason?: string | null;
    installUrl?: string | null;
    violations?: readonly StackViolation[];
  }) {
    super(init.title ?? `Request failed with HTTP ${String(init.status)}`);
    this.name = "ApiError";
    this.status = init.status;
    this.errorCode = init.errorCode;
    this.fieldErrors = init.fieldErrors ?? {};
    this.details = init.details ?? [];
    this.retryAfterSeconds = init.retryAfterSeconds ?? null;
    this.traceId = init.traceId;
    this.reason = init.reason ?? null;
    this.installUrl = init.installUrl ?? null;
    this.violations = init.violations ?? [];
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
    reason: typeof body.reason === "string" ? body.reason : null,
    installUrl: typeof body.installUrl === "string" && body.installUrl.startsWith("https://") ? body.installUrl : null,
    violations: parseViolations(body.violations),
  });
}

function isViolation(value: unknown): value is StackViolation {
  if (typeof value !== "object" || value === null) {
    return false;
  }
  const v = value as Record<string, unknown>;
  return ["layer", "option", "rule", "reason"].every((k) => typeof v[k] === "string");
}

function parseViolations(value: unknown): StackViolation[] {
  return Array.isArray(value) ? value.filter(isViolation) : [];
}

/**
 * SPEC §6 STACK_INCOMPATIBLE: "These stack choices don't work together: {reasons}." The reasons are the violations'
 * reason texts (each once), or the generic copy when the server sent none.
 */
export function stackIncompatibleMessage(violations: readonly StackViolation[]): string {
  const reasons = [...new Set(violations.map((v) => v.reason.trim().replace(/\.$/, "")).filter(Boolean))];
  return reasons.length === 0
    ? errorMessages.STACK_INCOMPATIBLE
    : `These stack choices don't work together: ${reasons.join("; ")}.`;
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
    "A rollback isn't possible because the release is partially applied: it's awaiting its merge on GitHub, or it was merged or tagged but didn't finish. Merge or close the pull request, or retry the release, first.",
  /** Shown before asking: the release is awaiting its merge on GitHub (FR-65; the API says release_partially_applied). */
  awaitingMerge:
    "A rollback isn't possible while the release is awaiting its merge on GitHub (the release is partially applied). Merge or close the pull request first.",
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

/** FR-58: the `reason` values of RESTORE_NOT_ALLOWED (backend RestoreReasons). */
export const restoreReasons = [
  "stage_completed",
  "stage_not_reached",
  "other_release_version",
  "corrupted",
  "releasing",
] as const;

export type RestoreReason = (typeof restoreReasons)[number];

/**
 * FR-58: why a version can't be restored, per reason. `stage` is the stage that produces the type ("Specification").
 * Used both for the hidden action's explanation and for a 409 RESTORE_NOT_ALLOWED from the server.
 */
export function restoreNotAllowedMessage(reason: RestoreReason, stage: string): string {
  switch (reason) {
    case "stage_completed":
      return `${stage} is complete. Roll back the project to change it.`;
    case "stage_not_reached":
      return `${stage} hasn't been reached yet in this version.`;
    case "other_release_version":
      return "Only versions of the release version in development can be restored. Older release versions are read-only.";
    case "corrupted":
      return "This version failed its integrity check, so it can't be restored.";
    case "releasing":
      return "The project is being released, so nothing can be restored until the release finishes.";
  }
}

export function isRestoreReason(value: unknown): value is RestoreReason {
  return typeof value === "string" && (restoreReasons as readonly string[]).includes(value);
}

/** "42 seconds" / "3 minutes" for a Retry-After wait. */
function waitCopy(seconds: number): string {
  if (seconds < 120) {
    return `${String(seconds)} second${seconds === 1 ? "" : "s"}`;
  }
  return `${String(Math.ceil(seconds / 60))} minutes`;
}

/**
 * 501 NOT_IMPLEMENTED: a contract stub whose backend hasn't landed yet (ApiProblems.NotImplementedCode; not a domain
 * code, so it isn't in `errorMessages` and maps by status).
 */
export const notImplementedMessage = "This isn't available yet. It arrives in a later AIDE update.";

/** User-facing copy for any thrown error; `context` selects overrides from `contextualErrorMessages`. */
export function errorMessage(error: unknown, context?: ErrorContext): string {
  if (error instanceof ApiError) {
    if (error.errorCode === "RATE_LIMITED" && error.retryAfterSeconds) {
      const s = error.retryAfterSeconds;
      return `Too many attempts. Try again in ${String(s)} second${s === 1 ? "" : "s"}.`;
    }
    if (error.errorCode === "GITHUB_RATE_LIMITED" && error.retryAfterSeconds) {
      return `GitHub's rate limit was reached. Try again in ${waitCopy(error.retryAfterSeconds)}.`;
    }
    if (context === "createArtifactDraft" && error.errorCode === "CONFLICT") {
      const latest = detailValue(error, "latestVersion");
      if (latest && /^\d+$/.test(latest)) {
        return `Someone saved a newer version (v${latest}) while you were editing. Your text is kept here: copy it, then cancel and edit again to start from v${latest}.`;
      }
    }
    if (error.errorCode === "STACK_INCOMPATIBLE") {
      return stackIncompatibleMessage(error.violations);
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
    if (error.status === 501) {
      return notImplementedMessage;
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
