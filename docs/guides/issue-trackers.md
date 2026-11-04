---
title: Issue trackers
parent: Guides
nav_order: 6
---

# Issue trackers

Tevrin can import work items from **Jira**, **Linear**, **GitHub Issues** and **GitLab Issues**. Each import is a read-only snapshot (a `source_items` artifact) that agents read. Tevrin only reads from trackers.

## Connect a tracker

Issue trackers sit in their own section under **Account, Connections, Issue trackers**.

- **Jira** takes a site (`*.atlassian.net`), your account email and a scoped read-only token.
- **Linear** takes a read-only API key.
- **GitHub Issues** uses your GitHub connection and the Tevrin GitHub App's `issues: read` permission. Update the App in GitHub, then accept the change on each installation.
- **GitLab Issues** uses your GitLab connection.

Jira and Linear tokens can carry an expiry date. Tevrin warns you 14 days before it.

## Import items

Browse and pick items in the project wizard, the new-version dialog or Refinement. Re-import, remove and a drift check keep the snapshot honest. The Specification step warns about imported items that no requirement cites.

## Egress

With real trackers the API calls these hosts. Allow them if outbound traffic is filtered.

| Host | Used for |
| --- | --- |
| `*.atlassian.net` | Only `https://<site>/_edge/tenant_info`, to resolve a Jira site |
| `api.atlassian.com` | Jira REST |
| `api.linear.app` | Linear GraphQL |
| `api.github.com`, registered GitLab instances | GitHub and GitLab issues, hosts Tevrin already reaches |

`Trackers:Provider=Fake` makes no tracker calls. It is for tests and E2E only, and is never set on Railway.

From the command line, see `tevrin sources` in the [CLI guide](cli.md).
