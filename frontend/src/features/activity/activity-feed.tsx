import { useState } from "react";

import { Button } from "../../components/ui/button";
import { SelectField } from "../../components/ui/select-field";
import {
  EmptyState,
  ErrorState,
  LoadingState,
} from "../../components/ui/states";
import { errorMessage } from "../../lib/errors";
import { eventCategories, eventCategoryLabels } from "../projects/labels";
import { RelativeTime } from "../projects/relative-time";
import {
  useActivity,
  type ActivityItem,
  type EventCategory,
} from "./activity-api";
import { useReleaseVersionNames } from "../versions/version-label";
import { describeActivity, type VersionNameLookup } from "./activity-summary";

function ActivityRow({ item, names }: { item: ActivityItem; names: VersionNameLookup }) {
  return (
    <li className="flex flex-col gap-0.5 py-2 text-sm sm:flex-row sm:items-baseline sm:gap-3">
      <span className="break-words sm:flex-1">{describeActivity(item, names)}</span>
      <span className="text-muted-foreground">
        {item.actor.isSystem ? "System" : item.actor.name} ·{" "}
        <RelativeTime iso={item.occurredAt} />
      </span>
    </li>
  );
}

/** Overview → Activity (FR-50): newest first, 50 per page with "Load more", filterable by category. */
export function ActivityFeed({ projectId, releaseVersionId }: { projectId: string; releaseVersionId?: string }) {
  const [category, setCategory] = useState<EventCategory | null>(null);
  const activity = useActivity(projectId, category, releaseVersionId);
  const names = useReleaseVersionNames(projectId);
  const items = activity.data?.pages.flatMap((page) => page.items) ?? [];

  let body;
  if (activity.isPending) {
    body = <LoadingState label="Loading activity…" />;
  } else if (activity.isLoadingError) {
    body = (
      <ErrorState
        error={activity.error}
        onRetry={() => void activity.refetch()}
        title="The activity couldn't load"
      />
    );
  } else if (items.length === 0) {
    body = (
      <EmptyState
        title={
          category
            ? `No ${eventCategoryLabels[category].toLowerCase()} yet`
            : "No activity yet"
        }
      />
    );
  } else {
    body = (
      <>
        <ul aria-label="Activity" className="divide-y divide-border/60">
          {items.map((item) => (
            <ActivityRow key={item.id} item={item} names={names} />
          ))}
        </ul>
        {activity.isError && (
          <p role="alert" className="mt-2 text-sm text-destructive">
            {errorMessage(activity.error)}
          </p>
        )}
        {activity.hasNextPage && (
          <Button
            variant="secondary"
            className="mt-3"
            onClick={() => void activity.fetchNextPage()}
            pending={activity.isFetchingNextPage}
            pendingLabel="Loading more…"
          >
            Load more
          </Button>
        )}
      </>
    );
  }

  return (
    <div className="flex flex-col gap-3">
      <SelectField
        label="Show"
        className="max-w-xs"
        value={category ?? ""}
        onChange={(e) => {
          const value = e.target.value;
          setCategory(eventCategories.find((c) => c === value) ?? null);
        }}
      >
        <option value="">All activity</option>
        {eventCategories.map((c) => (
          <option key={c} value={c}>
            {eventCategoryLabels[c]}
          </option>
        ))}
      </SelectField>
      <p aria-live="polite" className="sr-only">
        {activity.isSuccess
          ? `${String(items.length)} ${items.length === 1 ? "event" : "events"} shown`
          : ""}
      </p>
      {body}
    </div>
  );
}
