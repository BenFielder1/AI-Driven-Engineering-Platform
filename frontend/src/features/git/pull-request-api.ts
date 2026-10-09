import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { api, type Schemas } from "../../api/client";
import { toApiError } from "../../lib/errors";
import { projectKeys, unwrap } from "../projects/projects-api";

export type PullRequest = Schemas["PullRequestDto"];
export type PullRequestSummary = Schemas["PullRequestSummaryDto"];
export type PullRequestCommit = Schemas["PullRequestCommitDto"];
export type PullRequestFile = Schemas["PullRequestFileDto"];

/**
 * FR-36: the recorded project PR with its branch comparison (members). The API answers 404 NOT_FOUND until a PR is
 * opened, so callers enable this only once /state's `pullRequest` is set. Live `step` events refresh it (FR-46).
 */
export function usePullRequest(id: string, enabled: boolean) {
  return useQuery({
    queryKey: projectKeys.pullRequest(id),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET("/api/projects/{id}/git/pull-request", {
          params: { path: { id } },
          signal,
        }),
      ),
    enabled,
  });
}

/**
 * FR-36: queues the pull request job again (Contributor/Owner) after it failed, or in Review with no PR recorded. 202
 * with no body; the outcome arrives as PULL_REQUEST_OPENED or PULL_REQUEST_FAILED. 409 PHASE_PRECONDITION_FAILED or
 * CONFLICT (a job is already queued or running).
 */
export function useRetryPullRequest(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => {
      const { error, response } = await api.POST(
        "/api/projects/{id}/git/pull-request",
        { params: { path: { id } } },
      );
      if (!response.ok) {
        throw toApiError(error, response);
      }
    },
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: projectKeys.state(id) }),
        queryClient.invalidateQueries({
          queryKey: projectKeys.pullRequest(id),
        }),
      ]),
  });
}

/** "Pull request #12" for GitHub; a Local "PR" is the recorded branch comparison, without a number. */
export function pullRequestTitle(pr: PullRequestSummary): string {
  return pr.number === null
    ? "Branch comparison"
    : `Pull request #${String(pr.number)}`;
}
