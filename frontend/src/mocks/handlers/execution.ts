import { http, HttpResponse } from "msw";

import type { Schemas } from "../../api/client";
import { antiforgeryFailure, nextId } from "./auth";
import { problem } from "./problem";
import {
  access,
  currentVersionId,
  executionRunStatus,
  record,
  releaseVersionName,
  type Access,
  type StoredExecutionStep,
  type StoredProject,
} from "./projects";

type Artifact = Schemas["ArtifactDto"];
type FileAction = Schemas["FileAction"];

const planTypes: readonly Schemas["ArtifactType"][] = [
  "implementation_plan",
  "optimization_plan",
];

export interface MockPlanStep {
  number: number;
  title: string;
  task: string;
  files: { path: string; action: FileAction }[];
  dependencies: number[];
  userInstructions?: string | null;
}

/** A plan document as the backend renders it (PlanDocument.Render): a readable list, then the aide-plan block. */
export function planMarkdown(
  steps: readonly MockPlanStep[],
  title = "Implementation plan",
): string {
  const prose = steps
    .map((s) => `## Step ${String(s.number)}: ${s.title}\n\n${s.task}`)
    .join("\n\n");
  const json = JSON.stringify({ format: 1, steps }, null, 2);
  return `# ${title}\n\n${prose}\n\n## Plan data\n\n\`\`\`aide-plan\n${json}\n\`\`\`\n`;
}

/** `count` valid steps; step n depends on step n − 1. */
export function planSteps(count: number): MockPlanStep[] {
  return Array.from({ length: count }, (_, i) => ({
    number: i + 1,
    title: `Step ${String(i + 1)} title`,
    task: `Do part ${String(i + 1)}.`,
    files: [
      { path: `src/part-${String(i + 1)}.ts`, action: "create" as const },
    ],
    dependencies: i === 0 ? [] : [i],
  }));
}

const protectedPrefixes = [
  ".git/",
  ".aide/",
  ".github/workflows/",
  ".circleci/",
  ".azure-pipelines/",
];
const lockFiles = [
  "package-lock.json",
  "yarn.lock",
  "pnpm-lock.yaml",
  "packages.lock.json",
  "cargo.lock",
  "poetry.lock",
];

/** A subset of the backend's PathRules.IsProtected (§4.6): .git/.aide, CI workflows, lockfiles and env files. */
export function isProtectedPath(path: string): boolean {
  const p = path
    .trim()
    .replaceAll("\\", "/")
    .replace(/^(\.\/)+/, "")
    .toLowerCase();
  if (protectedPrefixes.some((prefix) => `${p}/`.startsWith(prefix))) {
    return true;
  }
  const name = p.split("/").at(-1) ?? "";
  return (
    lockFiles.includes(name) || name === ".env" || name.startsWith(".env.")
  );
}

const blockPattern = /^```aide-plan[ \t]*\n([\s\S]*?)\n```[ \t]*$/gm;

/**
 * A subset of the backend's PlanDocument.ParseMarkdown + PlanValidator (FR-29): exactly one block, readable JSON,
 * steps numbered 1..n, titles, at least one file, and dependencies on earlier steps only. Every reason is returned.
 */
export function validatePlan(
  markdown: string,
): { steps: MockPlanStep[] } | { reasons: string[] } {
  const blocks = [...markdown.replaceAll("\r\n", "\n").matchAll(blockPattern)];
  if (blocks.length === 0) {
    return {
      reasons: [
        "The plan has no ```aide-plan block with the structured steps.",
      ],
    };
  }
  if (blocks.length > 1) {
    return {
      reasons: [
        `The plan has ${String(blocks.length)} \`\`\`aide-plan blocks; keep exactly one.`,
      ],
    };
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(blocks[0]?.[1] ?? "");
  } catch (e) {
    return {
      reasons: [
        `The plan is not valid JSON (${e instanceof Error ? e.message : "unreadable"}).`,
      ],
    };
  }
  const raw = Array.isArray(parsed)
    ? parsed
    : (parsed as { steps?: unknown } | null)?.steps;
  if (!Array.isArray(raw)) {
    return { reasons: ['The plan JSON needs a "steps" array.'] };
  }
  const steps = raw as Partial<MockPlanStep>[];
  const reasons: string[] = [];
  if (steps.length === 0) {
    reasons.push("The plan has no steps.");
  }
  steps.forEach((step, i) => {
    const n = String(step.number ?? "?");
    if (step.number !== i + 1) {
      reasons.push(
        `Steps must be numbered 1..n in order: position ${String(i + 1)} has number ${n}.`,
      );
    }
    if (!(step.title ?? "").trim()) {
      reasons.push(`Step ${n} has no title.`);
    }
    for (const dep of step.dependencies ?? []) {
      if (dep >= (step.number ?? 0) || dep < 1) {
        reasons.push(
          `Step ${n} depends on step ${String(dep)}; dependencies must point to earlier steps.`,
        );
      }
    }
    if ((step.files ?? []).length === 0) {
      reasons.push(
        `Step ${n} declares no files; every step must create or modify at least one file.`,
      );
    }
  });
  return reasons.length > 0 ? { reasons } : { steps: steps as MockPlanStep[] };
}

/** 400 INVALID_PLAN as the API sends it: the first reason in the title, every reason in `details`. */
export const invalidPlan = (reasons: string[]) =>
  problem(400, "INVALID_PLAN", `The plan is invalid: ${reasons[0] ?? ""}`, {
    details: reasons,
  });

/** FR-30: one pending step per plan step for the approved plan version (once per version). */
export function createExecutionSteps(
  project: StoredProject,
  plan: Artifact,
  steps: readonly MockPlanStep[],
) {
  if (project.executionSteps.some((s) => s.planArtifactId === plan.id)) {
    return;
  }
  project.executionSteps.push(
    ...steps.map((s): StoredExecutionStep => ({
      id: nextId(),
      planArtifactId: plan.id,
      number: s.number,
      title: s.title,
      task: s.task,
      declaredFiles: s.files.map((f) => ({
        ...f,
        protected: isProtectedPath(f.path),
      })),
      dependencies: s.dependencies,
      userInstructions: s.userInstructions ?? null,
      status: "pending",
      attempt: 0,
      violations: null,
      branchName: null,
      commitSha: null,
      commitUrl: null,
      startedAt: null,
      completedAt: null,
      fileManifest: null,
    })),
  );
}

/**
 * The steps of the current plan, in step order. As in the backend (ExecutionQueries.CurrentPlanIdAsync), the current
 * plan is the most recently created locked plan: the implementation plan, or a review cycle's optimisation plan.
 */
export function currentPlanSteps(
  project: StoredProject,
): StoredExecutionStep[] {
  const plan = project.artifacts
    .filter((x) => planTypes.includes(x.type) && x.status === "locked")
    .sort(
      (x, y) => y.createdAt.localeCompare(x.createdAt) || y.version - x.version,
    )[0];
  return plan
    ? project.executionSteps
        .filter((s) => s.planArtifactId === plan.id)
        .sort((x, y) => x.number - y.number)
    : [];
}

/** ExecutionCountsDto over the current plan's steps. */
export function executionCounts(
  project: StoredProject,
): Schemas["ExecutionCountsDto"] {
  const steps = currentPlanSteps(project);
  const count = (status: Schemas["StepStatus"]) =>
    steps.filter((s) => s.status === status).length;
  return {
    total: steps.length,
    pending: count("pending"),
    inProgress: count("in_progress"),
    completed: count("completed"),
    rejected: count("rejected"),
    blocked: count("blocked"),
  };
}

/** Recomputes the /state execution counts from the stored steps (call after changing steps in a test). */
export function syncExecutionState(project: StoredProject) {
  project.state.execution = executionCounts(project);
}

/**
 * Seeds an approved implementation plan with `count` steps (FR-30) and lets `setup` change them (status, attempt,
 * violations, commit…). The project is put in the execution phase and the /state counts follow the steps.
 */
export function seedExecution(
  project: StoredProject,
  count: number,
  setup?: (steps: StoredExecutionStep[]) => void,
): StoredExecutionStep[] {
  const steps = planSteps(count);
  const plan: Artifact = {
    id: nextId(),
    type: "implementation_plan",
    version: 1,
    parentVersion: null,
    content: planMarkdown(steps),
    contentHash: "sha256:plan",
    status: "locked",
    releaseVersionId: currentVersionId(project),
    releaseVersion: releaseVersionName(project, currentVersionId(project)),
    restoredFromVersion: null,
    checkpointAtCreation: project.checkpoint,
    author: { user: null, agentRunId: nextId() },
    approvedBy: null,
    approvedAt: "2026-09-01T10:00:00Z",
    rejectionComment: null,
    createdAt: "2026-09-01T10:00:00Z",
    integrity: "ok",
    warnings: [],
  };
  project.artifacts.push(plan);
  project.currentPhase = "execution";
  project.status = "building";
  createExecutionSteps(project, plan, steps);
  const stored = currentPlanSteps(project);
  setup?.(stored);
  syncExecutionState(project);
  return stored;
}

/**
 * Seeds a locked optimisation plan (a review cycle's, §4.4) with `count` steps after the implementation plan, making
 * it the current plan: /state's artifact summary and counts follow it.
 */
export function seedOptimizationPlan(
  project: StoredProject,
  count: number,
  version = 1,
  setup?: (steps: StoredExecutionStep[]) => void,
  /** FR-57: the review cycle that drafted it (the review-fix run's cycle). */
  cycle?: number,
): StoredExecutionStep[] {
  const steps = planSteps(count).map((s) => ({
    ...s,
    title: `Optimisation ${String(s.number)} title`,
  }));
  const plan: Artifact = {
    id: nextId(),
    type: "optimization_plan",
    version,
    parentVersion: version > 1 ? version - 1 : null,
    content: planMarkdown(steps, "Optimisation plan"),
    contentHash: "sha256:optimisation-plan",
    status: "locked",
    releaseVersionId: currentVersionId(project),
    releaseVersion: releaseVersionName(project, currentVersionId(project)),
    restoredFromVersion: null,
    checkpointAtCreation: project.checkpoint,
    author: { user: null, agentRunId: nextId() },
    approvedBy: null,
    approvedAt: "2026-09-02T10:00:00Z",
    rejectionComment: null,
    createdAt: "2026-09-02T10:00:00Z",
    integrity: "ok",
    warnings: [],
  };
  project.artifacts.push(plan);
  if (cycle !== undefined) {
    (project.planCycles ??= {})[plan.id] = cycle;
  }
  const summary = {
    type: "optimization_plan" as const,
    latestVersion: version,
    latestStatus: "locked" as const,
    currentLockedVersion: version,
  };
  const existing = project.state.artifacts.find(
    (a) => a.type === "optimization_plan",
  );
  if (existing) {
    Object.assign(existing, summary);
  } else {
    project.state.artifacts.push(summary);
  }
  createExecutionSteps(project, plan, steps);
  const stored = currentPlanSteps(project);
  setup?.(stored);
  syncExecutionState(project);
  return stored;
}

function executionState(project: StoredProject): Schemas["ExecutionStateDto"] {
  syncExecutionState(project);
  return {
    status: executionRunStatus(project),
    paused: project.executionPaused,
    currentStepNumber:
      currentPlanSteps(project).find((s) => s.status === "in_progress")
        ?.number ?? null,
    counts: project.state.execution,
  };
}

/** Writer (Contributor/Owner/Admin) access for execution control, including antiforgery. */
function writer(request: Request, id: unknown): Access {
  const a = access(id);
  if (a instanceof Response) {
    return a;
  }
  const rejected = antiforgeryFailure(request);
  if (rejected) {
    return rejected;
  }
  return a.level === "viewer"
    ? problem(403, "FORBIDDEN", "Viewers can’t control execution.")
    : a;
}

/** §6: start · pause · resume. All need the execution phase; start and resume also need budget left (FR-26). */
function control(action: "start" | "pause" | "resume") {
  return http.post(
    `*/api/projects/:id/execution/${action}`,
    async ({ request, params }) => {
      const a = writer(request, params.id);
      if (a instanceof Response) {
        return a;
      }
      await record(request);
      const p = a.project;
      if (p.currentPhase !== "execution") {
        return problem(
          409,
          "PHASE_PRECONDITION_FAILED",
          "Execution can be controlled only in the execution phase.",
          {
            details: [`The project is in ${p.currentPhase}, not execution.`],
          },
        );
      }
      if (
        action !== "pause" &&
        p.tokenBudget !== null &&
        p.tokensUsed >= p.tokenBudget
      ) {
        return problem(
          409,
          "BUDGET_EXCEEDED",
          "The project has used its token budget.",
        );
      }
      if (action === "start") {
        // FR-31, FR-33: pending steps, rejected ones (retried first) and an interrupted in_progress step can run.
        const c = executionCounts(p);
        if (c.pending + c.rejected + c.inProgress === 0) {
          return problem(409, "PHASE_PRECONDITION_FAILED", "Nothing to run.", {
            details: [
              "The current plan has no pending, rejected or interrupted step to run.",
            ],
          });
        }
      }
      if (action === "pause") {
        p.executionPaused = true;
      } else {
        p.executionPaused = false;
        p.executionStarted = true;
      }
      return HttpResponse.json<Schemas["ExecutionStateDto"]>(
        executionState(p),
        { status: 202 },
      );
    },
  );
}

/** FR-36: records a pull request (GitHub by default) with a branch comparison; `overrides` change any field. */
export function seedPullRequest(
  project: StoredProject,
  overrides: Partial<Schemas["PullRequestDto"]> = {},
): Schemas["PullRequestDto"] {
  const pr: Schemas["PullRequestDto"] = {
    provider: "github_pat",
    baseBranch: "main",
    headBranch: "aide/inventory-tracker-1a2b3c4d",
    number: 12,
    url: "https://github.com/acme/inventory/pull/12",
    openedAt: "2026-09-01T12:00:00Z",
    comparisonAvailable: true,
    baseSha: "1111111111111111111111111111111111111111",
    headSha: "2222222222222222222222222222222222222222",
    commits: [],
    files: [],
    truncated: false,
    ...overrides,
  };
  project.pullRequest = pr;
  return pr;
}

type Run = Schemas["ExecutionRunDto"];
type Attempt = Schemas["StepAttemptDto"];

/** FR-57: seeds the attempt history of a step (oldest first); each attempt gets a fake agent run unless `run: null`. */
export function seedStepAttempts(
  project: StoredProject,
  step: Pick<StoredExecutionStep, "id" | "agentKey">,
  attempts: readonly Partial<Attempt>[],
): Attempt[] {
  const seeded = attempts.map((a, i): Attempt => ({
    attempt: i + 1,
    outcome: i === attempts.length - 1 ? "accepted" : "rejected",
    createdAt: new Date(Date.parse("2026-09-01T10:00:00Z") + i * 600_000).toISOString(),
    violations: [],
    warnings: [],
    producedFiles: [],
    run: {
      id: nextId(),
      agent: "code_generator",
      agentKey: step.agentKey ?? null,
      model: "claude-opus-5-5",
      inputTokens: 1_200,
      outputTokens: 3_400,
      durationMs: 42_000,
      status: "succeeded",
    },
    toolCalls: [],
    ...a,
  }));
  (project.stepAttempts ??= {})[step.id] = seeded;
  return seeded;
}

/** Mirrors the backend's run status (FR-57): superseded, else derived from the steps and the runner. */
function runStatus(project: StoredProject, planId: string, steps: StoredExecutionStep[], superseded: boolean): Schemas["PlanRunStatus"] {
  if (superseded) {
    return "superseded";
  }
  if (steps.length > 0 && steps.every((s) => s.status === "completed")) {
    return "completed";
  }
  if (steps.some((s) => s.status === "blocked")) {
    return "blocked";
  }
  if (steps.every((s) => s.status === "pending")) {
    return "pending";
  }
  const current = currentPlanSteps(project)[0]?.planArtifactId === planId;
  return current && project.executionPaused ? "paused" : "running";
}

/** FR-57: GET /execution/runs of one release version: the development run first, then review-fix runs by cycle. */
export function executionRuns(project: StoredProject, releaseVersionId: string): Run[] {
  const plans = project.artifacts.filter(
    (x) => planTypes.includes(x.type) && x.releaseVersionId === releaseVersionId && project.executionSteps.some((s) => s.planArtifactId === x.id),
  );
  const cycleOf = (planId: string) =>
    project.planCycles?.[planId] ?? project.complianceReports?.find((r) => r.optimizationPlanArtifactId === planId)?.cycle ?? null;
  const ordered = [...plans].sort(
    (x, y) =>
      (x.type === "implementation_plan" ? 0 : 1) - (y.type === "implementation_plan" ? 0 : 1) ||
      (cycleOf(x.id) ?? 0) - (cycleOf(y.id) ?? 0) ||
      x.version - y.version,
  );
  return ordered.map((plan): Run => {
    const steps = project.executionSteps.filter((s) => s.planArtifactId === plan.id).sort((a, b) => a.number - b.number);
    const superseded = plan.status === "invalidated" || steps.every((s) => s.status === "invalidated");
    const started = steps.flatMap((s) => (s.startedAt ? [s.startedAt] : [])).sort();
    const completed = steps.flatMap((s) => (s.completedAt ? [s.completedAt] : [])).sort();
    const status = runStatus(project, plan.id, steps, superseded);
    const stepIds = new Set(steps.map((s) => s.id));
    const tokens = project.runs
      .filter((r) => r.stepId !== null && stepIds.has(r.stepId))
      .reduce((sum, r) => sum + r.inputTokens + r.outputTokens, 0);
    return {
      id: plan.id,
      planType: plan.type,
      planVersion: plan.version,
      releaseVersionId: plan.releaseVersionId,
      cycle: plan.type === "optimization_plan" ? cycleOf(plan.id) : null,
      complianceReportId:
        plan.type === "optimization_plan"
          ? (project.complianceReports?.find((r) => r.optimizationPlanArtifactId === plan.id)?.id ?? null)
          : null,
      status,
      superseded,
      supersededReason: superseded
        ? (project.supersededReasons?.[plan.id] ?? "A newer approved plan invalidated this plan.")
        : null,
      stepCount: steps.length,
      completedCount: steps.filter((s) => s.status === "completed").length,
      startedAt: started[0] ?? null,
      completedAt: status === "completed" ? (completed.at(-1) ?? null) : null,
      commitCount: steps.filter((s) => s.commitSha !== null).length,
      tokens,
      steps: steps.map((s) => ({
        id: s.id,
        number: s.number,
        title: s.title,
        agentKey: s.agentKey ?? null,
        status: s.status,
        attempt: s.attempt,
        declaredFiles: s.declaredFiles,
        producedFiles: s.fileManifest ?? [],
        commitSha: s.commitSha,
        commitUrl: s.commitUrl,
      })),
    };
  });
}

export const executionHandlers = [
  // FR-36: the recorded PR and its branch comparison; 404 until one is opened.
  http.get("*/api/projects/:id/git/pull-request", ({ params }) => {
    const a = access(params.id);
    if (a instanceof Response) {
      return a;
    }
    return a.project.pullRequest
      ? HttpResponse.json<Schemas["PullRequestDto"]>(a.project.pullRequest)
      : problem(
          404,
          "NOT_FOUND",
          "No pull request has been opened for this project yet.",
        );
  }),
  // FR-36: queue the pull request job again (writers): in execution with every step completed, or in review, while no
  // PR is recorded and no job is queued. 202 with no body.
  http.post(
    "*/api/projects/:id/git/pull-request",
    async ({ request, params }) => {
      const a = writer(request, params.id);
      if (a instanceof Response) {
        return a;
      }
      await record(request);
      const p = a.project;
      const completed = executionRunStatus(p) === "completed";
      if (
        !(
          p.currentPhase === "review" ||
          (p.currentPhase === "execution" && completed)
        ) ||
        p.pullRequest
      ) {
        return problem(
          409,
          "PHASE_PRECONDITION_FAILED",
          "The pull request cannot be opened again right now.",
          {
            details: [
              p.pullRequest
                ? "A pull request is already recorded for the current plan."
                : `The project is in ${p.currentPhase} and not every step is completed.`,
            ],
          },
        );
      }
      if (p.pullRequestJobQueued) {
        return problem(
          409,
          "CONFLICT",
          "A pull request job is already queued or running.",
        );
      }
      p.pullRequestJobQueued = true;
      return new HttpResponse(null, { status: 202 });
    },
  ),
  control("start"),
  control("pause"),
  control("resume"),
  // FR-57: every execution run of a release version (default: the current one).
  http.get("*/api/projects/:id/execution/runs", ({ request, params }) => {
    const a = access(params.id);
    if (a instanceof Response) {
      return a;
    }
    const scope = new URL(request.url).searchParams.get("releaseVersionId") ?? currentVersionId(a.project);
    if (!a.project.releaseVersions.some((v) => v.id === scope)) {
      return problem(404, "NOT_FOUND", "The release version was not found in this project.");
    }
    return HttpResponse.json<Run[]>(executionRuns(a.project, scope));
  }),
  // FR-57: a step's attempt history, oldest first.
  http.get("*/api/projects/:id/execution/steps/:stepId/attempts", ({ params }) => {
    const a = access(params.id);
    if (a instanceof Response) {
      return a;
    }
    if (!a.project.executionSteps.some((s) => s.id === params.stepId)) {
      return problem(404, "NOT_FOUND", "The execution step was not found.");
    }
    return HttpResponse.json<Attempt[]>(a.project.stepAttempts?.[String(params.stepId)] ?? []);
  }),
  // FR-30: the steps of a plan version (default: the current locked implementation plan, as in the API).
  http.get("*/api/projects/:id/execution/steps", ({ request, params }) => {
    const a = access(params.id);
    if (a instanceof Response) {
      return a;
    }
    const q = new URL(request.url).searchParams;
    const type = (q.get("planType") ??
      "implementation_plan") as Schemas["ArtifactType"];
    if (!planTypes.includes(type)) {
      return problem(
        400,
        "VALIDATION",
        "One or more validation errors occurred.",
        {
          errors: {
            planType: [
              "Steps belong to implementation_plan or optimization_plan versions.",
            ],
          },
        },
      );
    }
    const versionParam = q.get("planVersion");
    const scope = q.get("releaseVersionId") ?? currentVersionId(a.project);
    const plans = a.project.artifacts.filter((x) => x.type === type && x.releaseVersionId === scope);
    const plan =
      versionParam === null
        ? plans
            .filter((x) => x.status === "locked")
            .sort((x, y) => y.version - x.version)[0]
        : plans.find((x) => x.version === Number(versionParam));
    if (!plan) {
      return versionParam === null
        ? HttpResponse.json<Schemas["ExecutionStepsDto"]>({
            plan: null,
            steps: [],
          })
        : problem(
            404,
            "NOT_FOUND",
            `Version ${versionParam} of the ${type} was not found.`,
          );
    }
    const steps = a.project.executionSteps
      .filter((s) => s.planArtifactId === plan.id)
      .sort((x, y) => x.number - y.number)
      // eslint-disable-next-line @typescript-eslint/no-unused-vars
      .map(({ planArtifactId, ...dto }) => dto);
    return HttpResponse.json<Schemas["ExecutionStepsDto"]>({
      plan: {
        artifactId: plan.id,
        type: plan.type,
        version: plan.version,
        status: plan.status,
      },
      steps,
    });
  }),
];
