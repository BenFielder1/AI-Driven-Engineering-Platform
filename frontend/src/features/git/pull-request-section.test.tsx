import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";

import {
  seedExecution,
  seedPullRequest,
  syncExecutionState,
} from "../../mocks/handlers/execution";
import { addEvent } from "../../mocks/handlers/lifecycle";
import { projectsDb, type StoredProject } from "../../mocks/handlers/projects";
import { contextualErrorMessages } from "../../lib/errors";
import { seedProjectAs } from "../../test/artifacts";
import { projectEvent } from "../../test/fake-live";
import { openOverviewAs } from "../../test/overview";
import { renderRoute } from "../../test/render";

const sha1 = "aaaaaaa1111111111111111111111111111111111";
const sha2 = "bbbbbbb2222222222222222222222222222222222";

/** A project in execution whose `count` steps all completed. */
function completedProject(
  setup?: (p: StoredProject) => void,
  role: "contributor" | "viewer" | "owner" = "contributor",
) {
  return seedProjectAs(role, (p) => {
    p.currentPhase = "execution";
    seedExecution(p, 2, (steps) => {
      for (const step of steps) {
        Object.assign(step, { status: "completed", attempt: 1 });
      }
    });
    setup?.(p);
  });
}

async function openExecution(project: StoredProject) {
  const utils = renderRoute(`/projects/${project.id}/execution`);
  await screen.findByRole("heading", { level: 2, name: "Execution" });
  await waitFor(() => {
    expect(screen.queryByText("Loading execution…")).not.toBeInTheDocument();
  });
  return { ...utils, user: userEvent.setup() };
}

/** EXECUTION_COMPLETED, then a dead-lettered PULL_REQUEST_FAILED. */
function failedOnce(p: StoredProject) {
  addEvent(p, {
    type: "EXECUTION_COMPLETED",
    category: "step",
    summary: "done",
    payload: { stepCount: 2 },
  });
  addEvent(p, {
    type: "PULL_REQUEST_FAILED",
    category: "step",
    summary: "failed",
    payload: {
      jobId: "j1",
      deadLettered: true,
      reason: "GitHub refused the request.",
    },
  });
}

const prSection = () =>
  screen.getByRole("region", { name: /^(Pull request|Branch comparison)/ });

describe("Execution → Pull request (FR-34–FR-36)", () => {
  it("FR36_github_pr_links_out_and_lists_the_comparison", async () => {
    const project = completedProject((p) => {
      seedPullRequest(p, {
        commits: [
          {
            sha: sha2,
            subject: "Step 2: Step 2 title",
            url: `https://github.com/acme/inventory/commit/${sha2}`,
          },
          {
            sha: sha1,
            subject: "Step 1: Step 1 title",
            url: `https://github.com/acme/inventory/commit/${sha1}`,
          },
        ],
        files: [{ path: "src/part-1.ts", status: "added" }],
      });
    });
    await openExecution(project);

    expect(
      await screen.findByRole("heading", {
        level: 3,
        name: "Pull request #12",
      }),
    ).toBeInTheDocument();
    const section = prSection();
    const link = within(section).getByRole("link", { name: "View on GitHub" });
    expect(link).toHaveAttribute(
      "href",
      "https://github.com/acme/inventory/pull/12",
    );
    expect(link).toHaveAttribute("rel", "noopener noreferrer");
    expect(link).toHaveAttribute("target", "_blank");
    expect(within(section).getByText(/main/)).toHaveTextContent(
      "main ←from aide/inventory-tracker-1a2b3c4d",
    );

    const commits = await within(section).findByRole("list", {
      name: "Commits",
    });
    const commitLink = within(commits).getByRole("link", { name: "bbbbbbb" });
    expect(commitLink).toHaveAttribute(
      "href",
      `https://github.com/acme/inventory/commit/${sha2}`,
    );
    expect(
      within(commits)
        .getAllByRole("listitem")
        .map((li) => li.textContent),
    ).toEqual(["bbbbbbbStep 2: Step 2 title", "aaaaaaaStep 1: Step 1 title"]);
    const files = within(section).getByRole("list", { name: "Files changed" });
    expect(within(files).getByRole("listitem")).toHaveTextContent(
      "Addedsrc/part-1.ts",
    );
  });

  it("FR36_local_comparison_lists_commits_and_files_with_status_labels", async () => {
    const project = completedProject((p) => {
      seedPullRequest(p, {
        provider: "local",
        number: null,
        url: null,
        commits: [{ sha: sha1, subject: "Step 1: Step 1 title", url: null }],
        files: [
          { path: "src/new.ts", status: "added" },
          { path: "src/old.ts", status: "deleted" },
          { path: "README.md", status: "modified" },
        ],
        truncated: true,
      });
    });
    await openExecution(project);

    expect(
      await screen.findByRole("heading", {
        level: 3,
        name: "Branch comparison",
      }),
    ).toBeInTheDocument();
    const section = prSection();
    expect(within(section).getByText("Local repository")).toBeInTheDocument();
    expect(
      within(section).queryByRole("link", { name: "View on GitHub" }),
    ).not.toBeInTheDocument();

    const commits = await within(section).findByRole("list", {
      name: "Commits",
    });
    expect(within(commits).queryByRole("link")).not.toBeInTheDocument();
    expect(within(commits).getByText("aaaaaaa")).toHaveAttribute("title", sha1);
    expect(
      within(commits).getByText("Step 1: Step 1 title"),
    ).toBeInTheDocument();

    const files = within(section).getByRole("list", { name: "Files changed" });
    expect(
      within(files)
        .getAllByRole("listitem")
        .map((li) => li.textContent),
    ).toEqual(["Addedsrc/new.ts", "Deletedsrc/old.ts", "ModifiedREADME.md"]);
    expect(
      within(section).getByText(/too large to show in full/),
    ).toBeInTheDocument();
  });

  it("FR36_unavailable_comparison_says_so", async () => {
    const project = completedProject((p) => {
      seedPullRequest(p, {
        provider: "local",
        number: null,
        url: null,
        comparisonAvailable: false,
      });
    });
    await openExecution(project);

    expect(
      await screen.findByText(/branch comparison isn.t available right now/),
    ).toBeInTheDocument();
  });

  it("FR36_waiting_state_until_the_live_pull_request_event", async () => {
    const project = completedProject();
    const { live } = await openExecution(project);

    expect(
      await within(prSection()).findByText("Opening pull request…"),
    ).toBeInTheDocument();

    seedPullRequest(project, {
      files: [{ path: "src/part-1.ts", status: "added" }],
    });
    syncExecutionState(project);
    live.latest.emit(
      projectEvent(project.id, "step", { type: "PULL_REQUEST_OPENED" }),
    );

    expect(
      await screen.findByRole("heading", {
        level: 3,
        name: "Pull request #12",
      }),
    ).toBeInTheDocument();
    expect(screen.queryByText("Opening pull request…")).not.toBeInTheDocument();
    expect(
      await screen.findByRole("list", { name: "Files changed" }),
    ).toBeInTheDocument();
  });

  it("FR36_failure_newer_than_completion_replaces_the_waiting_state", async () => {
    const project = completedProject((p) => {
      addEvent(p, {
        type: "EXECUTION_COMPLETED",
        category: "step",
        summary: "done",
        payload: { stepCount: 2 },
      });
      addEvent(p, {
        type: "PULL_REQUEST_FAILED",
        category: "step",
        summary: "failed",
        payload: {
          jobId: "j1",
          deadLettered: true,
          reason: "GitHub refused the request.",
        },
      });
    });
    await openExecution(project);

    const alert = await within(prSection()).findByText(
      "The pull request couldn't be opened",
    );
    expect(alert).toBeInTheDocument();
    expect(
      within(prSection()).getByText("GitHub refused the request."),
    ).toBeInTheDocument();
    expect(
      within(prSection()).getByText(
        /check the project.s Git settings and access, then retry/i,
      ),
    ).toBeInTheDocument();
    expect(
      within(prSection()).getByRole("button", {
        name: "Retry opening pull request",
      }),
    ).toBeInTheDocument();
    expect(screen.queryByText("Opening pull request…")).not.toBeInTheDocument();
  });

  it("FR36_retry_button_queues_the_job", async () => {
    const project = completedProject(failedOnce);
    const { user } = await openExecution(project);

    await user.click(
      await within(prSection()).findByRole("button", {
        name: "Retry opening pull request",
      }),
    );

    expect(
      await within(prSection()).findByText("Opening pull request…"),
    ).toBeInTheDocument();
    expect(
      screen.queryByText("The pull request couldn't be opened"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Retry opening pull request" }),
    ).not.toBeInTheDocument();
    expect(projectsDb.requests).toContainEqual({
      method: "POST",
      path: `/api/projects/${project.id}/git/pull-request`,
      body: null,
    });
    expect(project.pullRequestJobQueued).toBe(true);
  });

  it("FR36_retry_hidden_for_viewers", async () => {
    const project = completedProject(failedOnce, "viewer");
    await openExecution(project);

    expect(
      await within(prSection()).findByText(
        "The pull request couldn't be opened",
      ),
    ).toBeInTheDocument();
    expect(
      within(prSection()).getByText(
        /A Contributor or Owner can retry opening it/,
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Retry opening pull request" }),
    ).not.toBeInTheDocument();
  });

  it("FR36_retry_in_review_without_a_pull_request_shows_conflict_copy", async () => {
    const project = completedProject((p) => {
      p.currentPhase = "review";
      p.pullRequestJobQueued = true;
    });
    const { user } = await openExecution(project);

    const section = prSection();
    expect(
      within(section).getByText(
        /No pull request is recorded for the current plan yet/,
      ),
    ).toBeInTheDocument();
    await user.click(
      within(section).getByRole("button", {
        name: "Retry opening pull request",
      }),
    );

    expect(
      await within(section).findByText(
        contextualErrorMessages.retryPullRequest.CONFLICT,
      ),
    ).toBeInTheDocument();
  });

  it("FR36_retry_outside_execution_or_review_says_when_it_can_run", async () => {
    const project = completedProject((p) => {
      failedOnce(p);
      p.currentPhase = "planning";
    });
    await openExecution(project);

    expect(
      await within(prSection()).findByText(
        /can be retried once the project is in Execution/,
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Retry opening pull request" }),
    ).not.toBeInTheDocument();
  });

  it("FR36_lowercase_file_statuses_render_labels", async () => {
    const statuses = [
      "added",
      "modified",
      "deleted",
      "renamed",
      "copied",
      "changed",
    ] as const;
    const project = completedProject((p) => {
      seedPullRequest(p, {
        files: statuses.map((status) => ({ path: `src/${status}.ts`, status })),
      });
    });
    await openExecution(project);

    const files = await screen.findByRole("list", { name: "Files changed" });
    expect(
      within(files)
        .getAllByRole("listitem")
        .map((li) => li.textContent),
    ).toEqual([
      "Addedsrc/added.ts",
      "Modifiedsrc/modified.ts",
      "Deletedsrc/deleted.ts",
      "Renamedsrc/renamed.ts",
      "Copiedsrc/copied.ts",
      "Changedsrc/changed.ts",
    ]);
  });

  it("FR36_older_failure_before_a_new_completion_still_waits", async () => {
    const project = completedProject((p) => {
      addEvent(p, {
        type: "PULL_REQUEST_FAILED",
        category: "step",
        summary: "failed",
        payload: { reason: "old" },
      });
      addEvent(p, {
        type: "EXECUTION_COMPLETED",
        category: "step",
        summary: "done",
        payload: { stepCount: 2 },
      });
    });
    await openExecution(project);

    expect(
      await within(prSection()).findByText("Opening pull request…"),
    ).toBeInTheDocument();
    expect(
      screen.queryByText("The pull request couldn't be opened"),
    ).not.toBeInTheDocument();
  });

  it("FR36_no_section_before_every_step_completed", async () => {
    const project = seedProjectAs("contributor", (p) => {
      p.currentPhase = "execution";
      seedExecution(p, 2);
    });
    await openExecution(project);

    expect(
      screen.queryByRole("heading", { level: 3, name: "Pull request" }),
    ).not.toBeInTheDocument();
    expect(screen.queryByText("Opening pull request…")).not.toBeInTheDocument();
  });
});

describe("Overview → Lifecycle → Pull request (FR-36)", () => {
  it("FR36_github_pr_number_links_out_with_base_and_head", async () => {
    await openOverviewAs("viewer", { currentPhase: "review" }, (p) => {
      seedPullRequest(p);
    });

    const pr = await screen.findByRole("region", { name: "Pull request" });
    const link = within(pr).getByRole("link", {
      name: /^#12\s*\(opens on GitHub\)$/,
    });
    expect(link).toHaveAttribute(
      "href",
      "https://github.com/acme/inventory/pull/12",
    );
    expect(link).toHaveAttribute("rel", "noopener noreferrer");
    expect(pr).toHaveTextContent("main ←from aide/inventory-tracker-1a2b3c4d");
  });

  it("FR36_local_shows_the_recorded_comparison_with_a_link_to_it", async () => {
    const { project } = await openOverviewAs(
      "viewer",
      { currentPhase: "review" },
      (p) => {
        seedPullRequest(p, { provider: "local", number: null, url: null });
      },
    );

    const pr = await screen.findByRole("region", { name: "Pull request" });
    expect(
      within(pr).getByText("Branch comparison recorded"),
    ).toBeInTheDocument();
    expect(
      within(pr).getByRole("link", { name: "View the comparison" }),
    ).toHaveAttribute("href", `/projects/${project.id}/execution#pull-request`);
  });

  it("FR36_no_summary_until_a_pull_request_is_recorded", async () => {
    await openOverviewAs("owner", { currentPhase: "execution" });

    await screen.findByRole("region", { name: "Next action" });
    expect(
      screen.queryByRole("region", { name: "Pull request" }),
    ).not.toBeInTheDocument();
  });
});
