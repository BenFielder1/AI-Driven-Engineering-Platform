# Issue tracker connections: Jira, Linear and others

*Research note for v5 planning, 2026-10-28. Not a spec: nothing here is a requirement until it lands in `SPEC.md`.*

## Summary

**Yes, this can be done, and it fits AIDE's existing model well. Recommendation: add an "Issue trackers" section to
Account → Connections, put the trackers behind one `IIssueTrackerProvider` abstraction, and let a project import a set
of tracker items as an immutable, hashed source snapshot. That snapshot feeds Idea Refinement and the Spec Generator.
Ship Jira, Linear, GitHub Issues and GitLab Issues first. Keep the first cut read-only.**

- **Jira Cloud** and **Linear** both have stable public APIs, token auth that doesn't need a registered OAuth app, and
  a free plan with API access:
  - Jira Free: up to 10 users
  - Linear Free: unlimited members, 2 teams, 250 open issues
- **The connection pattern already exists.** v4 platform connections (FR-142) are a pasted token with a label,
  validated on save, encrypted, and shown with account, scope, expiry and status. Tracker tokens fit that model.
- **GitHub Issues and GitLab Issues come almost for free.** AIDE already holds credentials for both hosts:
  - the GitHub App needs one more permission, **Issues: read**, amended the same way FR-143 added Pages
  - GitLab tokens with `read_api` already reach issues
- **Where it plugs in:**
  - The project wizard's "Project idea" step (FR-5) gains **Import from tracker**.
  - The selected items are rendered to Markdown and stored as a versioned `source_items` input.
  - Refinement and spec prompts get a new `{{SOURCE_ITEMS}}` placeholder (FR-24).
  - Spec requirements can cite the issue keys they came from, which gives traceability back to the tracker.
- **Leave for later:**
  - write-back (comments, links, status changes, creating issues from plan steps)
  - webhooks
  - OAuth "Sign in with Atlassian/Linear" flows

  Each adds a public callback or inbound endpoint, and new failure modes, for a feature whose core value is reading.

## 1. What "drawing tasks from a tracker" means for AIDE

AIDE's lifecycle starts from free idea text (FR-5, FR-27). A tracker connection replaces or enriches that text with
structured work the team has already written:

| Lifecycle step | What the tracker adds |
| --- | --- |
| Project creation | Pick an epic, project, label, saved filter or hand-picked issues as the source of the idea |
| Refinement (FR-27) | The agent sees the issues' titles, descriptions, acceptance criteria, priorities and hierarchy, and asks clarifying questions about gaps and conflicts between them |
| Specification (FR-28) | Functional requirements carry a `Source: PROJ-123` reference. The spec-verifier can flag imported items that no requirement covers |
| Planning | Plan steps can reference the issues they deliver *(later: create or update issues from the plan)* |
| Review / Release | *(later)* Post a comment or link on each source issue when its requirement is released |

### Recommended rules (to become spec rules)

- **Snapshots, not live reads.** An import fetches the items once and stores:
  - the normalised items
  - the rendered Markdown
  - a SHA-256 content hash
  - the provider, query and fetch time

  Agents read only the snapshot, so runs stay deterministic and reproducible (the same reasoning as rule 9 for
  artifacts).
- **Re-import makes a new snapshot version**, and approving a newer snapshot invalidates downstream artifacts through
  the existing §4.4 cascade. AIDE never silently refreshes.
- **Tracker content is untrusted input.** Issue descriptions and comments are written by anyone with tracker access.
  They are a prompt-injection channel, so:
  - wrap them in a clearly delimited data block in the prompt templates
  - never let them change tool permissions or gate behaviour
- **Size limits:**
  - cap the item count per import (for example, 200)
  - cap the rendered size against the refinement model's context window, using v4's model catalog (FR-157 onwards)
  - comments are opt-in
  - attachments aren't imported in the first cut; only their names are listed
- **Read-only scopes.** v5 asks for read scopes only. Write-back would be a later, separately approved change with its
  own scopes.

## 2. Architecture fit

- **Provider abstraction.** One `IIssueTrackerProvider` in `Aide.Infrastructure`, the same shape as `IGitProvider`.
  It has four operations:
  - `ValidateAsync`: who the token is, which sites or workspaces it reaches, its scopes and expiry
  - `ListContainersAsync`: projects, teams or repos to browse
  - `SearchAsync`: by native query or by structured filter
  - `GetItemsAsync`: full items with children
- **Fake provider.** A Fake provider with fixtures serves tests (rule 10's spirit: no test hits a real tracker). Record
  real API responses from free test accounts as JSON fixtures for the adapters' unit tests.
- **Normalised item** (pure record in `Aide.Domain`; the Markdown rendering is a pure, unit-tested function):

  | Field | Notes |
  | --- | --- |
  | `provider`, `externalId`, `key`, `url` | `key` is the human key: `PROJ-123`, `ENG-42`, `owner/repo#7` |
  | `type` | normalised to `epic`, `story`, `task`, `bug`, `subtask` or `other`, with the native type kept |
  | `title`, `descriptionMarkdown` | converted from Jira ADF, Azure DevOps HTML and similar formats where needed |
  | `status`, `statusCategory` | `statusCategory` is `todo`, `in_progress` or `done` |
  | `priority`, `labels`, `assignee?`, `estimate?` | assignee is off by default (personal data) |
  | `parentKey`, `childKeys[]`, `links[]` | hierarchy and "blocks / relates to" |
  | `acceptanceCriteria?` | a custom field where the tracker has one, otherwise left inside the description |
  | `comments[]?` | opt-in |
  | `updatedAt` | used for "changed since import" checks |

- **Connections.** These are a new connection category in the FR-142 generic model, so the migration adds rows, not
  tables. They're validated on save, stored encrypted, never returned, and marked `invalid` or `insufficient_scope` by
  the same rules.
- **Background work.** Imports run as jobs on the existing PostgreSQL job table (D1), with the provider's 429 handling
  and `Retry-After` honoured. They aren't calls made from endpoints.
- **MCP alternative (considered, not recommended for v5).** Atlassian (Rovo MCP, `mcp.atlassian.com`) and Linear
  (`mcp.linear.app/mcp`) both host official MCP servers. Letting agents call those tools would mean:
  - non-deterministic reads in the middle of a run
  - harder faking in tests
  - per-user OAuth 2.1 flows

  The REST/GraphQL adapters above keep reads in AIDE's job model. The MCP route belongs with the "MCP tools" v5 item in
  [`MICROSOFT_AGENT_FRAMEWORK.md`](MICROSOFT_AGENT_FRAMEWORK.md), if it's pursued at all.

## 3. Jira Cloud

| Item | Detail |
| --- | --- |
| Free plan | **Jira Free:** up to 10 users, 2 GB storage, 100 automation runs a month, no time limit. Create a site at atlassian.com/software/jira/free |
| API | REST v3, `https://{site}.atlassian.net/rest/api/3/…` |
| Auth (recommended) | **Scoped API token** from id.atlassian.com → Security → API tokens → *Create API token with scopes*. Basic auth with `email:token`. It **must** call the gateway `https://api.atlassian.com/ex/jira/{cloudId}/rest/api/3/…`. A scoped token sent to the site URL returns 401 |
| Finding `cloudId` | `GET https://{site}.atlassian.net/_edge/tenant_info` returns it *(secondary source)*. Ask the user for the site URL and resolve the `cloudId` on save |
| Scopes | Read-only: classic `read:jira-work` + `read:jira-user`, or their granular equivalents. Confirm the exact granular names when building |
| Token expiry | 1–365 days and fixed at creation. Scopes can't be edited afterwards. AIDE shows the expiry and notifies before it lapses (FR-142 already shows expiry) |
| Alternative | Unscoped API tokens still work against the site URL. Atlassian recommends scoped tokens and hasn't deprecated unscoped ones. OAuth 2.0 (3LO) needs a registered app and a callback, so leave it for later |
| Search | `GET/POST /rest/api/3/search/jql` with JQL (for example, `parent = PROJ-10`, `project = PROJ AND labels = mvp`, `filter = 10042`). The old `/rest/api/3/search` was **removed** in 2025. The new endpoint pages with a `nextPageToken` |
| Hierarchy | Epic → story/task → subtask via the `parent` field, on both team-managed and company-managed projects |
| Description format | **Atlassian Document Format (ADF)**, a JSON tree, not Markdown. Write an ADF → Markdown converter in `Aide.Domain` (lossy for underline, colour and panels; acceptable). Alternatively, `expand=renderedFields` returns HTML |
| Acceptance criteria | Usually a site-specific custom field (`customfield_NNNNN`). Map it per connection with an optional "acceptance criteria field" setting, discovered from `GET /rest/api/3/field` |
| Rate limits | Cost-based per tenant, returning 429 with `Retry-After`. A one-off import of a few hundred issues is far below them |
| Known gotchas | Some endpoints still mishandle scoped tokens (for example, JRACLOUD-94547, "Get valid project key"). Avoid them, or fall back to unscoped tokens |

**Verdict:** feasible, and the most valuable target. The work is in the ADF converter and the `cloudId` and gateway
handling.

## 4. Linear

| Item | Detail |
| --- | --- |
| Free plan | **Free:** unlimited members, 2 teams, 250 non-archived issues. Enough for testing; an import of a demo project fits easily |
| API | GraphQL at `https://api.linear.app/graphql` (Linear's own app uses the same API) |
| Auth (recommended) | **Personal API key** (Settings → Account → Security & access). Sent as `Authorization: <key>` with **no** `Bearer` prefix. Keys can be limited to read-only and to specific teams when created. OAuth 2.0 (`Bearer`) needs a registered app, so leave it for later |
| Validation | Query `viewer { id name email organization { name urlKey } }` |
| Search | `issues(filter: …)` by team, project, cycle, label, state or priority, or `issue(id:)` by key (`ENG-42`). Relay-style cursor pagination |
| Hierarchy | Initiative → project → issue → sub-issue (`parent`, `children`), plus project milestones |
| Description format | **Markdown already**, so no conversion is needed |
| Rate limits | API key: 2,500 requests and 3,000,000 complexity points an hour per user. OAuth app: 5,000 requests and 2,000,000 points. Any single query is capped at 10,000 points, so keep nested queries small (fetch children per page) |
| Webhooks *(later)* | HMAC-SHA256 of the raw body in the `Linear-Signature` header (hex, no prefix), plus a `webhookTimestamp` in the body for replay protection |

**Verdict:** feasible, and the simplest adapter: Markdown descriptions, one GraphQL endpoint and generous limits.

## 5. Other common trackers

All of these have a free plan. "Fit" means how much useful structure they give the refinement and spec agents for
software work.

| Platform | Free plan | API and auth | Query and format | Fit |
| --- | --- | --- | --- | --- |
| **GitHub Issues** | Free for public and private repos | REST/GraphQL. Reuse the AIDE GitHub App (add **Issues: read**) or PAT | Search qualifiers, labels, milestones, sub-issues, issue types. Markdown | **High.** Already connected, so the lowest cost |
| **GitLab Issues** | GitLab.com Free | REST v4. Reuse the v4 GitLab connection (`read_api`) | Labels, milestones, iterations. Markdown. Epics need Premium, so on Free group by label or milestone | **High.** Already connected |
| **Azure DevOps Boards** | Basic plan free for the first 5 users, plus unlimited Stakeholders | REST. **Organisation-scoped PAT** with Work Items (Read). Global PATs are being retired (creation blocked from March 2026, existing ones decommissioned on 1 Dec 2026, per a secondary source) | WIQL (`POST _apis/wit/wiql`) returns **IDs only**, then batch-fetch the items. Descriptions are **HTML** | **High** for Microsoft shops. Epic → Feature → Story hierarchy and a real acceptance-criteria field |
| **YouTrack Cloud** (JetBrains) | Free for up to 10 users, with the full feature set | REST at `/api`. **Permanent token** (Bearer), YouTrack scope | YouTrack query language. Markdown | **Medium–high.** Developer-focused, with epics and subtasks |
| **Plane** (open source) | Free cloud tier, or self-host | REST `https://api.plane.so/api/v1`, `X-API-Key` header. Use `/work-items/`; the `/issues/` paths are deprecated | Filters per project; modules and cycles. HTML/Markdown | **Medium.** A Linear/Jira-style model, and self-hostable for offline tests. Free-tier API access is unconfirmed |
| **Shortcut** | Free plan (limits unconfirmed) | REST v3 `https://api.app.shortcut.com/api/v3`, `Shortcut-Token` header. Tokens have full account access and no scopes | Story search; epics and objectives. Markdown. 200 requests a minute | **Medium.** Good model, but its unscoped tokens are a security minus |
| **ClickUp** | Free Forever | REST v2, personal token (`pk_…`) or OAuth. 100 requests a minute per token on Free | Lists and tasks with custom fields | **Low–medium.** General work management, with structure that varies per workspace |
| **Asana** | Free Personal plan | REST, PAT (Bearer). About 150 requests a minute on Free *(secondary source)* | Projects, tasks and subtasks. Rich text | **Low.** Not software-specific |
| **Trello** | Free | REST. API key from the Power-Up admin page plus a user token, sent as query parameters | Boards, lists and cards; checklists | **Low.** Flat cards. Key and token in the URL need log redaction |
| **Notion** | Free | REST. Internal integration token; each database must be shared with the integration. About 3 requests a second | Database query. Block tree → Markdown | **Low.** Free-form. Some teams keep PRDs here, though, which is useful as idea text rather than tasks |
| **monday.com** | Free for 2 seats | GraphQL `api.monday.com/v2`. API on Free per secondary sources, which conflict on limits | Boards and items | **Low.** Generic boards |

**Not recommended:** Height (shut down in 2025), Pivotal Tracker (retired in 2025), and Jira Data Center/Server
(self-managed and with no free tier; add it only if a user asks).

## 6. Recommended v5 scope

1. **Tier 1 (v5):** Jira Cloud, Linear, GitHub Issues and GitLab Issues.
   - Read-only import into project creation, and a re-import that creates a new snapshot version.
   - `{{SOURCE_ITEMS}}` in the refinement and spec templates, with traceability from requirements to source keys.
   - A "source changed since import" indicator, by comparing `updatedAt` on demand when the project page opens. This
     needs no webhooks.
2. **Tier 2 (v5.x, if wanted):** Azure DevOps Boards and YouTrack. Both have free plans and strong software models. The
   adapter shape is the same; Azure DevOps adds an HTML → Markdown converter and the two-step WIQL fetch.
3. **Later:**
   - write-back (comment or link on source issues at release, and creating issues from plan steps)
   - webhooks
   - OAuth app flows
   - Plane and Shortcut

### Free test accounts to set up

| Platform | How |
| --- | --- |
| Jira | Free site at atlassian.com, then a scoped read-only API token |
| Linear | Free workspace, then a personal API key, read-only |
| GitHub | A test repo with issues and sub-issues; update the App's permissions to add Issues: read |
| GitLab | A test project with issues; an existing token with `read_api` |
| Azure DevOps *(tier 2)* | Free organisation, then an organisation-scoped PAT with Work Items (Read) |
| YouTrack *(tier 2)* | Free cloud instance, then a permanent token |

## 7. Open questions for Ben

- Should the import **replace** the idea text, or **add to** it? Recommendation: add to it. The idea text stays
  required as the "why", and the items are supporting material.
- Is importing **comments** worth the token and personal-data cost? Recommendation: opt-in per import, off by default.
- Is **write-back** wanted in v5, or should v5 stay read-only? Recommendation: read-only for v5.
- Should a project be able to combine items from more than one tracker? Recommendation: no, one connection per import,
  but several imports per project.
- Should the spec-verifier treat an imported item with no covering requirement as a **blocking** finding, or as
  advisory only?

## 8. Confidence

- **Primary sources:**
  - Linear rate limits and pricing come from linear.app
  - Jira Free limits and the scoped-token gateway and expiry rules come from atlassian.com and support.atlassian.com
  - the ClickUp rate limits come from developer.clickup.com
  - the YouTrack free plan and tokens come from jetbrains.com
  - the Plane API comes from developers.plane.so
- **Secondary sources:**
  - Jira `cloudId` discovery via `_edge/tenant_info`
  - the Azure DevOps global PAT retirement dates
  - Asana's free-tier rate limit
  - monday.com and Shortcut free-plan API details
  - the official Linear MCP endpoint
- **To verify when building:**
  - the exact granular Jira scope names for a read-only scoped token
  - whether Linear's Free plan restricts any API features (none found)
  - Plane's and Shortcut's free-tier API access
  - the current Azure DevOps REST api-version

## Sources

- [Jira Cloud platform REST API v3](https://developer.atlassian.com/cloud/jira/platform/rest/v3/)
- [Run JQL search query using Jira Cloud REST API (Atlassian KB)](https://confluence.atlassian.com/jirakb/run-jql-search-query-using-jira-cloud-rest-api-1289424308.html)
- [Jenkins jira-plugin issue on the removal of /rest/api/3/search](https://github.com/jenkinsci/jira-plugin/issues/636)
- [Manage API tokens for your Atlassian account](https://support.atlassian.com/atlassian-account/docs/manage-api-tokens-for-your-atlassian-account/)
- [Scoped token against site URL returns 401 (mcp-atlassian #968)](https://github.com/sooperset/mcp-atlassian/issues/968)
- [JRACLOUD-94547: Get valid project key fails with scoped tokens](https://jira.atlassian.com/browse/JRACLOUD-94547)
- [How to get a Jira API token (Knit)](https://getknit.dev/blog/jira-api-key)
- [Explore Jira Cloud plans](https://support.atlassian.com/jira-cloud-administration/docs/explore-jira-cloud-plans/)
- [Jira pricing](https://www.atlassian.com/software/jira/pricing)
- [Post HTML issue description with Jira REST API v3 (ADF)](https://community.developer.atlassian.com/t/post-html-issue-description-with-jira-rest-api-v3/38482)
- [Atlassian Rovo MCP server](https://developer.atlassian.com/cloud/rovo-mcp/)
- [Rovo MCP authentication and authorization](https://developer.atlassian.com/cloud/rovo-mcp/guides/authentication-and-authorization/)
- [Linear pricing](https://linear.app/pricing)
- [Linear GraphQL API](https://linear.app/developers/graphql)
- [Linear rate limiting](https://linear.app/developers/rate-limiting)
- [Linear SDK webhooks](https://linear.app/docs/api/sdk-webhooks)
- [Guide to Linear webhooks (Hookdeck)](https://hookdeck.com/webhooks/platforms/guide-to-linear-webhooks-features-and-best-practices)
- [Linear MCP server (LiteLLM docs)](https://docs.litellm.ai/docs/mcp_servers/linear)
- [Azure DevOps pricing](https://azure.microsoft.com/en-us/pricing/details/devops/azure-devops-services/)
- [Retirement of global personal access tokens in Azure DevOps](https://devblogs.microsoft.com/devops/retirement-of-global-personal-access-tokens-in-azure-devops/)
- [Azure DevOps global PAT and OAuth deprecation (Mend)](https://docs.mend.io/integrations/latest/azure-devops-global-pat-and-oauth-1.0-deprecation)
- [WIQL: Query By Wiql](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/wiql/query%20by%20wiql)
- [Buy YouTrack (free plan)](https://jetbrains.com/youtrack/buy)
- [YouTrack: manage permanent tokens](https://jetbrains.com/help/youtrack/cloud/manage-permanent-token.html)
- [Plane API reference](https://developers.plane.so/api-reference/introduction)
- [Shortcut API (Jentic summary)](https://jentic.com/apis/shortcut)
- [ClickUp API rate limits](https://developer.clickup.com/docs/rate-limits)
- [Asana personal access token](https://developers.asana.com/docs/personal-access-token)
- [Asana personal access token guide (Merge)](https://www.merge.dev/blog/asana-personal-access-token)
- [Trello REST API authorization](https://developer.atlassian.com/cloud/trello/guides/rest-api/authorization)
- [Notion API introduction (Nordic APIs)](https://nordicapis.com/introduction-to-the-notion-api/)
- [monday.com Free plan (Costbench)](https://costbench.com/software/project-management/monday/free-plan)
