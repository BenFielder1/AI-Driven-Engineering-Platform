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
import type { ActivityItem } from "./activity-api";

type Payload = ActivityItem["payload"];

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
 * anything missing falls back to a shorter line.
 */
function describeRelease(type: string, payload: Payload): string | null {
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
      const how = payload.mergedExternally === true ? "merged on GitHub and tagged" : "merged and tagged";
      return tag ? `Released: ${how} ${tag}` : `Released: ${how}`;
    }
    case "RELEASE_AWAITING_MERGE": {
      const reasons = Array.isArray(payload.reasons) ? payload.reasons : [];
      const first: unknown = reasons[0];
      const message =
        typeof first === "object" && first !== null && "message" in first && typeof first.message === "string"
          ? first.message
          : null;
      const base = `${release} is waiting for its pull request to be merged on GitHub`;
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
        return "Merge readiness checked: a merge on GitHub will be needed";
      }
      return "Merge readiness checked";
    }
    case "RELEASE_MERGED_EXTERNALLY":
      return `${release}: the pull request was merged on GitHub`;
    case "RELEASE_FAILED": {
      if (str(payload, "errorCode") === "PR_CLOSED") {
        return `${release} failed: the pull request was closed without merging`;
      }
      if (str(payload, "errorCode") === "MERGE_TIMEOUT") {
        return `${release} failed: the pull request wasn't merged within 14 days`;
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

/** LLM connection events (FR-78, FR-79). */
function describeConnection(type: string, payload: Payload): string | null {
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
    case "PULL_REQUEST_OPENED":
    case "PULL_REQUEST_FAILED":
    case "PULL_REQUEST_FORGOTTEN":
      text = describePullRequest(type, payload);
      break;
    default:
      text = describeArtifact(type, payload);
  }
  return text ?? item.summary;
}
