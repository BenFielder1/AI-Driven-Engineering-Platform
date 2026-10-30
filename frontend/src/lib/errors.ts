import type { FieldValues, Path, UseFormSetError } from "react-hook-form";

import { withGitHost, type GitHostTerms } from "./git-host";

/** SPEC §6 GITLAB_RATE_LIMITED / BITBUCKET_RATE_LIMITED: "{host} is rate limiting requests. AIDE will retry." */
function hostJobRateLimited(host: string): string {
  return `${host} is rate limiting requests. AIDE will retry.`;
}

/**
 * SPEC §6 UPSTREAM_RATE_LIMITED for a Git host ("{provider} is rate limiting requests. Try again in a few minutes."),
 * optionally saying what didn't happen ("Nothing was created"). errorMessage() swaps "in a few minutes" for the
 * Retry-After wait in the contexts listed in `retryAfterContexts`.
 */
function hostRateLimited(host: string, outcome?: string): string {
  return outcome
    ? `${host} is rate limiting requests. ${outcome}; try again in a few minutes.`
    : `${host} is rate limiting requests. Try again in a few minutes.`;
}

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
  // External services
  UPSTREAM_ERROR: "An external service (such as GitHub) failed or couldn't be reached. Try again in a moment.",
  // LLM connections (FR-78, FR-79)
  CREDENTIAL_INVALID:
    "The provider rejected this key. Check that you copied the whole key and that it is active, then try again.",
  CREDENTIAL_NOT_OWNER: "You can only bind one of your own connections. Add your own key under Connections.",
  // GitHub App and connections (FR-80–FR-86, §4.14)
  GITHUB_NOT_CONNECTED:
    "Connect your GitHub account first, under Connections → GitHub.",
  GITHUB_APP_NOT_INSTALLED:
    "The AIDE GitHub App isn't installed there. Install it on the account or repository, then try again.",
  REPO_NAME_TAKEN: "A repository with this name already exists for that owner. Choose another name.",
  GITHUB_RATE_LIMITED: "GitHub's rate limit was reached. Wait a moment and try again.",
  GIT_ERROR: "Git or GitHub refused the operation.",
  // Deployment platforms (FR-87–FR-91, FR-144, §4.15, §6). `{platform}` is completed by errorMessage() from the
  // ProblemDetails `platform` extension, or by platformCopy() for a code recorded on a target or deployment.
  PLATFORM_GIT_NOT_CONNECTED: "{platform} can't see this repository. Connect it in {platform}, then verify again.",
  PLATFORM_PLAN_LIMIT: "Your {platform} plan doesn't allow this.",
  PLATFORM_ERROR: "{platform} returned an error. Try again later.",
  // A job code (FR-150, §6): the platform answered 429, so the deployment job backs off and retries.
  PLATFORM_RATE_LIMITED: "{platform} is rate limiting deployments. AIDE will retry.",
  DEPLOYMENT_ACTION_UNSUPPORTED: "{platform} doesn't support this action.",
  DEPLOYMENT_NOT_CONFIGURED:
    "Deployment isn't set up for this project. An Owner can set up the deployment targets in Settings → Deployment.",
  INVALID_DEPLOYMENT: "Only a ready production deployment can be rolled back to.",
  DEPLOYMENT_TIMEOUT: "The deployment didn't finish within 30 minutes, so AIDE stopped waiting for it.",
  // Import an existing repository (FR-73)
  TAG_NOT_FOUND: "That tag isn't in the repository. Pick another released version, or choose Never released.",
  RELEASE_CANCELLED: "A rollback cancelled this release before it ran.",
  // Recorded on a release attempt that was awaiting its merge (FR-65, §4.14). `{pr}` and `{host}` name the project's Git
  // host (FR-140): pass its terms to copyForCode(); without them the copy reads as GitHub.
  PR_CLOSED: "The {pr} was closed without merging. Reopen it on {host}, then retry.",
  MERGE_TIMEOUT:
    "The {pr} wasn't merged within 14 days, so AIDE stopped waiting. Merge it on {host} or fix what blocks it, then retry the release.",
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
    "Some plan steps break the stack's recommended order. This is a warning: the plan can still be approved.",
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
  // FR-124, FR-127: composed plan rules (a plan error, and a plan warning).
  STACK_CHANGE_UNPLANNED: "The plan must include work for the stack change.",
  UNDECLARED_GENERATED_FILE: "A file generated from this change isn't declared by any step.",
  // FR-118 and Git hosts (§6): no usable Git connection for the project or request.
  GIT_NOT_CONNECTED:
    "Connect your Git host account (or ask an Owner to re-bind the project) to continue.",
  // GitLab instances and connections (FR-130, FR-131, §6). Copy with a {host}, {missing} or {provider} is completed by
  // gitHostFailureMessage(), connectionScopeMessage() and the per-context overrides below.
  GIT_HOST_NOT_ALLOWED: "That address isn't allowed for a GitLab instance.",
  GIT_AUTH_FAILED: "Signing in with your Git host didn't complete. Try again.",
  CONNECTION_SCOPE_INSUFFICIENT: "This token is missing permissions AIDE needs, or it is the wrong type of token.",
  UPSTREAM_RATE_LIMITED: "The service is rate limiting requests. Try again in a few minutes.",
  // FR-136 job codes (§6): the Git host rate limited a background job; it is re-queued for the reset.
  GITLAB_RATE_LIMITED: hostJobRateLimited("GitLab"),
  BITBUCKET_RATE_LIMITED: hostJobRateLimited("Bitbucket"),
  // FR-143, §6: GitHub Pages as a frontend host needs the App's Pages permission.
  PLATFORM_PERMISSION_MISSING:
    "The AIDE GitHub App needs the Pages permission. Update it in GitHub settings, then accept it on each installation.",
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
  /**
   * /api/me/connections/platforms[/:id] · /:id/test (FR-142). `{provider}` is completed by platformConnectionMessage().
   */
  platformConnection: {
    UPSTREAM_ERROR: "We couldn't reach {provider} to check the token. Nothing changed; try again in a moment.",
    UPSTREAM_RATE_LIMITED: "{provider} is rate limiting requests. Nothing changed; try again in a few minutes.",
    FORBIDDEN: "Connections can only be changed from a signed-in browser session, not with an API token.",
    NOT_FOUND: "That connection no longer exists. The list now shows the latest.",
    CREDENTIAL_INVALID:
      "{provider} rejected this token, so it wasn't saved. Check that you copied the whole token, that it hasn't expired or been revoked, and that it is the type of token described above.",
  },
  /** FR-142 Vercel: the v2 copy (FR-87), naming team tokens. */
  vercelConnection: {
    UPSTREAM_ERROR: "We couldn't reach Vercel to check the token. Nothing changed; try again in a moment.",
    FORBIDDEN: "Connections can only be changed from a signed-in browser session, not with an API token.",
    CREDENTIAL_INVALID:
      "Vercel rejected this token, so it wasn't saved. Check that you copied the whole token, that it hasn't expired and, for a team token, that it can access the team.",
  },
  /** FR-142 Cloudflare: an account-owned (cfat_) token is refused before calling Cloudflare. */
  cloudflareConnection: {
    CONNECTION_SCOPE_INSUFFICIENT:
      "This is a Cloudflare account-owned token (it starts with cfat_), so it wasn't saved. Workers Builds needs a user API token: create one under My Profile → API Tokens with Workers Scripts Edit, Workers CI Edit, Account Settings Read and User Details Read.",
  },
  /** FR-142 Railway: a workspace token also needs its workspace id. */
  railwayConnection: {
    CREDENTIAL_INVALID:
      "Railway rejected this token, so it wasn't saved. Check that you copied the whole token and, for a workspace token, that the Workspace ID is the token's workspace.",
  },
  /** PUT /api/projects/:id/settings/deployment/targets/:component (FR-144). */
  deploymentTarget: {
    FORBIDDEN: "Only project Owners can change the deployment targets.",
    CREDENTIAL_NOT_OWNER:
      "You can only bind one of your own connections. Pick one of yours, or add a token under Connections.",
    VALIDATION: "Check the target's settings: some fields need attention.",
    UPSTREAM_ERROR: "A service AIDE needs for this failed or couldn't be reached, so nothing changed. Try again in a moment.",
    CONFLICT: "Someone else changed the deployment settings at the same time. The page now shows the latest; try again.",
  },
  /** POST /api/projects/:id/settings/deployment/targets/:component/verify (FR-144). */
  deploymentVerify: {
    FORBIDDEN: "Only project Owners can check a deployment target.",
  },
  /** DELETE /api/projects/:id/settings/deployment/targets/:component (FR-144). */
  deploymentUnbind: {
    FORBIDDEN: "Only project Owners can unbind a deployment target.",
  },
  /** PUT /api/projects/:id/settings/deployment/env/:key (FR-145): 502 means saved in AIDE but not pushed to a platform. */
  deploymentEnvSave: {
    FORBIDDEN: "Only project Owners can edit environment variables.",
    UPSTREAM_ERROR:
      "Saved in AIDE, but pushing it to a deployment platform failed. AIDE pushes it again on the next save or deployment.",
  },
  /** DELETE /api/projects/:id/settings/deployment/env/:key (FR-145): nothing is deleted when a platform can't delete it. */
  deploymentEnvDelete: {
    FORBIDDEN: "Only project Owners can delete environment variables.",
    UPSTREAM_ERROR: "A deployment platform couldn't delete it, so nothing was deleted. Try again in a moment.",
  },
  /** POST /api/projects/:id/deployments/preview · /:deploymentId/redeploy (FR-90, FR-91). */
  deploy: {
    FORBIDDEN: "Only Contributors and Owners can deploy a preview, and only Owners can redeploy production.",
    UPSTREAM_ERROR: "The deployment platform failed or couldn't be reached, so no deployment was created. Try again in a moment.",
  },
  /** POST /api/projects/:id/deployments/:deploymentId/rollback (FR-91, FR-150; `/promote` is its v2 alias). */
  rollbackDeployment: {
    FORBIDDEN: "Only project Owners can roll back a production deployment.",
    INVALID_DEPLOYMENT:
      "This deployment can't be rolled back to: only a ready production deployment that isn't live can. The list now shows the latest.",
    DEPLOYMENT_ACTION_UNSUPPORTED: "{platform} doesn't support rollback, so nothing changed.",
    UPSTREAM_ERROR: "The deployment platform failed or couldn't be reached, so nothing was rolled back. Try again in a moment.",
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
  /** POST /api/projects (FR-86): a github_app repo needs push access for the caller; FR-73 import versions. */
  createProject: {
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
    PLATFORM_GIT_NOT_CONNECTED: "{platform} can't see this repository. Connect it in {platform}, then try again.",
  },
  /** POST /api/projects `git` with `mode: gitlab` (FR-132, FR-136, FR-141): the bound connection and the picked project. */
  createProjectGitLab: {
    CREDENTIAL_NOT_OWNER:
      "You can only bind one of your own GitLab connections. Pick another, or connect GitLab under Connections.",
    GIT_NOT_CONNECTED:
      "This GitLab connection no longer works: GitLab rejected it, or an administrator disabled its instance. Reconnect it under Connections, or pick another.",
    FORBIDDEN:
      "Your GitLab account needs Developer access or higher on this project for AIDE to work on it. Pick another project, or ask its maintainers for access.",
    NOT_FOUND:
      "That GitLab project or connection no longer exists, or your GitLab account can't see it. Pick another project.",
    UPSTREAM_ERROR: "We couldn't reach GitLab to check the project. Nothing was created; try again in a moment.",
    UPSTREAM_RATE_LIMITED: hostRateLimited("GitLab", "Nothing was created"),
  },
  /** PATCH /api/projects/:id/settings/git `connectionId`: Rebind (FR-136). */
  gitBinding: {
    CREDENTIAL_NOT_OWNER: "You can only bind one of your own GitLab connections. Pick another.",
    GIT_NOT_CONNECTED:
      "That GitLab connection no longer works: GitLab rejected it, or an administrator disabled its instance. Reconnect it under Connections, then bind it.",
    FORBIDDEN:
      "Your GitLab account needs Developer access or higher on this repository to bind it. Ask a maintainer for access, or ask another Owner to re-bind.",
    NOT_FOUND: "That GitLab connection no longer exists. Pick another, or connect GitLab under Connections.",
    VALIDATION: "That connection is for a different GitLab instance than the project's repository. Pick another.",
    UPSTREAM_ERROR: "We couldn't reach GitLab to check your access. Nothing changed; try again in a moment.",
    UPSTREAM_RATE_LIMITED: hostRateLimited("GitLab", "Nothing changed"),
  },
  /** POST /api/projects `git` with `mode: bitbucket` (FR-135, FR-136, FR-141): the bound connection and the picked repository. */
  createProjectBitbucket: {
    CREDENTIAL_NOT_OWNER:
      "You can only bind one of your own Bitbucket connections. Pick another, or connect Bitbucket under Connections.",
    GIT_NOT_CONNECTED:
      "This Bitbucket connection no longer works: Bitbucket rejected it, or it is missing scopes. Reconnect it under Connections, or pick another.",
    FORBIDDEN:
      "Your Bitbucket account needs write access to this repository for AIDE to work on it. Pick another repository, or ask its admins for access.",
    NOT_FOUND:
      "That Bitbucket repository or connection no longer exists, or your Bitbucket account can't see it. Pick another repository.",
    UPSTREAM_ERROR: "We couldn't reach Bitbucket to check the repository. Nothing was created; try again in a moment.",
    UPSTREAM_RATE_LIMITED: hostRateLimited("Bitbucket", "Nothing was created"),
  },
  /** PATCH /api/projects/:id/settings/git `connectionId` on a Bitbucket project: Rebind (FR-136). */
  gitBindingBitbucket: {
    CREDENTIAL_NOT_OWNER: "You can only bind one of your own Bitbucket connections. Pick another.",
    GIT_NOT_CONNECTED:
      "That Bitbucket connection no longer works: Bitbucket rejected it, or it is missing scopes. Reconnect it under Connections, then bind it.",
    FORBIDDEN:
      "Your Bitbucket account needs write access to this repository to bind it. Ask a workspace admin for access, or ask another Owner to re-bind.",
    NOT_FOUND: "That Bitbucket connection no longer exists. Pick another, or connect Bitbucket under Connections.",
    VALIDATION: "That connection can't be bound to this project. Pick another.",
    UPSTREAM_ERROR: "We couldn't reach Bitbucket to check your access. Nothing changed; try again in a moment.",
    UPSTREAM_RATE_LIMITED: hostRateLimited("Bitbucket", "Nothing changed"),
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
  /** POST /api/stack/detect for a GitLab import (FR-141). */
  detectStackGitLab: {
    GIT_NOT_CONNECTED:
      "Your GitLab connection no longer works, so nothing was detected. Reconnect it under Connections, or choose each layer yourself.",
    NOT_FOUND: "We couldn't read that project or ref, so nothing was detected. Choose each layer yourself.",
    VALIDATION: "We couldn't read that project, so nothing was detected. Choose each layer yourself.",
    FORBIDDEN: "GitLab refused to list the project's files, so nothing was detected. Choose each layer yourself.",
  },
  /** POST /api/stack/detect for a Bitbucket import (FR-141). */
  detectStackBitbucket: {
    GIT_NOT_CONNECTED:
      "Your Bitbucket connection no longer works, so nothing was detected. Reconnect it under Connections, or choose each layer yourself.",
    NOT_FOUND: "We couldn't read that repository or ref, so nothing was detected. Choose each layer yourself.",
    VALIDATION: "We couldn't read that repository, so nothing was detected. Choose each layer yourself.",
    FORBIDDEN: "Bitbucket refused to list the repository's files, so nothing was detected. Choose each layer yourself.",
    UPSTREAM_RATE_LIMITED: hostRateLimited("Bitbucket"),
  },
  /** PUT /api/projects/:id/stack and the start-version stack change (FR-117). */
  changeStack: {
    FORBIDDEN: "Only project Owners can change the stack.",
    CONFLICT: "Someone else changed the project at the same time. Reload to see the latest, then try again.",
    STACK_LOCKED:
      "The stack can't change after the implementation plan is locked. Start a new version to change it.",
  },
  /** POST/PATCH /api/admin/integrations/gitlab[/:id] · /:id/check (FR-130). */
  gitlabInstance: {
    CONFLICT:
      "A GitLab instance with this base URL already exists, or this instance can't change that way: gitlab.com keeps its address, an instance in use keeps its base URL, and an instance configured in the server's environment only changes Enabled here.",
    UPSTREAM_ERROR:
      "AIDE couldn't reach a GitLab at that address (GET /api/v4/version didn't answer like GitLab). Check the base URL and try again.",
    NOT_FOUND: "That GitLab instance no longer exists. The list now shows the latest.",
    FORBIDDEN: "Only administrators can change GitLab instances, and only from a browser session.",
  },
  /** DELETE /api/admin/integrations/gitlab/:id (FR-130). */
  deleteGitLabInstance: {
    CONFLICT:
      "This GitLab instance can't be removed: projects use it, or it is gitlab.com or configured in the server's environment. Disable it instead.",
    NOT_FOUND: "That GitLab instance was already removed. The list now shows the latest.",
    FORBIDDEN: "Only administrators can remove GitLab instances, and only from a browser session.",
  },
  /** POST/DELETE /api/me/connections/gitlab · /:id/test (FR-131). */
  gitlabConnection: {
    CREDENTIAL_INVALID:
      "GitLab rejected this token, so it wasn't saved. Check that you copied the whole token and that it hasn't expired or been revoked.",
    NOT_FOUND: "That GitLab instance or connection no longer exists. The page now shows the latest.",
    UPSTREAM_ERROR: "We couldn't reach GitLab to check the token. Nothing changed; try again in a moment.",
    UPSTREAM_RATE_LIMITED: hostRateLimited("GitLab"),
    FORBIDDEN: "Connections can only be changed from a signed-in browser session, not with an API token.",
  },
  /** GET/POST /api/auth/gitlab/link: confirm linking a GitLab sign-in to the account with its email (FR-131). */
  gitlabLink: {
    FORBIDDEN:
      "You're signed in to a different AIDE account. Sign out, then sign in to the account with this GitLab email to link it.",
    NOT_FOUND: "This GitLab sign-in is no longer waiting to be linked (links last 10 minutes). Continue with GitLab again.",
    CONFLICT: "This GitLab account is already linked to another AIDE account.",
  },
  /** GET /api/me/git/gitlab/repos · /owners (FR-132). */
  gitlabRepos: {
    UPSTREAM_ERROR: "We couldn't load your projects from GitLab. Try again in a moment.",
    UPSTREAM_RATE_LIMITED: hostRateLimited("GitLab"),
    GIT_NOT_CONNECTED:
      "This GitLab connection no longer works: GitLab rejected it, or an administrator disabled its instance. Reconnect it under Connections.",
    NOT_FOUND: "That GitLab connection no longer exists. Pick another, or connect GitLab under Connections.",
  },
  /** POST /api/me/git/gitlab/repos (FR-132). */
  createGitLabRepo: {
    REPO_NAME_TAKEN: "A project with this name or path already exists in that namespace. Choose another name or path.",
    FORBIDDEN: "GitLab doesn't let you create projects there. Pick your personal namespace or another group.",
    UPSTREAM_RATE_LIMITED:
      "GitLab's project-creation limit was reached, so nothing was created. Try again later.",
    UPSTREAM_ERROR: "GitLab couldn't create the project right now. Nothing was created; try again in a moment.",
    GIT_NOT_CONNECTED:
      "This GitLab connection no longer works: GitLab rejected it, or an administrator disabled its instance. Reconnect it under Connections.",
    NOT_FOUND: "That GitLab connection or group no longer exists. Pick another namespace.",
  },
  /** PUT/DELETE /api/admin/integrations/bitbucket (FR-133). */
  bitbucketConsumer: {
    CONFLICT:
      "The Bitbucket consumer is set in the server's configuration (Bitbucket__OAuth__*), so it can't be changed or removed here. Change it there and restart AIDE.",
    FORBIDDEN: "Only administrators can change the Bitbucket consumer, and only from a browser session.",
  },
  /** POST/DELETE /api/me/connections/bitbucket · /:id/test (FR-134). */
  bitbucketConnection: {
    CREDENTIAL_INVALID:
      "Bitbucket rejected this email and token, so nothing was saved. Check the email of your Atlassian account, that you copied the whole token, and that it hasn't expired or been revoked. App passwords don't work.",
    NOT_FOUND: "That Bitbucket connection no longer exists. The page now shows the latest.",
    CONFLICT: "Bitbucket was connected at the same time elsewhere. Reload the page, then try again.",
    UPSTREAM_ERROR: "We couldn't reach Bitbucket to check the token. Nothing changed; try again in a moment.",
    UPSTREAM_RATE_LIMITED: hostRateLimited("Bitbucket"),
    FORBIDDEN: "Connections can only be changed from a signed-in browser session, not with an API token.",
  },
  /** GET/POST /api/auth/bitbucket/link: confirm linking a Bitbucket sign-in to the account with its email (FR-134). */
  bitbucketLink: {
    FORBIDDEN:
      "You're signed in to a different AIDE account. Sign out, then sign in to the account with this Bitbucket email to link it.",
    NOT_FOUND:
      "This Bitbucket sign-in is no longer waiting to be linked (links last 10 minutes). Continue with Bitbucket again.",
    CONFLICT: "This Bitbucket account is already linked to another AIDE account.",
  },
  /** GET /api/me/git/bitbucket/repos · /owners (FR-135). */
  bitbucketRepos: {
    UPSTREAM_ERROR: "We couldn't load your repositories from Bitbucket. Try again in a moment.",
    UPSTREAM_RATE_LIMITED: hostRateLimited("Bitbucket"),
    GIT_NOT_CONNECTED: "This Bitbucket connection no longer works: Bitbucket rejected it. Reconnect it under Connections.",
    NOT_FOUND: "That Bitbucket connection no longer exists. Pick another, or connect Bitbucket under Connections.",
  },
  /** POST /api/me/git/bitbucket/repos (FR-135). */
  createBitbucketRepo: {
    REPO_NAME_TAKEN: "A repository with this name already exists in that workspace. Choose another name.",
    FORBIDDEN: "Bitbucket doesn't let you create repositories there. Pick another workspace or project.",
    UPSTREAM_RATE_LIMITED: hostRateLimited("Bitbucket", "Nothing was created"),
    UPSTREAM_ERROR:
      "Bitbucket couldn't create the repository, or AIDE couldn't push its first commit (the README on main). Check the workspace on Bitbucket: if the repository exists but is empty, delete it there, then try again.",
    GIT_NOT_CONNECTED: "This Bitbucket connection no longer works: Bitbucket rejected it. Reconnect it under Connections.",
    NOT_FOUND: "That Bitbucket connection, workspace or project no longer exists. Pick another.",
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

/**
 * Contexts whose UPSTREAM_RATE_LIMITED copy names the Retry-After wait instead of "a few minutes" (Bitbucket's hourly
 * budget can hold requests back for longer, FR-135).
 */
const retryAfterContexts: ReadonlySet<ErrorContext> = new Set<ErrorContext>([
  "bitbucketConnection",
  "platformConnection",
  "bitbucketRepos",
  "createBitbucketRepo",
  "createProjectBitbucket",
  "gitBindingBitbucket",
  "detectStackBitbucket",
]);

/** Copy for a code, preferring the context's override. */
export function messageFor(code: ErrorCode, context?: ErrorContext): string {
  const overrides: Partial<Record<ErrorCode, string>> | undefined = context
    ? contextualErrorMessages[context]
    : undefined;
  return withGitHost(overrides?.[code] ?? errorMessages[code]);
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

/** Git host names for GIT_AUTH_FAILED's `provider` (FR-131, FR-134). */
const gitHostNames: Readonly<Record<string, string>> = { gitlab: "GitLab", bitbucket: "Bitbucket" };

/**
 * FR-131, FR-134: `reason` on `?error=GIT_AUTH_FAILED&reason=…&provider=gitlab|bitbucket` (backend GitLab/GitLabSignIn.cs,
 * Bitbucket/BitbucketSignIn.cs,
 * Aide.Domain/GitHosts/GitSignInRules.cs). `{host}` is the Git host's name.
 */
export const gitHostFailureMessages: Readonly<Record<string, string>> = {
  access_denied: "{host} authorization was cancelled.",
  state: "The {host} sign-in expired or was started in another browser. Try again.",
  instance_unavailable: "That {host} instance is disabled or no longer set up for sign-in. Ask an administrator.",
  token_exchange: "{host} didn't accept the sign-in. Try again.",
  host_not_allowed: "That {host} address isn't allowed. Ask an administrator to check the instance.",
  email_required:
    "{host} didn't share a verified email address. Verify an email on your {host} account, or register with email first.",
  already_linked: "This {host} account is already linked to another AIDE user.",
  link_session: "Your session ended while connecting {host}. Sign in and try again.",
  account_create_failed: "We couldn't create an account from your {host} profile. Try again, or register with email.",
  connection_failed: "Signed in, but we couldn't save your {host} connection. Connect it again from Connections.",
  busy: "Too many sign-ins are in progress. Try again in a minute.",
};

/** Reasons whose copy differs per host (Bitbucket Cloud has one OAuth consumer, not instances; FR-134). */
const gitHostFailureOverrides: Readonly<Record<string, Readonly<Record<string, string>>>> = {
  bitbucket: {
    instance_unavailable:
      "Bitbucket sign-in isn't set up here any more (the OAuth consumer was removed). Ask an administrator, or sign in another way.",
  },
};

/** SPEC §6 GIT_AUTH_FAILED: "Signing in with {host} didn't complete. Try again.", or the reason's specific copy. */
export function gitHostFailureMessage(provider: string | undefined, reason: string | undefined): string {
  const host = (provider ? gitHostNames[provider] : undefined) ?? "your Git host";
  const override = provider && reason ? gitHostFailureOverrides[provider]?.[reason] : undefined;
  const copy =
    override ?? (reason ? gitHostFailureMessages[reason] : undefined) ?? "Signing in with {host} didn't complete. Try again.";
  const text = copy.replaceAll("{host}", host);
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/**
 * SPEC §6 CONNECTION_SCOPE_INSUFFICIENT: "This token is missing permissions: {missing}." (`details` lists them). FR-134:
 * Bitbucket may also name scopes AIDE couldn't confirm (`unverifiableScopes`; a token is saved only when all are
 * verified), with what to do instead.
 */
export function connectionScopeMessage(
  missing: readonly string[],
  unverifiable: readonly string[] = [],
  oauthAvailable = true,
): string {
  const scopes = missing.map((m) => m.trim()).filter(Boolean);
  const unconfirmed = unverifiable.map((m) => m.trim()).filter(Boolean);
  const parts: string[] = [];
  if (scopes.length > 0) {
    parts.push(`This token is missing permissions: ${scopes.join(", ")}.`);
  }
  if (unconfirmed.length > 0) {
    parts.push(
      oauthAvailable
        ? `AIDE couldn't confirm these permissions: ${unconfirmed.join(", ")}. Connect with Bitbucket (OAuth) instead, or create a repository in your workspace first so AIDE can check them, then try again.`
        : `AIDE couldn't confirm these permissions: ${unconfirmed.join(", ")}. Create a repository in the workspace first so AIDE can check them, then try again.`,
    );
  }
  return parts.length === 0 ? errorMessages.CONNECTION_SCOPE_INSUFFICIENT : parts.join(" ");
}

/**
 * FR-142: copy for a failed platform connection call. A provider's own context wins (Vercel's team tokens, Cloudflare's
 * cfat_ refusal, Railway's workspace id), then the shared platform copy with `{provider}` filled in.
 */
export function platformConnectionMessage(error: unknown, provider: { id: string; name: string }): string {
  const own = `${provider.id}Connection`;
  const providerContext = Object.hasOwn(contextualErrorMessages, own) ? (own as ErrorContext) : undefined;
  const code = error instanceof ApiError ? error.errorCode : null;
  const overrides: Partial<Record<ErrorCode, string>> | undefined = providerContext
    ? contextualErrorMessages[providerContext]
    : undefined;
  const context = code !== null && overrides?.[code] ? providerContext : "platformConnection";
  return errorMessage(error, context).replaceAll("{provider}", provider.name);
}

/**
 * Codes recorded on deployments and events before v4 renamed them (D-V4-4: VERCEL_* → PLATFORM_*). They are no
 * longer API codes, so they aren't in `errorMessages`; copyForCode() still reads them so history stays readable.
 */
const legacyCodeCopy: Readonly<Record<string, string>> = {
  VERCEL_GIT_NOT_CONNECTED: "Vercel couldn't see this repository.",
  VERCEL_PLAN_LIMIT: "The Vercel plan didn't allow this.",
  VERCEL_ERROR: "Vercel reported the build or deployment as failed. The build log says why.",
};

function isErrorCode(code: unknown): code is ErrorCode {
  return typeof code === "string" && Object.hasOwn(errorMessages, code);
}

/**
 * Copy for an `errorCode` recorded on a resource (e.g. a failed release), or `null` when the code is unknown. `host`
 * names the project's Git host in host-aware copy (FR-140; default GitHub).
 */
export function copyForCode(
  code: string | null | undefined,
  context?: ErrorContext,
  host?: GitHostTerms,
): string | null {
  if (!isErrorCode(code)) {
    return typeof code === "string" && Object.hasOwn(legacyCodeCopy, code) ? (legacyCodeCopy[code] ?? null) : null;
  }
  const overrides: Partial<Record<ErrorCode, string>> | undefined = context
    ? contextualErrorMessages[context]
    : undefined;
  return withGitHost(overrides?.[code] ?? errorMessages[code], host);
}

/** SPEC §6 `{platform}`: the display name of a deployment platform id (ProblemDetails `platform`, a target's provider). */
export const platformNames: Readonly<Record<string, string>> = {
  vercel: "Vercel",
  netlify: "Netlify",
  cloudflare: "Cloudflare",
  render: "Render",
  railway: "Railway",
  github_pages: "GitHub Pages",
  supabase: "Supabase",
  fake: "the Fake provider",
};

/** Fills `{platform}` (unknown or missing: "the platform"), capitalising the first letter of the result. */
export function withPlatform(text: string, platform: string | null | undefined): string {
  if (!text.includes("{platform}")) {
    return text;
  }
  const name = (platform ? platformNames[platform] : undefined) ?? "the platform";
  const filled = text.replaceAll("{platform}", name);
  return filled.charAt(0).toUpperCase() + filled.slice(1);
}

/** PLATFORM_PLAN_LIMIT: "Your {platform} plan doesn't allow this. {detail}" with the platform's reason, when there is one. */
function withPlanDetail(code: ErrorCode | null, text: string, detail: string | null | undefined): string {
  const d = detail?.trim();
  return code === "PLATFORM_PLAN_LIMIT" && d ? `${text} ${d}` : text;
}

/**
 * FR-144: copy for a code recorded on a deployment target or deployment (the setup job's `errorCode`, a failed
 * deployment), naming its platform; `detail` is the platform's reason for PLATFORM_PLAN_LIMIT. `null` for unknown codes.
 */
export function platformCopy(
  code: string | null | undefined,
  platform: string | null | undefined,
  detail?: string | null,
): string | null {
  const copy = copyForCode(code);
  return copy === null || !isErrorCode(code) ? copy : withPlanDetail(code, withPlatform(copy, platform), detail);
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
  platform?: string;
  errors?: Record<string, string[]>;
  details?: string[];
  unverifiableScopes?: unknown;
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
  /** ProblemDetails `unverifiableScopes` (CONNECTION_SCOPE_INSUFFICIENT on a Bitbucket token, FR-134). */
  readonly unverifiableScopes: readonly string[];
  /** ProblemDetails `platform` (PLATFORM_* codes, FR-144): the platform id that fills `{platform}` in the copy. */
  readonly platform: string | null;

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
    unverifiableScopes?: readonly string[];
    platform?: string | null;
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
    this.unverifiableScopes = init.unverifiableScopes ?? [];
    this.platform = init.platform ?? null;
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
    unverifiableScopes: Array.isArray(body.unverifiableScopes)
      ? body.unverifiableScopes.filter((d): d is string => typeof d === "string")
      : [],
    platform: typeof body.platform === "string" ? body.platform : null,
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
  /**
   * Shown before asking: the release is awaiting its merge on the Git host (FR-65; the API says
   * release_partially_applied). Host-aware (FR-140): show it through rollbackAwaitingMergeMessage().
   */
  awaitingMerge:
    "A rollback isn't possible while the release is awaiting its merge on {host} (the release is partially applied). Merge or close the {pr} first.",
  releaseRunning:
    "A rollback isn't possible while a release is merging and tagging. Wait for it to finish, then try again.",
} as const;

/** FR-65, FR-140: rollbackBlockedMessages.awaitingMerge naming the project's Git host (default GitHub). */
export function rollbackAwaitingMergeMessage(host?: GitHostTerms): string {
  return withGitHost(rollbackBlockedMessages.awaitingMerge, host);
}

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
    if (
      error.errorCode === "UPSTREAM_RATE_LIMITED" &&
      error.retryAfterSeconds &&
      context &&
      retryAfterContexts.has(context)
    ) {
      return messageFor("UPSTREAM_RATE_LIMITED", context).replace(
        "in a few minutes",
        `in ${waitCopy(error.retryAfterSeconds)}`,
      );
    }
    if (error.errorCode === "CONNECTION_SCOPE_INSUFFICIENT") {
      const override: Partial<Record<ErrorCode, string>> | undefined = context
        ? contextualErrorMessages[context]
        : undefined;
      if (override?.CONNECTION_SCOPE_INSUFFICIENT) {
        return override.CONNECTION_SCOPE_INSUFFICIENT;
      }
      return connectionScopeMessage(error.details, error.unverifiableScopes);
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
      const generic = error.message.startsWith("Request failed with HTTP");
      return withPlanDetail(
        error.errorCode,
        withPlatform(messageFor(error.errorCode, context), error.platform),
        generic ? null : error.message,
      );
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
