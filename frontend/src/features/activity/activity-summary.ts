import {
  agentKindLabels,
  agentKinds,
  artifactTypeLabels,
  artifactTypes,
  escalationDecisionLabels,
  escalationDecisions,
  escalationReasonLabels,
  formatPercent,
  formatDuration,
  formatNumber,
  gateTypeLabels,
  gateTypes,
  llmProviderLabels,
  phaseLabels,
  phases,
  reviewDecisions,
} from "../projects/labels";
import { jobDetailCodes, platformCopy } from "../../lib/errors";
import { gitHostTermsOf } from "../../lib/git-host";
import { databaseProviderLabel } from "../deployments/database-format";
import { componentLabels, deploymentProviderLabels } from "../deployments/deployment-format";
import { stackLayerLabels } from "../stack/stack-model";
import type { ActivityItem } from "./activity-api";

type Payload = ActivityItem["payload"];

const platformDisplayNames: Readonly<Record<string, string>> = deploymentProviderLabels;

/** Looks up a release version's semver by id (FR-63: "1.2.0 · checkpoint 4"); may return `undefined`. */
export type VersionNameLookup = (releaseVersionId: string) => string | undefined;

/** Narrows a free-form payload value to one of `allowed` (the contract's wire values), or `null`. */
function oneOf<T extends string>(
  payload: Payload,
  key: string,
  allowed: readonly T[],
): T | null {
  const value = payload[key];
  return typeof value === "string"
    ? (allowed.find((a) => a === value) ?? null)
    : null;
}

/** A whole, non-negative number (versions), or `null`. */
function version(payload: Payload, key: string): number | null {
  const value = payload[key];
  return typeof value === "number" && Number.isInteger(value) && value >= 0
    ? value
    : null;
}

/** A non-empty string, or `null`. */
function str(payload: Payload, key: string): string | null {
  const value = payload[key];
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

/** `ARTIFACT_APPROVED` → "Artifact approved". */
function humanizeType(type: string): string {
  const words = type.toLowerCase().replaceAll("_", " ");
  return words.length === 0
    ? type
    : words.charAt(0).toUpperCase() + words.slice(1);
}

/**
 * A checkpoint from the payload as "1.2.0 · checkpoint 4" (FR-63), or "checkpoint 4" when the release version isn't
 * known. v2 payloads use `key` (e.g. `checkpoint`); events stored before D-V2-11 still carry `legacyKey`
 * (e.g. `projectVersion`), so that is read as a fallback.
 */
function checkpointAt(
  payload: Payload,
  key: string,
  legacyKey: string,
  names: VersionNameLookup | undefined,
): string | null {
  const n = version(payload, key) ?? version(payload, legacyKey);
  if (n === null) {
    return null;
  }
  const id = str(payload, "releaseVersionId");
  const name = id && names ? names(id) : undefined;
  return name ? `${name} · checkpoint ${String(n)}` : `checkpoint ${String(n)}`;
}

function describePhaseChange(payload: Payload, names: VersionNameLookup | undefined): string | null {
  const from = oneOf(payload, "from", phases);
  const to = oneOf(payload, "to", phases);
  if (!from || !to) {
    return null;
  }
  const at = checkpointAt(payload, "checkpoint", "projectVersion", names);
  const text = `Phase changed from ${phaseLabels[from]} to ${phaseLabels[to]}`;
  return at === null ? text : `${text} (${at})`;
}

function describeVersionStarted(payload: Payload): string | null {
  const v = str(payload, "version");
  if (!v) {
    return null;
  }
  const base = str(payload, "baseTag");
  return base ? `Version ${v} started from ${base}` : `Version ${v} started`;
}

/** FR-74: "nextjs-frontend@1.0.0", or "no preset" when the id is null. */
function presetAt(payload: Payload, idKey: string, versionKey: string): string {
  const id = str(payload, idKey);
  if (!id) {
    return "no preset";
  }
  const v = str(payload, versionKey);
  return v ? `${id}@${v}` : id;
}

/** FR-74, FR-92 PRESET_CHANGED: "Preset of 1.2.0 changed from no preset to nextjs-frontend@1.0.0". */
function describePresetChanged(payload: Payload): string {
  const v = str(payload, "version");
  const from = presetAt(payload, "fromPresetId", "fromPresetVersion");
  const to = presetAt(payload, "presetId", "presetVersion");
  return `Preset${v ? ` of ${v}` : ""} changed from ${from} to ${to}`;
}

/** FR-117, FR-155 STACK_CHANGED: "Stack changed: Backend, Data" (the catalog layers that changed). */
function describeStackChanged(payload: Payload): string {
  const layers = Array.isArray(payload.layersChanged)
    ? payload.layersChanged.filter((l): l is string => typeof l === "string")
    : [];
  return layers.length > 0
    ? `Stack changed: ${layers.map((l) => stackLayerLabels[l] ?? l).join(", ")}`
    : "Stack changed";
}

function describeGate(type: string, payload: Payload, names: VersionNameLookup | undefined): string | null {
  const gateType = oneOf(payload, "type", gateTypes);
  if (!gateType) {
    return null;
  }
  const gate = `${gateTypeLabels[gateType]} gate`;
  switch (type) {
    case "GATE_CREATED":
      return `${gate} created`;
    case "GATE_APPROVED": {
      if (payload.decision === undefined || payload.decision === null) {
        return `${gate} approved`;
      }
      const decision = oneOf(payload, "decision", escalationDecisions);
      if (!decision) {
        return null;
      }
      const target = checkpointAt(payload, "targetCheckpoint", "targetVersion", names);
      const label = escalationDecisionLabels[decision];
      return decision === "rollback" && target !== null
        ? `${gate} decided: ${label} to ${target}`
        : `${gate} decided: ${label}`;
    }
    case "GATE_REJECTED":
      return `${gate} rejected`;
    case "GATE_CANCELLED":
      return `${gate} cancelled`;
    case "GATE_OVERDUE":
      return `${gate} is overdue`;
    default:
      return null;
  }
}

function describeRollback(payload: Payload, names: VersionNameLookup | undefined): string | null {
  const from = checkpointAt(payload, "fromCheckpoint", "fromVersion", names);
  const target = version(payload, "targetCheckpoint") ?? version(payload, "targetVersion");
  return from === null || target === null
    ? null
    : `Rolled back from ${from} to checkpoint ${String(target)}`;
}

/** A whole, non-negative count (tokens, attempts, milliseconds), or `null`. */
function count(payload: Payload, key: string): number | null {
  return version(payload, key);
}

/** Agent run events (FR-23, FR-25, FR-26); payloads from AgentRunner. */
function describeRun(type: string, payload: Payload): string | null {
  const agent = oneOf(payload, "agent", agentKinds);
  if (!agent) {
    return null;
  }
  const run = `${agentKindLabels[agent]} run`;
  switch (type) {
    case "RUN_STARTED": {
      const model =
        typeof payload.model === "string" && payload.model
          ? payload.model
          : null;
      return model ? `${run} started on ${model}` : `${run} started`;
    }
    case "RUN_COMPLETED": {
      const input = count(payload, "inputTokens");
      const output = count(payload, "outputTokens");
      const ms = count(payload, "durationMs");
      if (input === null || output === null) {
        return `${run} completed`;
      }
      const tokens = `${formatNumber(input + output)} tokens`;
      return ms === null
        ? `${run} completed: ${tokens}`
        : `${run} completed: ${tokens} in ${formatDuration(ms)}`;
    }
    case "RUN_FAILED": {
      const attempts = count(payload, "attempts");
      return attempts === null || attempts === 0
        ? `${run} failed`
        : `${run} failed after ${String(attempts)} attempt${attempts === 1 ? "" : "s"}`;
    }
    case "BUDGET_EXCEEDED": {
      const used = count(payload, "tokensUsed");
      const budget = count(payload, "tokenBudget");
      const detail =
        used !== null && budget !== null
          ? ` (${formatNumber(used)} of ${formatNumber(budget)} tokens used)`
          : "";
      return `${run} refused: the token budget is used up${detail}`;
    }
    default:
      return null;
  }
}

const plural = (n: number, one: string, many = `${one}s`) =>
  `${String(n)} ${n === 1 ? one : many}`;

/** Execution step and runner events (FR-31–FR-34, FR-37); payloads from the execution job and ExecutionService. */
function describeStep(type: string, payload: Payload): string | null {
  switch (type) {
    case "EXECUTION_STARTED":
      return "Execution started";
    case "EXECUTION_PAUSED":
      return "Execution paused: no new step starts until it is resumed";
    case "EXECUTION_RESUMED":
      return "Execution resumed";
    case "EXECUTION_COMPLETED": {
      const steps = count(payload, "stepCount");
      return steps === null
        ? "Execution completed: every step is done"
        : `Execution completed: all ${plural(steps, "step")} done`;
    }
    default:
      break;
  }
  const n = count(payload, "stepNumber");
  if (n === null) {
    return null;
  }
  const step = `Step ${String(n)}`;
  const attempt = count(payload, "attempt");
  switch (type) {
    case "STEP_STARTED":
      return attempt === null
        ? `${step} started`
        : `${step} started (attempt ${String(attempt)})`;
    case "STEP_COMPLETED": {
      const sha =
        typeof payload.commitSha === "string" && payload.commitSha
          ? payload.commitSha.slice(0, 7)
          : null;
      const text = sha
        ? `${step} completed: commit ${sha}`
        : `${step} completed`;
      // FR-34: the commit stays on the project branch when the push fails; it is pushed again later.
      return payload.pushed === false ? `${text} (not pushed yet)` : text;
    }
    case "GIT_PUSH_FAILED": {
      // The commit stays on the project branch; the next step's push or the pull request job pushes it again.
      const reason = str(payload, "reason");
      const detail = reason ? ` (${reason})` : "";
      return `Pushing the project branch after step ${String(n)} failed${detail}; it will be pushed again`;
    }
    case "STEP_REJECTED": {
      const errors = count(payload, "errorCount");
      const detail = errors === null ? "" : ` with ${plural(errors, "error")}`;
      const retry = payload.retry === true ? ", retrying" : "";
      return attempt === null
        ? `${step} rejected by the Execution Guardian${detail}${retry}`
        : `${step} rejected by the Execution Guardian${detail} (attempt ${String(attempt)})${retry}`;
    }
    case "STEP_BLOCKED":
      return attempt === null
        ? `${step} blocked: an Owner decides its escalation`
        : `${step} blocked after ${plural(attempt, "attempt")}: an Owner decides its escalation`;
    case "STEP_UNBLOCKED":
      return `${step} unblocked: it will be retried with fresh attempts`;
    default:
      return null;
  }
}

/** Pull request events (FR-36); payloads from PullRequestJob. */
function describePullRequest(type: string, payload: Payload): string | null {
  if (type === "PULL_REQUEST_FORGOTTEN") {
    // Written when the Git provider or repository changes; the PR job opens a new one in the new repository.
    const changes = payload.changes;
    const what =
      Array.isArray(changes) && changes.includes("provider")
        ? "the Git provider changed"
        : "the Git repository changed";
    return `The recorded pull request was forgotten: ${what}`;
  }
  if (type === "PULL_REQUEST_FAILED") {
    const reason = str(payload, "reason");
    return reason
      ? `Opening the pull request failed: ${reason}`
      : "Opening the pull request failed";
  }
  const n = count(payload, "number");
  const base = str(payload, "baseBranch");
  const head = str(payload, "headBranch");
  const branches = base && head ? ` (${base} ← ${head})` : "";
  if (n === null) {
    return `Branch comparison recorded${branches}`;
  }
  return `Pull request #${String(n)} ${payload.reused === true ? "reused" : "opened"}${branches}`;
}

/** A 0–1 score, or `null`. */
function score(payload: Payload, key: string): number | null {
  const value = payload[key];
  return typeof value === "number" && value >= 0 && value <= 1 ? value : null;
}

const decisionPhrases: Record<(typeof reviewDecisions)[number], string> = {
  release_eligible: "release eligible",
  review_required: "another cycle needed",
  escalation_required: "escalated to an Owner",
};

const reviewEscalationReasons = [
  "low_score",
  "severe_drift",
  "max_cycles",
  "no_improvement",
  "no_optimization_plan",
] as const;

/**
 * Review events (FR-38, FR-39). Assumed payload: `cycle`, and on completion `overallScore`, `decision` and
 * `decisionReason`; anything missing falls back to a shorter line (or the server's summary).
 */
function describeReview(type: string, payload: Payload): string | null {
  const cycle = count(payload, "cycle");
  const review = cycle === null ? "Review" : `Review cycle ${String(cycle)}`;
  switch (type) {
    case "REVIEW_STARTED":
      return `${review} started`;
    case "REVIEW_FAILED": {
      const reason = str(payload, "reason");
      return reason ? `${review} failed: ${reason}` : `${review} failed`;
    }
    case "REVIEW_COMPLETED":
    case "COMPLIANCE_REPORT_CREATED": {
      const s = score(payload, "overallScore");
      const decision = oneOf(payload, "decision", reviewDecisions);
      const reason = oneOf(payload, "decisionReason", reviewEscalationReasons);
      const parts = [
        s === null ? null : `score ${formatPercent(s)}`,
        decision === null ? null : decisionPhrases[decision],
      ].filter((p): p is string => p !== null);
      const base =
        parts.length === 0 ? `${review} completed` : `${review} completed: ${parts.join(", ")}`;
      const withReason = reason ? `${base} (${escalationReasonLabels[reason].toLowerCase()})` : base;
      // FR-85: review stopped waiting for pending checks.
      return payload.checksTimedOut === true ? `${withReason}; stopped waiting for checks` : withReason;
    }
    default:
      return null;
  }
}

/**
 * Release events (FR-42, FR-43). Assumed payload: `tag`, `attempt`, and on failure `errorCode` and `conflictingFiles`;
 * anything missing falls back to a shorter line. `provider` (FR-140) names the Git host; older events without it read
 * as GitHub.
 */
function describeRelease(type: string, payload: Payload): string | null {
  const host = gitHostTermsOf(payload.provider);
  const tag = str(payload, "tag");
  const release = tag ? `Release ${tag}` : "Release";
  switch (type) {
    case "RELEASE_STARTED": {
      const attempt = count(payload, "attempt");
      return attempt !== null && attempt > 1
        ? `${release} started (attempt ${String(attempt)})`
        : `${release} started`;
    }
    case "RELEASE_COMPLETED": {
      const how = payload.mergedExternally === true ? `merged on ${host.host} and tagged` : "merged and tagged";
      return tag ? `Released: ${how} ${tag}` : `Released: ${how}`;
    }
    case "RELEASE_AWAITING_MERGE": {
      const reasons = Array.isArray(payload.reasons) ? payload.reasons : [];
      const first: unknown = reasons[0];
      const message =
        typeof first === "object" && first !== null && "message" in first && typeof first.message === "string"
          ? first.message
          : null;
      const base = `${release} is waiting for its ${host.pr} to be merged on ${host.host}`;
      if (message === null) {
        return base;
      }
      return reasons.length > 1 ? `${base}: ${message} (+${String(reasons.length - 1)} more)` : `${base}: ${message}`;
    }
    case "MERGE_READINESS_CHECKED": {
      if (payload.willAutoMerge === true) {
        return "Merge readiness checked: AIDE will merge automatically";
      }
      if (payload.willAutoMerge === false) {
        return `Merge readiness checked: a merge on ${host.host} will be needed`;
      }
      return "Merge readiness checked";
    }
    case "RELEASE_MERGED_EXTERNALLY":
      return `${release}: the ${host.pr} was merged on ${host.host}`;
    case "RELEASE_FAILED": {
      if (str(payload, "errorCode") === "PR_CLOSED") {
        return `${release} failed: the ${host.pr} was closed without merging`;
      }
      if (str(payload, "errorCode") === "MERGE_TIMEOUT") {
        return `${release} failed: the ${host.pr} wasn't merged within 14 days`;
      }
      if (str(payload, "errorCode") === "MERGE_CONFLICT") {
        const files = payload.conflictingFiles;
        const n = Array.isArray(files) ? files.length : 0;
        return n > 0
          ? `${release} failed: merge conflict in ${plural(n, "file")}`
          : `${release} failed: merge conflict`;
      }
      return `${release} failed`;
    }
    default:
      return null;
  }
}

/** FR-58: "Specification v1 restored as draft v3". */
function describeRestored(payload: Payload): string | null {
  const artifactType = oneOf(payload, "type", artifactTypes);
  const v = version(payload, "version");
  if (!artifactType || v === null) {
    return null;
  }
  const from = version(payload, "restoredFromVersion");
  const label = artifactTypeLabels[artifactType];
  return from === null
    ? `${label} restored as draft v${String(v)}`
    : `${label} v${String(from)} restored as draft v${String(v)}`;
}

/** FR-70: the Repository Analyst's locked codebase summary. */
function describeCodebaseSummary(payload: Payload): string {
  const v = version(payload, "version");
  return v === null
    ? "Codebase summary created by the Repository Analyst"
    : `Codebase summary v${String(v)} created by the Repository Analyst`;
}

/** Events whose payload names an artifact type and version (e.g. `ARTIFACT_APPROVED`). */
function describeArtifact(type: string, payload: Payload): string | null {
  const artifactType = oneOf(payload, "type", artifactTypes);
  const v = version(payload, "version");
  return artifactType && v !== null
    ? `${humanizeType(type)}: ${artifactTypeLabels[artifactType]} v${String(v)}`
    : null;
}

const llmProviders = Object.keys(llmProviderLabels) as (keyof typeof llmProviderLabels)[];

/** Why a project's LLM binding was cleared (FR-79; backend LlmBindingClearedReason). */
export const llmBindingClearedReasonLabels = {
  owner_role_lost: "the Owner who bound it is no longer an Owner",
  member_removed: "the Owner who bound it left the project",
  connection_deleted: "the connection was deleted",
} as const;

const bindingClearedReasons = Object.keys(llmBindingClearedReasonLabels) as (keyof typeof llmBindingClearedReasonLabels)[];

const deploymentTargetWords = { preview: "Preview", production: "Production" } as const;
const deploymentStatusWords = {
  queued: "queued",
  building: "building",
  ready: "ready",
  error: "failed",
  canceled: "canceled",
} as const;

/** FR-91, FR-92: why a deployment failed, in a few words (the full copy is in lib/errors). */
const deploymentFailureWords: Readonly<Record<string, string>> = {
  VERCEL_ERROR: "Vercel reported the build as failed",
  DEPLOYMENT_TIMEOUT: "it didn't finish within 30 minutes",
  CREDENTIAL_INVALID: "Vercel rejected the connection's token",
  CONNECTION_INVALID: "Vercel rejected the connection's token",
  VERCEL_GIT_NOT_CONNECTED: "Vercel can't access the repository",
  VERCEL_PLAN_LIMIT: "the Vercel plan doesn't allow it",
  // D-V4-4: the provider-neutral codes that replaced the VERCEL_* ones (FR-144).
  PLATFORM_ERROR: "the platform reported the build as failed",
  PLATFORM_GIT_NOT_CONNECTED: "the platform can't see the repository",
  PLATFORM_PLAN_LIMIT: "the platform plan doesn't allow it",
};

/** §4.14, FR-92: why GitHub stopped accepting a user's connection (`reason` of a GitHub `CONNECTION_INVALID`). */
const githubInvalidReasons: Readonly<Record<string, string>> = {
  rejected: "GitHub rejected its token",
  refresh_expired: "its refresh token expired",
  refresh_failed: "refreshing its token failed",
};

/** FR-90, FR-91, §4.15: "Preview deployment of 1.2.0 (3f2a9c1) ready at https://…". */
function describeDeployment(type: string, payload: Payload): string | null {
  const target = oneOf(payload, "target", ["preview", "production"] as const);
  if (!target) {
    return null;
  }
  const sha = str(payload, "gitSha");
  const version = str(payload, "releaseVersion");
  const subject = `${deploymentTargetWords[target]} deployment${version ? ` of ${version}` : ""}${sha ? ` (${sha.slice(0, 7)})` : ""}`;
  const url = str(payload, "url");
  if (type === "DEPLOYMENT_FAILED") {
    const code = str(payload, "errorCode");
    const why = code ? deploymentFailureWords[code] : undefined;
    return why ? `${subject} failed: ${why}` : `${subject} failed`;
  }
  if (type === "DEPLOYMENT_PREVIEW_READY") {
    return `${subject} ready${url ? ` at ${url}` : ""}`;
  }
  if (type === "DEPLOYMENT_CREATED") {
    const trigger = oneOf(payload, "trigger", ["auto", "manual", "promote", "redeploy", "rollback"] as const);
    if (trigger === "rollback") {
      return `${subject} rolled back`;
    }
    return trigger === "promote" ? `${subject} promoted` : trigger === "redeploy" ? `${subject} redeployed` : `${subject} created`;
  }
  const status = oneOf(payload, "status", ["queued", "building", "ready", "error", "canceled"] as const);
  if (!status) {
    return null;
  }
  return `${subject} ${deploymentStatusWords[status]}${status === "ready" && url ? ` at ${url}` : ""}`;
}

/** FR-142: deployment platforms and databases with pasted-token connections (what each calls its token). */
const platformConnectionTokens: Readonly<Record<string, { name: string; token: string }>> = {
  vercel: { name: "Vercel", token: "token" },
  netlify: { name: "Netlify", token: "token" },
  cloudflare: { name: "Cloudflare", token: "token" },
  render: { name: "Render", token: "API key" },
  railway: { name: "Railway", token: "token" },
  supabase: { name: "Supabase", token: "token" },
};

/** LLM and platform connection events (FR-78, FR-79, FR-87, FR-142). */
function describeConnection(type: string, payload: Payload): string | null {
  const platform = typeof payload.provider === "string" ? platformConnectionTokens[payload.provider] : undefined;
  if (type === "CONNECTION_INVALID" && platform) {
    return `${platform.name} connection marked invalid: ${platform.name} rejected its ${platform.token}`;
  }
  if (type === "CONNECTION_INVALID" && payload.provider === "github") {
    // §4.14, FR-92: a user's GitHub connection (no project); its owner reconnects under Connections.
    const login = str(payload, "login");
    const reason = str(payload, "reason");
    const why = reason ? githubInvalidReasons[reason] : undefined;
    const subject = login ? `GitHub connection of @${login}` : "GitHub connection";
    return `${subject} marked invalid${why ? `: ${why}` : ""}; reconnect it under Connections`;
  }
  const provider = oneOf(payload, "provider", llmProviders);
  const name = provider ? llmProviderLabels[provider] : "LLM";
  switch (type) {
    case "CONNECTION_INVALID":
      return `${name} connection marked invalid: the provider rejected its key during a run`;
    case "LLM_BINDING_CLEARED": {
      const reason = oneOf(payload, "reason", bindingClearedReasons);
      const text = `${name} connection unbound`;
      return reason ? `${text}: ${llmBindingClearedReasonLabels[reason]}` : text;
    }
    default:
      return null;
  }
}

/** FR-155: the v4 events the admin log words like the activity feed; older events keep the server's summary there. */
export const fr155EventTypes: ReadonlySet<string> = new Set([
  "STACK_CHANGED",
  "STACK_DETECTED",
  "GIT_HOST_INSTANCE_CHANGED",
  "GIT_BINDING_CLEARED",
  "DEPLOYMENT_TARGET_CONFIGURED",
  "DEPLOYMENT_TARGET_FAILED",
  "DATABASE_PROVISIONED",
  "DATABASE_PROVISIONING_FAILED",
  "DATABASE_EXPIRING",
  "DATABASE_RESET",
  "MIGRATION_RUN_STARTED",
  "MIGRATION_RUN_COMPLETED",
  "MIGRATION_RUN_FAILED",
  "PREVIEW_ROLLOUT_COMPLETED",
]);

const environments = ["preview", "production"] as const;

/** "1 Nov 2026" from an ISO timestamp, or `null`. */
function dateOf(payload: Payload, key: string): string | null {
  const iso = str(payload, key);
  const date = iso ? new Date(iso) : null;
  return date && !Number.isNaN(date.getTime()) ? new Intl.DateTimeFormat(undefined, { dateStyle: "medium" }).format(date) : null;
}

const shortShaOf = (payload: Payload, key = "gitSha"): string | null => str(payload, key)?.slice(0, 7) ?? null;

/** Adds an event's error copy (lib/errors) as a second sentence, when it says more than the event's own line. */
const withCopy = (line: string, copy: string | null | undefined): string => (copy ? `${line}. ${copy}` : line);

/** A payload list's length, or 0 when it's missing. */
const lengthOf = (payload: Payload, key: string): number => {
  const value = payload[key];
  return Array.isArray(value) ? value.length : 0;
};

/** FR-118, FR-155: "Stack detected at the base of 1.3.0 (abcdef1): 3 suggestions, 1 conflict". */
function describeStackDetected(payload: Payload): string {
  const v = str(payload, "version");
  const sha = shortShaOf(payload, "baseCommitSha");
  const base = `Stack detected${v ? ` at the base of ${v}` : ""}${sha ? ` (${sha})` : ""}`;
  const counts: [string, string][] = [
    ["suggestions", "suggestion"],
    ["conflicts", "conflict"],
    ["mismatches", "mismatch"],
  ];
  const parts = counts.flatMap(([key, one]) => {
    const n = lengthOf(payload, key);
    return n > 0 ? [plural(n, one, one === "mismatch" ? "mismatches" : `${one}s`)] : [];
  });
  return parts.length > 0 ? `${base}: ${parts.join(", ")}` : base;
}

const instanceChanges = { created: "added", updated: "changed", removed: "removed" } as const;
const instanceChangeKinds = Object.keys(instanceChanges) as (keyof typeof instanceChanges)[];

/** FR-130, FR-155 (project-less, admin log): `GitLab instance "Acme" added`. */
function describeGitHostInstance(payload: Payload): string | null {
  const change = oneOf(payload, "change", instanceChangeKinds);
  const kind = oneOf(payload, "kind", ["gitlab", "bitbucket"] as const);
  if (!change || !kind) {
    return null;
  }
  const name = str(payload, "name");
  return `${gitHostTermsOf(kind).host} instance${name ? ` "${name}"` : ""} ${instanceChanges[change]}`;
}

/** Why a project's Git binding was cleared (FR-136; backend GitBindingClearedReason). */
const gitBindingClearedReasons: Readonly<Record<string, (host: string) => string>> = {
  owner_role_lost: () => "the Owner who bound it is no longer an Owner",
  member_removed: () => "the Owner who bound it left the project",
  connection_deleted: () => "the connection was deleted",
  connection_invalid: (host) => `${host} rejected the connection`,
};

/** FR-136, FR-155: "GitLab connection unbound: the connection was deleted; an Owner re-binds it in Settings → Git". */
function describeGitBindingCleared(payload: Payload): string {
  const provider = oneOf(payload, "provider", ["gitlab", "bitbucket"] as const);
  const host = provider ? gitHostTermsOf(provider).host : "Git";
  const reason = str(payload, "reason");
  const why = reason && Object.hasOwn(gitBindingClearedReasons, reason) ? gitBindingClearedReasons[reason]?.(host) : undefined;
  return `${host} connection unbound${why ? `: ${why}` : ""}; an Owner re-binds it in Settings → Git`;
}

const targetProvider = (payload: Payload): string | null => {
  const provider = str(payload, "provider");
  return provider ? (platformDisplayNames[provider] ?? provider) : null;
};

/** The target's subject: "Frontend deployment target", "Backend deployment target" or "Deployment target". */
const targetSubject = (payload: Payload): string => {
  const component = oneOf(payload, "component", ["frontend", "backend"] as const);
  return component ? `${componentLabels[component]} deployment target` : "Deployment target";
};

const targetFailureWords: Readonly<Record<string, (platform: string) => string>> = {
  git_access_lost: (platform) => `${platform} can no longer reach the repository`,
  token_invalid: (platform) => `${platform} rejected the connection's token`,
  setup_failed: () => "its setup failed",
  readiness_failed: () => "a readiness check failed",
};

/** FR-144, FR-155: "Frontend deployment target set up on Netlify". */
function describeTargetConfigured(payload: Payload): string {
  const provider = targetProvider(payload);
  const base = `${targetSubject(payload)} set up${provider ? ` on ${provider}` : ""}`;
  return payload.status === "attention" ? `${base}; it needs attention` : base;
}

/** FR-144, FR-155: "Frontend deployment target on Netlify needs attention: Netlify rejected the connection's token". */
function describeTargetFailed(payload: Payload): string {
  const provider = targetProvider(payload);
  const reason = str(payload, "reason");
  const words = reason && Object.hasOwn(targetFailureWords, reason) ? targetFailureWords[reason]?.(provider ?? "The platform") : undefined;
  const base = `${targetSubject(payload)}${provider ? ` on ${provider}` : ""} needs attention${words ? `: ${words}` : ""}`;
  return reason === "setup_failed" || reason === "readiness_failed"
    ? withCopy(base, platformCopy(str(payload, "errorCode"), str(payload, "provider")))
    : base;
}

/** "Preview database on Render Postgres". */
const databaseSubject = (payload: Payload): string => {
  const env = oneOf(payload, "environment", environments);
  const provider = str(payload, "provider");
  return `${env ? deploymentTargetWords[env] : "Project"} database${provider ? ` on ${databaseProviderLabel(provider)}` : ""}`;
};

const databaseModeWords = { create: "created", link: "linked", external: "connected" } as const;
const databaseModes = Object.keys(databaseModeWords) as (keyof typeof databaseModeWords)[];

/** The copy of a job's error code, leaving out the codes that only repeat the line ("Creating the database failed."). */
function jobCopy(payload: Payload, platform: string | null): string | null {
  const code = str(payload, "errorCode");
  return code && !jobDetailCodes.has(code) ? platformCopy(code, platform) : null;
}

/** FR-152, FR-153, FR-155: managed database and migration run events. */
function describeDatabase(type: string, payload: Payload): string | null {
  const subject = databaseSubject(payload);
  const provider = str(payload, "provider");
  switch (type) {
    case "DATABASE_PROVISIONED": {
      const mode = oneOf(payload, "mode", databaseModes);
      const expires = dateOf(payload, "expiresAt");
      const base = `${subject} ${mode ? databaseModeWords[mode] : "available"}`;
      return expires ? `${base} (expires ${expires})` : base;
    }
    case "DATABASE_PROVISIONING_FAILED":
      return withCopy(`${subject}: setup failed`, jobCopy(payload, provider));
    case "DATABASE_EXPIRING": {
      if (payload.reason === "paused") {
        return `${subject} is paused: restore the project to keep using it`;
      }
      const expires = dateOf(payload, "expiresAt");
      return expires ? `${subject} expires on ${expires}` : `${subject} expires soon`;
    }
    case "DATABASE_RESET": {
      // FR-148: the reset only ever applies to the preview database; older payloads don't name the environment.
      const where = provider ? ` on ${databaseProviderLabel(provider)}` : "";
      // A `manual` reset (Railway) only records that someone reset it on the platform; AIDE ran nothing.
      return payload.method === "reset_schema" || payload.method === "reset_schema_external"
        ? `Preview database${where} reset: its schema was emptied`
        : `Preview database${where} recorded as reset`;
    }
    default:
      return null;
  }
}

/** FR-153, FR-154, FR-155: "Production migration run (abcdef1) applied 3 files". */
function describeMigrationRun(type: string, payload: Payload): string | null {
  const env = oneOf(payload, "environment", environments);
  const sha = shortShaOf(payload);
  const subject = `${env ? deploymentTargetWords[env] : "Database"} migration run${sha ? ` (${sha})` : ""}`;
  switch (type) {
    case "MIGRATION_RUN_STARTED":
      return `${subject} started`;
    case "MIGRATION_RUN_COMPLETED": {
      const applied = count(payload, "applied");
      if (applied === null) {
        return `${subject} completed`;
      }
      return applied === 0 ? `${subject} completed: nothing new to apply` : `${subject} applied ${plural(applied, "file")}`;
    }
    case "MIGRATION_RUN_FAILED": {
      // FR-154: a host-mode pre-deploy has no run of its own (`runId` null), only the deployment that ran it.
      const line = str(payload, "runId")
        ? `${subject} failed`
        : `${env ? deploymentTargetWords[env] : "Database"} migrations${sha ? ` (${sha})` : ""} failed during the deployment`;
      return withCopy(line, jobCopy(payload, null));
    }
    default:
      return null;
  }
}

/** FR-147, FR-155: "Preview rollout (abcdef1) succeeded: Frontend at https://…, Backend at https://…". */
function describePreviewRollout(payload: Payload): string | null {
  const state = oneOf(payload, "state", ["succeeded", "failed", "blocked", "canceled"] as const);
  if (!state) {
    return null;
  }
  const sha = shortShaOf(payload);
  const subject = `Preview rollout${sha ? ` (${sha})` : ""}`;
  switch (state) {
    case "succeeded": {
      const live = (Array.isArray(payload.components) ? (payload.components as unknown[]) : []).flatMap((c) => {
        if (typeof c !== "object" || c === null) {
          return [];
        }
        const { component, url } = c as { component?: unknown; url?: unknown };
        return (component === "frontend" || component === "backend") && typeof url === "string" && url
          ? [`${componentLabels[component]} at ${url}`]
          : [];
      });
      return live.length > 0 ? `${subject} succeeded: ${live.join(", ")}` : `${subject} succeeded`;
    }
    case "canceled":
      return `${subject} replaced by a newer preview`;
    default:
      return withCopy(`${subject} ${state}`, platformCopy(str(payload, "errorCode"), null) ?? str(payload, "message"));
  }
}

/**
 * FR-50: the one-line description shown in the activity feed, built from the event's type and payload with the same
 * labels the rest of the UI uses. Unknown types and payloads that don't match the expected shape fall back to the
 * server's `summary`.
 */
export function describeActivity(
  item: Pick<ActivityItem, "type" | "payload" | "summary">,
  names?: VersionNameLookup,
): string {
  const { type, payload } = item;
  let text: string | null;
  switch (type) {
    case "PHASE_CHANGED":
      text = describePhaseChange(payload, names);
      break;
    case "VERSION_STARTED":
      text = describeVersionStarted(payload);
      break;
    case "PRESET_CHANGED":
      text = describePresetChanged(payload);
      break;
    case "STACK_CHANGED":
      text = describeStackChanged(payload);
      break;
    case "ARTIFACT_RESTORED":
      text = describeRestored(payload);
      break;
    case "CODEBASE_SUMMARY_CREATED":
      text = describeCodebaseSummary(payload);
      break;
    case "GATE_CREATED":
    case "GATE_APPROVED":
    case "GATE_REJECTED":
    case "GATE_CANCELLED":
    case "GATE_OVERDUE":
      text = describeGate(type, payload, names);
      break;
    case "ROLLBACK_COMPLETED":
      text = describeRollback(payload, names);
      break;
    case "RELEASE_STARTED":
    case "RELEASE_COMPLETED":
    case "RELEASE_FAILED":
    case "RELEASE_AWAITING_MERGE":
    case "RELEASE_MERGED_EXTERNALLY":
    case "MERGE_READINESS_CHECKED":
      text = describeRelease(type, payload);
      break;
    case "RUN_STARTED":
    case "RUN_COMPLETED":
    case "RUN_FAILED":
    case "BUDGET_EXCEEDED":
      text = describeRun(type, payload);
      break;
    case "STEP_STARTED":
    case "STEP_COMPLETED":
    case "STEP_REJECTED":
    case "STEP_BLOCKED":
    case "STEP_UNBLOCKED":
    case "EXECUTION_STARTED":
    case "EXECUTION_PAUSED":
    case "EXECUTION_RESUMED":
    case "EXECUTION_COMPLETED":
    case "GIT_PUSH_FAILED":
      text = describeStep(type, payload);
      break;
    case "REVIEW_STARTED":
    case "REVIEW_COMPLETED":
    case "REVIEW_FAILED":
    case "COMPLIANCE_REPORT_CREATED":
      text = describeReview(type, payload);
      break;
    case "CONNECTION_INVALID":
    case "LLM_BINDING_CLEARED":
      text = describeConnection(type, payload);
      break;
    case "DEPLOYMENT_CREATED":
    case "DEPLOYMENT_UPDATED":
    case "DEPLOYMENT_FAILED":
    case "DEPLOYMENT_PREVIEW_READY":
      text = describeDeployment(type, payload);
      break;
    case "PULL_REQUEST_OPENED":
    case "PULL_REQUEST_FAILED":
    case "PULL_REQUEST_FORGOTTEN":
      text = describePullRequest(type, payload);
      break;
    case "STACK_DETECTED":
      text = describeStackDetected(payload);
      break;
    case "GIT_HOST_INSTANCE_CHANGED":
      text = describeGitHostInstance(payload);
      break;
    case "GIT_BINDING_CLEARED":
      text = describeGitBindingCleared(payload);
      break;
    case "DEPLOYMENT_TARGET_CONFIGURED":
      text = describeTargetConfigured(payload);
      break;
    case "DEPLOYMENT_TARGET_FAILED":
      text = describeTargetFailed(payload);
      break;
    case "DATABASE_PROVISIONED":
    case "DATABASE_PROVISIONING_FAILED":
    case "DATABASE_EXPIRING":
    case "DATABASE_RESET":
      text = describeDatabase(type, payload);
      break;
    case "MIGRATION_RUN_STARTED":
    case "MIGRATION_RUN_COMPLETED":
    case "MIGRATION_RUN_FAILED":
      text = describeMigrationRun(type, payload);
      break;
    case "PREVIEW_ROLLOUT_COMPLETED":
      text = describePreviewRollout(payload);
      break;
    default:
      text = describeArtifact(type, payload);
  }
  return text ?? item.summary;
}
