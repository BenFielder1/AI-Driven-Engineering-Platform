import { useInfiniteQuery } from "@tanstack/react-query";

import { api, type Schemas } from "../../api/client";
import { projectKeys, unwrap } from "../projects/projects-api";

export type ActivityItem = Schemas["ActivityItemDto"];
export type ActivityPage = Schemas["ActivityPageDto"];
export type EventCategory = NonNullable<Schemas["EventCategory"]>;

/** FR-50: events per page ("the latest 50 with load more"). */
export const ACTIVITY_PAGE_SIZE = 50;

/**
 * FR-50: the project's activity feed, newest first, optionally filtered by category; "load more" follows `nextBefore`.
 * `releaseVersionId` limits it to a past release version (FR-62); without it, the whole history.
 */
export function useActivity(id: string, category: EventCategory | null, releaseVersionId?: string) {
  return useInfiniteQuery({
    queryKey: [...projectKeys.activity(id, category), releaseVersionId ?? 'all'],
    queryFn: async ({ pageParam, signal }) =>
      unwrap(
        await api.GET("/api/projects/{id}/activity", {
          params: {
            path: { id },
            query: {
              limit: ACTIVITY_PAGE_SIZE,
              ...(category ? { type: category } : {}),
              ...(pageParam ? { before: pageParam } : {}),
              ...(releaseVersionId ? { releaseVersionId } : {}),
            },
          },
          signal,
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextBefore ?? undefined,
  });
}
