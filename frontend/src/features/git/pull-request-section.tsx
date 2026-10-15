import { useState } from "react";

import { Alert } from "../../components/ui/alert";
import { Badge, type BadgeTone } from "../../components/ui/badge";
import { Button } from "../../components/ui/button";
import { ErrorState, LoadingState } from "../../components/ui/states";
import { errorMessage } from "../../lib/errors";
import { useActivity } from "../activity/activity-api";
import {
  gitProviderLabels,
  pullRequestFileStatusLabels,
} from "../projects/labels";
import type { Phase, ProjectState } from "../projects/projects-api";
import { RelativeTime } from "../projects/relative-time";
import {
  pullRequestTitle,
  usePullRequest,
  useRetryPullRequest,
  type PullRequest,
  type PullRequestFile,
  type PullRequestSummary,
} from "./pull-request-api";

const fileTones: Record<PullRequestFile["status"], BadgeTone> = {
  added: "success",
  modified: "info",
  deleted: "danger",
  renamed: "warning",
  copied: "neutral",
  changed: "neutral",
};

/** base ← head, as GitHub shows it (read out as "base from head"). */
export function BranchPair({ pr }: { pr: PullRequestSummary }) {
  return (
    <span className="font-mono text-sm">
      {pr.baseBranch} <span aria-hidden="true">←</span>
      <span className="sr-only">from</span> {pr.headBranch}
    </span>
  );
}

/** The events that decide what happened after the last step completed (or the recorded PR was forgotten). */
const outcomeEvents = new Set([
  "PULL_REQUEST_FAILED",
  "PULL_REQUEST_OPENED",
  "PULL_REQUEST_FORGOTTEN",
  "EXECUTION_COMPLETED",
]);

interface PendingProps {
  projectId: string;
  phase: Phase;
  /** Every step of the current plan completed. */
  completed: boolean;
  writer: boolean;
}

/**
 * FR-36: no PR is recorded for the current plan. The pull request job is queued with the last step (and again when the
 * Git settings change); when the newest of its outcome events is PULL_REQUEST_FAILED, the failure replaces the waiting
 * state. Writers can queue the job again after a failure, or in Review while no PR is recorded.
 */
function PullRequestPending({
  projectId,
  phase,
  completed,
  writer,
}: PendingProps) {
  const activity = useActivity(projectId, "step");
  const retry = useRetryPullRequest(projectId);
  // The newest outcome event when a retry was accepted: until a newer one arrives, the job is queued again.
  const [retriedAfter, setRetriedAfter] = useState<string | null | undefined>(
    undefined,
  );
  const latest = activity.data?.pages[0]?.items.find((e) =>
    outcomeEvents.has(e.type),
  );
  const retried =
    retriedAfter !== undefined && retriedAfter === (latest?.id ?? null);
  const failed = !retried && latest?.type === "PULL_REQUEST_FAILED";
  const retryAllowed =
    phase === "review" || (phase === "execution" && completed);
  const showRetry =
    writer && retryAllowed && !retried && (failed || phase === "review");

  const retryButton = showRetry && (
    <div>
      <Button
        variant={failed ? "primary" : "secondary"}
        pending={retry.isPending}
        pendingLabel="Retrying…"
        onClick={() => {
          const marker = latest?.id ?? null;
          retry.mutate(undefined, {
            onSuccess: () => {
              setRetriedAfter(marker);
            },
          });
        }}
      >
        Retry opening pull request
      </Button>
    </div>
  );
  const retryError = (
    <div role="alert" aria-live="assertive">
      {retry.isError && (
        <Alert
          tone="error"
          title={errorMessage(retry.error, "retryPullRequest")}
        />
      )}
    </div>
  );

  if (failed) {
    const reason =
      typeof latest.payload.reason === "string" && latest.payload.reason
        ? latest.payload.reason
        : null;
    const next = !retryAllowed
      ? "Opening it can be retried once the project is in Execution with every step completed, or in Review."
      : writer
        ? "Check the project’s Git settings and access, then retry. Saving a change to the Git settings retries it too."
        : "A Contributor or Owner can retry opening it once the Git settings and access are fixed.";
    return (
      <div className="flex flex-col gap-3">
        <Alert tone="error" title="The pull request couldn't be opened">
          {reason && <p className="break-words">{reason}</p>}
          <p className="mt-1">
            The commits are safe on the project branch. {next}
          </p>
        </Alert>
        {retryButton}
        {retryError}
      </div>
    );
  }
  if (
    phase === "review" &&
    !retried &&
    latest?.type !== "EXECUTION_COMPLETED" &&
    latest?.type !== "PULL_REQUEST_FORGOTTEN"
  ) {
    return (
      <div className="flex flex-col gap-3">
        <p className="text-sm">
          No pull request is recorded for the current plan yet.
          {writer
            ? " Retry to open it."
            : " A Contributor or Owner can retry opening it."}
        </p>
        {retryButton}
        {retryError}
      </div>
    );
  }
  return (
    <div className="flex flex-col gap-3">
      <p role="status" aria-busy="true" className="text-sm">
        <span
          aria-hidden="true"
          className="mr-2 inline-block size-3 animate-pulse rounded-full bg-primary align-middle"
        />
        Opening pull request…
      </p>
      {retryButton}
      {retryError}
    </div>
  );
}

function Comparison({ pr }: { pr: PullRequest }) {
  if (!pr.comparisonAvailable) {
    return (
      <Alert tone="warning">
        The branch comparison isn&apos;t available right now: the working copy
        or one of the branches is missing.
      </Alert>
    );
  }
  return (
    <div className="flex flex-col gap-4">
      {pr.truncated && (
        <Alert tone="info">
          This comparison is too large to show in full: only the first commits
          and files are listed.
          {pr.url ? " The pull request on GitHub has everything." : ""}
        </Alert>
      )}
      <section
        aria-labelledby="pull-request-commits"
        className="flex flex-col gap-2"
      >
        <h4 id="pull-request-commits" className="font-semibold">
          Commits ({pr.commits.length})
        </h4>
        {pr.commits.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No commits differ from {pr.baseBranch}.
          </p>
        ) : (
          <ul aria-label="Commits" className="flex flex-col gap-1 text-sm">
            {pr.commits.map((c) => (
              <li key={c.sha} className="flex gap-2">
                {c.url ? (
                  <a
                    href={c.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="font-mono text-primary underline"
                    title={c.sha}
                  >
                    {c.sha.slice(0, 7)}
                  </a>
                ) : (
                  <code className="font-mono" title={c.sha}>
                    {c.sha.slice(0, 7)}
                  </code>
                )}
                <span className="break-words">{c.subject}</span>
              </li>
            ))}
          </ul>
        )}
      </section>
      <section
        aria-labelledby="pull-request-files"
        className="flex flex-col gap-2"
      >
        <h4 id="pull-request-files" className="font-semibold">
          Files changed ({pr.files.length})
        </h4>
        {pr.files.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No files differ from {pr.baseBranch}.
          </p>
        ) : (
          <ul
            aria-label="Files changed"
            className="flex flex-col gap-1 text-sm"
          >
            {pr.files.map((f) => (
              <li key={f.path} className="flex items-center gap-2">
                <Badge tone={fileTones[f.status]}>
                  {pullRequestFileStatusLabels[f.status]}
                </Badge>
                <code className="break-all font-mono">{f.path}</code>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

function PullRequestDetails({
  projectId,
  summary,
}: {
  projectId: string;
  summary: PullRequestSummary;
}) {
  const pr = usePullRequest(projectId, true);
  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm">
        <span>{gitProviderLabels[summary.provider]}</span>
        <BranchPair pr={summary} />
        <span className="text-muted-foreground">
          Recorded <RelativeTime iso={summary.openedAt} />
        </span>
        {summary.url && (
          <a
            href={summary.url}
            target="_blank"
            rel="noopener noreferrer"
            className="text-primary underline"
          >
            View on GitHub
          </a>
        )}
      </div>
      {pr.isPending ? (
        <LoadingState label="Loading the branch comparison…" />
      ) : pr.isError ? (
        <ErrorState
          error={pr.error}
          title="The branch comparison couldn't load"
          onRetry={() => void pr.refetch()}
        />
      ) : (
        <Comparison pr={pr.data} />
      )}
    </div>
  );
}

/**
 * Execution page → Pull request (FR-34–FR-36): once every step completed, the PR (GitHub) or branch comparison (Local)
 * with its commits and changed files; while the job opens it, a waiting state; its failure, if it failed; and a retry
 * for writers after a failure or in Review with no PR.
 */
export function PullRequestSection({
  projectId,
  state,
  writer,
}: {
  projectId: string;
  state: ProjectState;
  /** Contributor, Owner or Admin: may retry opening the PR. */
  writer: boolean;
}) {
  const summary = state.pullRequest;
  const completed = state.executionStatus === "completed";
  if (!summary && !completed && state.phase !== "review") {
    return null;
  }
  return (
    <section
      id="pull-request"
      aria-labelledby="pull-request-title"
      className="flex scroll-mt-4 flex-col gap-3 rounded-xl border border-border/60 bg-card/70 shadow-sm backdrop-blur-md p-4"
    >
      <h3 id="pull-request-title" className="text-lg">
        {summary ? pullRequestTitle(summary) : "Pull request"}
      </h3>
      {summary ? (
        <PullRequestDetails projectId={projectId} summary={summary} />
      ) : (
        <PullRequestPending
          projectId={projectId}
          phase={state.phase}
          completed={completed}
          writer={writer}
        />
      )}
    </section>
  );
}
