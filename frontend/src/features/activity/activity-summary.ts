import {
  agentKindLabels,
  agentKinds,
  artifactTypeLabels,
  artifactTypes,
  escalationDecisionLabels,
  escalationDecisions,
  formatDuration,
  formatNumber,
  gateTypeLabels,
  gateTypes,
  phaseLabels,
  phases,
} from "../projects/labels";
import type { ActivityItem } from "./activity-api";

type Payload = ActivityItem["payload"];

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

/** `ARTIFACT_APPROVED` → "Artifact approved". */
function humanizeType(type: string): string {
  const words = type.toLowerCase().replaceAll("_", " ");
  return words.length === 0
    ? type
    : words.charAt(0).toUpperCase() + words.slice(1);
}

function describePhaseChange(payload: Payload): string | null {
  const from = oneOf(payload, "from", phases);
  const to = oneOf(payload, "to", phases);
  if (!from || !to) {
    return null;
  }
  const v = version(payload, "projectVersion");
  const text = `Phase changed from ${phaseLabels[from]} to ${phaseLabels[to]}`;
  return v === null ? text : `${text} (version ${String(v)})`;
}

function describeGate(type: string, payload: Payload): string | null {
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
      const target = version(payload, "targetVersion");
      const label = escalationDecisionLabels[decision];
      return decision === "rollback" && target !== null
        ? `${gate} decided: ${label} to version ${String(target)}`
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

function describeRollback(payload: Payload): string | null {
  const from = version(payload, "fromVersion");
  const target = version(payload, "targetVersion");
  return from === null || target === null
    ? null
    : `Rolled back from version ${String(from)} to version ${String(target)}`;
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

/** A non-empty string, or `null`. */
function str(payload: Payload, key: string): string | null {
  const value = payload[key];
  return typeof value === "string" && value.trim() ? value.trim() : null;
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

/** Events whose payload names an artifact type and version (e.g. `ARTIFACT_APPROVED`). */
function describeArtifact(type: string, payload: Payload): string | null {
  const artifactType = oneOf(payload, "type", artifactTypes);
  const v = version(payload, "version");
  return artifactType && v !== null
    ? `${humanizeType(type)}: ${artifactTypeLabels[artifactType]} v${String(v)}`
    : null;
}

/**
 * FR-50: the one-line description shown in the activity feed, built from the event's type and payload with the same
 * labels the rest of the UI uses. Unknown types and payloads that don't match the expected shape fall back to the
 * server's `summary`.
 */
export function describeActivity(
  item: Pick<ActivityItem, "type" | "payload" | "summary">,
): string {
  const { type, payload } = item;
  let text: string | null;
  switch (type) {
    case "PHASE_CHANGED":
      text = describePhaseChange(payload);
      break;
    case "GATE_CREATED":
    case "GATE_APPROVED":
    case "GATE_REJECTED":
    case "GATE_CANCELLED":
    case "GATE_OVERDUE":
      text = describeGate(type, payload);
      break;
    case "ROLLBACK_COMPLETED":
      text = describeRollback(payload);
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
