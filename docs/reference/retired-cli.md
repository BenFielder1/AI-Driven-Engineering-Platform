---
title: Retired CLI
parent: Reference
nav_order: 7
---

# Retired CLI

Tevrin 0.7.0 removed the command-line tool. This page records it so it can be rebuilt. The code stays in git history at commit `e8c3990`.

## What it was

| Item | Detail |
| --- | --- |
| Tool | `tevrin`, a .NET global tool in `backend/src/Aide.Cli`, package `Tevrin.Cli` |
| Alias | `aide`, a deprecated command in `backend/src/Aide.Cli.Alias`, package `Aide.Cli`. It compiles the same sources and prints a deprecation notice to stderr first |
| Added | 0.1.0 (FR-52) as `aide`. Renamed to `tevrin` in 0.6.0 (D-V6-2, FR-200) |
| Published | Never to nuget.org. It was packed in CI and installed from a local folder (`--add-source`) |
| Removed | 0.7.0 |

It talked to the Tevrin API with a personal API token, so every command is a thin wrapper over an endpoint. Business rules stayed on the server.

## Snapshot

Commit `e8c3990` (the `main` merge of PR #59). Restore any file with `git show e8c3990:<path>`.

| Group | Files |
| --- | --- |
| `backend/src/Aide.Cli/` | `Aide.Cli.csproj`, `Program.cs`, `CliApp.cs`, `CliException.cs`, `ExitCodes.cs` |
| `Api/` | `AideApiClient.cs`, `Dtos.cs` |
| `Commands/` | `AnalysisCommands.cs`, `ArtifactCommands.cs`, `AuthCommands.cs`, `CommandFactory.cs`, `ConfigCommands.cs`, `DatabaseCommands.cs`, `LifecycleCommands.cs`, `LogsCommand.cs`, `Output.cs`, `Parsers.cs`, `ProjectCommands.cs`, `SourcesCommands.cs`, `StackCommands.cs`, `VersionCommands.cs` |
| `Config/` | `CliSettings.cs`, `ConfigStore.cs` |
| `Hosting/` | `CliHost.cs`, `IEditorLauncher.cs`, `ITerminal.cs` |
| `backend/src/Aide.Cli.Alias/` | `Aide.Cli.Alias.csproj`, `Program.cs` |
| `backend/tests/Aide.Cli.Tests/` | `Aide.Cli.Tests.csproj`, `CliTestHarness.cs`, `Samples.cs`, `CommandTests.cs`, `ConfigTests.cs`, `ExitCodeAndOutputTests.cs`, `Fr93CommandTests.cs`, `Fr156CommandTests.cs`, `Fr156StackCommandTests.cs`, `Fr194CommandTests.cs`, `Fr200CommandTests.cs`, `SecurityFixTests.cs` |

List them with `git ls-tree -r e8c3990 --name-only backend/src/Aide.Cli backend/src/Aide.Cli.Alias backend/tests/Aide.Cli.Tests`.

## Design

- `Program.cs` calls `CliApp.RunAsync(args, CliHost.CreateDefault())`. The alias calls `CliApp.RunAliasAsync`.
- `CliApp` builds the System.CommandLine tree. Parse errors print `error: <message>` and `Run '<command> --help' for usage.` to stderr and exit 2.
- `CliHost` holds everything that touches the machine: stdout, stderr, stdin (`ITerminal`), the editor (`IEditorLauncher`), environment variables, the user config root, the clock and the HTTP handler. Tests replace each with a fake.
- `CommandFactory.SetHandler` wraps every action. A `CliException` prints one stderr message and returns its exit code. Ctrl+C prints `Cancelled.` and exits 1. No stack traces are shown.
- `CommandContext` resolves settings lazily and builds one `AideApiClient` per run.
- `AideApiClient` wraps `HttpClient` with `Authorization: Bearer <token>`, user agent `aide-cli/<version>`, JSON web defaults and a `WithQuery` helper. It returns the parsed body, the status, the `ProblemDetails` and `Retry-After`.
- `Api/Dtos.cs` hand-copies the response shapes from `backend/openapi/api.json`. It does not use the generated TypeScript client.
- `Output` prints plain tables and key-value lists, with no colour, so output stays pipe-friendly. `--json` prints the API response indented.
- `Parsers` holds the range-checked integer and GUID parsers.

## Commands

Global options on every command (recursive): `--json` (print the API response, re-indented, instead of text) and `--api-url <url>`. Arguments called `<id>` are the project GUID ("Project id (a GUID, shown by 'tevrin projects list')"). Success exits 0. Response-file expansion (`@file`) is off in the parser, because `--request @file` is read by the command.

Unless a section says otherwise, `--json` prints the raw API body. "Table" means `Output.Table` (padded columns, no colour). Every API error maps as in [Exit codes](#exit-codes-and-error-mapping). Paths are relative to the API base URL, so `api/projects` is `<base>/api/projects`.

### Auth and config

**`tevrin login`**

- Option: `--token <token>`. Without it: a hidden prompt `API token: ` on a terminal, otherwise all of stdin. The value is trimmed. Empty gives exit 2 ("No token given").
- Calls `GET api/auth/me` with that token. It saves only after this succeeds.
- Saves `token` and `tokenApiUrl` (the canonical URL in effect). If `--api-url` was given, it also saves `apiUrl`.
- If `TEVRIN_TOKEN` or `AIDE_TOKEN` is set, stderr gets `warning: <variable> is set and overrides the saved token.`
- Output: `Logged in to <url> as <name> <<email>> (<systemRole>). Token saved to <file> (sent only to this API URL).` With `--json`: the `/me` body.

**`tevrin logout`**: no options, no API call. Clears `token` and `tokenApiUrl`. Prints `Saved token removed. Revoke it on the Account page if it may have leaked.`

**`tevrin config set <key> [value]`**

- `key` is `api-url` or `token` (other values fail parsing, exit 2). `value` is optional.
- `api-url`: `value` is required (missing gives exit 2). An empty string clears the setting. A non-empty value must be an absolute http(s) URL without query or fragment, else exit 2. A trailing `/api` is accepted. If a token is saved without a `tokenApiUrl`, it is first pinned to the previous URL. If the saved token now belongs to a different URL, stderr gets `note: the saved token belongs to <url> and will not be sent to <url>; run 'tevrin login' for it.`
- `token`: a non-blank `value` is refused (exit 2, so it stays out of shell history). With no value: hidden prompt on a terminal, else stdin. An empty result is exit 2. A blank-string `value` clears the token. The token is bound to the canonical API URL in effect.
- Output: `Saved <key> = <shown> to <file>`. For a token, `<shown>` is the masked token plus ` (for <tokenApiUrl>)`. For `api-url` it is the value, or `(default)` when cleared. `--json` has no effect.

**`tevrin config get [key]`**

- `key` optional: `api-url` or `token`. No API call.
- Text: no key prints key-value lines `api-url`, `token` and `config file`, each value followed by `(flag|environment|config file|default)`. With a key it prints only the value.
- A saved token withheld for the current URL shows masked, with source `config file, saved for <url>; not sent to <url>`.
- `--json`: an object with `path`, plus `apiUrl` and `apiUrlSource` (unless key is `token`), plus `token` and `tokenSource` (unless key is `api-url`).

**`tevrin config path`**: prints the config file path. With `--json`: `{ "path": "…" }`.

### Projects and versions

**`tevrin projects list`** calls `GET api/projects?page&limit&search&phase&status&sortBy&sortOrder`. Unset options are left out of the query. The defaults are always sent.

| Option | Default and values |
| --- | --- |
| `-p, --page` | 1, whole number of at least 1 |
| `-l, --limit` | 20, 1 to 100 |
| `-s, --search` | name filter |
| `--phase` | `analysis`, `refinement`, `specification`, `planning`, `execution`, `review`, `release` |
| `--status` | `active`, `building`, `reviewing`, `releasing`, `released`, `rolled_back` |
| `--sort-by` | `updated` (also `created`, `name`) |
| `--sort-order` | `desc` (also `asc`) |

Output: table `ID NAME PHASE STATUS RELEASED "IN DEVELOPMENT" ROLE UPDATED` (role defaults to `admin`), then `Page <n> of <pages> (<total> projects)`. No rows prints `No projects.` or `No projects on this page.`

**`tevrin project init`** (alias `create`) calls `POST api/projects`. The body is `name`, `idea`, `approvalMode`, `complianceThreshold`, `maxFilesPerStep`, `maxReviewCycles`, `tokenBudget`, `git { provider, repo, defaultBranch, token, localPath }`, `llm { provider, model, connectionId }`, `stackSource`, `source` and `import { developmentVersion, releasedTag }`.

| Option | Meaning |
| --- | --- |
| `-n, --name` | 1 to 100 characters. Prompted if missing on a terminal, else exit 2 |
| `--idea` / `--idea-file` | 1 to 10000 characters, or a file (`-` for stdin). Both together exit 2. Idea is prompted if missing |
| `--approval-mode` | `strict`, `advisory`, `autonomous`. Prompted `Approval mode [advisory/strict/autonomous] (advisory)`. Left out, the server default applies |
| `--compliance-threshold` | 0.5 to 1 |
| `--max-files-per-step` | 1 to 100 |
| `--max-review-cycles` | 1 to 10 |
| `--token-budget` | at least 1000, omit for unlimited |
| `--git-provider` | `local` (default) or `github` (sent as `github_pat`). Prompted `Git provider [local/github] (local)` |
| `--repo-url` | required with `github` (prompted), exit 2 with `local` |
| `--default-branch` | server default `main` |
| `--llm-provider` | `anthropic`, `openai`, `ollama`, `fake`. Never prompted. Needs `--llm-model` |
| `--llm-model` | model id. Needs `--llm-provider` |
| `--llm-connection` | GUID. Needs `--llm-provider` |
| `--infer-stack` | sends `stackSource: "infer"`. Server exit 4 `STACK_INFER_NO_CODE` for a repo without code |
| `--import-version` | semver to develop. Sends `source: "import"` and `import.developmentVersion` |
| `--released-tag` | with `--import-version` only (else exit 2) |
| `--local-path` | with `--import-version` and `--git-provider local` only (else exit 2) |
| `--no-input` | never prompt; missing required values exit 2 |

The GitHub token is never a flag: `TEVRIN_GIT_TOKEN` (or `AIDE_GIT_TOKEN`), else a hidden prompt (`GitHub access token (empty to add later in settings)`) when interactive, else none. Prompts only happen when stdin is a terminal and `--no-input` is absent. Output: `Created project <name> (<id>): phase <p>, status <s>, approval mode <m>.` then `Next: tevrin analysis show <id>` when the phase is `analysis`, else `Next: tevrin status <id>`.

**`tevrin status <id>`** calls `GET api/projects/{id}/state`. For text it also calls `GET api/projects/{id}`, `…/stack`, `…/migrations` and `…/sources`.

- Text: the heading `<name> (<id>)`, then key-value rows `Phase`, `Status`, `Released`, `In development`, `Checkpoint`, `Preset`, `Stack`, `Source items`, `Production <component>` rows (or `Production URLs: -`), `Approval mode`, `Execution`, `Steps`, `Review`, `Tokens`, `Pull request`, `Latest release`, `Latest deployment`, `Latest migration`. Then `Artifacts:` as table `TYPE LATEST STATUS LOCKED`, then `Pending gates:` lines (`<type> <id> since <time>`, with `(overdue)`) or `Pending gates: none`.
- `--json`: the state body plus `stack` (the stack body), `latestMigrationRun` (first migration run or null) and `sources`.

**`tevrin version start <id> <semver>`** calls `POST api/projects/{id}/versions`, after `GET api/projects/{id}/stack` when stack flags are used.

- Argument `semver`: `MAJOR.MINOR.PATCH[-prerelease]`, trimmed. Body: `{ version, request, stackSource?, stack? }`.
- `-r, --request` is required: the text, `@path` for a file, `@-` for stdin. Empty or a bare `@` exits 2.
- `--infer-stack` sends `stackSource: "infer"`. With stack flags it exits 2.
- The stack flags `--frontend --backend --data --vcs --frontend-host --backend-host --database-host --local` also work as `--stack-frontend` and so on. They merge into the current composition as in `stack set` and are sent as `stack`.
- A hidden `--preset` always exits 2: "presets were removed in 0.4.0; use --frontend, --backend, --data, --vcs and the host options to choose a stack (omit them to keep the current one)."
- Output: `Started version <v> (from <baseTag>): phase <p>, checkpoint <n>.` then `Next: tevrin analysis show <id>` (phase `analysis`) or `Next: tevrin status <id>`. An origin-model change prints a `warning:` line on stderr.
- Owner only. Server exit 4: `VERSION_INVALID`, `VERSION_NOT_GREATER`, `VERSION_IN_PROGRESS`, `TAG_EXISTS`, `RELEASE_NOT_MERGED`, `NOT_RELEASED`, `STACK_INCOMPATIBLE`, `STACK_INFER_NO_CODE`.

**`tevrin versions <id>`** calls `GET api/projects/{id}/versions`. Table `VERSION STATUS TAG CHECKPOINT PRESET BRANCH STARTED RELEASED`. Checkpoint is `current/max`. Preset is `none` or `<preset>@<version> (retired, migrated)`. Empty: `No versions.`

### Stack

Stack flag values may be `none`. `--data` is `<option>`, `<option>:baas` or `<option>:host`, else exit 2. `--local` accepts `compose` or `none`. Blank values exit 2.

**`tevrin stack catalog`** calls `GET api/stack/catalog`. Text: `Stack catalog <version>`, then per layer `<name> (<id>)`, per choice `  <name> (<id>):` and an indented table `OPTION NAME MODES`.

**`tevrin stack show <id>`** calls `GET api/projects/{id}/stack`. Key-value rows `Version` (`<v> (<status>)`), `Locked` (`yes (<reason>)` or `no`), `Catalog`, `Frontend` and `Backend` (`<pack> at <path>`), `Data`, `VCS`, `Frontend host`, `Backend host`, `Database host`, `Local`, `Origin model`. Then an optional `Preset: <label>` line, `No stack composition is pinned on this version.` and `Caveat <rule> (<layer>: <option>): <text>` lines.

**`tevrin stack set <id>`**

- Needs at least one of `--frontend --backend --data --vcs --frontend-host --backend-host --database-host --local`, else exit 2. There are no `--stack-*` aliases here.
- Calls `GET api/projects/{id}/stack`, merges the named layers into the current composition, then `PUT api/projects/{id}/stack` with the whole merged composition (a left-out layer would otherwise become none). Changing the frontend, backend or either host drops the stored origin model so the server resets it. A `warning:` line goes to stderr when it changed.
- Output: the `stack show` text for the updated stack. Owner only. Server exit 4: `STACK_INCOMPATIBLE` (violations printed), `STACK_LOCKED`.

**`tevrin stack detect <repo-url>`** calls `POST api/stack/detect` with `{ git: { provider, instanceId, repo, ref, token, connectionId } }`.

- Argument `repo-url`: an http(s) URL, or `owner/name` with `--provider`. A trailing `.git` is stripped. The path must have `owner/name`.
- Options: `--provider` (`github_app`, `github_pat`, `gitlab`, `bitbucket`, `local`; default from the host: github.com `github_app`, gitlab.com `gitlab`, bitbucket.org `bitbucket`; other hosts exit 2), `--ref`, `--instance <guid>`, `--connection <guid>`.
- For `github_pat` the token comes from `TEVRIN_GIT_TOKEN` or `AIDE_GIT_TOKEN`. Nothing is stored. Server exit 4: `GIT_NOT_CONNECTED`.
- Output: `Detected at <ref> (<short sha>).`, a table `CHOICE OPTION PATH MODE EVIDENCE` (or `No suggestions.`), `Conflict in <choice> (choose one): …` lines and a notice when the repository was truncated.

### Analysis

**`tevrin analysis show <id>`** calls `GET api/projects/{id}/analysis`.

- Key-value rows: `Version`, `Base` (short SHA or `no code`), `Codebase summary`, then for inferred stacks `Stack Analyst`, `Profile` (`v<n> <status> (by the Stack Analyst|edited)` or `none yet`), `Approve` (`yes` or `no: <reason>`) and `Continue`, then `Stack`.
- Not inferred: it adds `This version doesn't infer its stack: there is no profile to approve.`
- Inferred: it adds `Needs your input:` lines (`<layer> (<status>): <question>; found <tech>, which isn't in the catalog; candidates: …`), a table `LAYER OPTION PATH MODE STATUS CONFIDENCE` for frontend, backend, data and vcs, and `Continue with No stack (general) is chosen.` when set. An unreadable profile prints `The profile can't be read: …`.

**`tevrin analysis approve <id>`**

- Options: `-c, --comment`, `--generalist` (approve with No stack, FR-181).
- Calls `GET …/analysis`, then `POST api/projects/{id}/artifacts/{profileArtifactId}/approve` with `{ comment, generalist: true|null }`.
- No profile yet, or a profile that is not a `draft`, exits 1 with a message. Owner only. Server exit 4: `STACK_PROFILE_INCOMPLETE` (unresolved layers listed), `STACK_INCOMPATIBLE`.
- Output: `Approved stack profile v<n>: the stack is pinned. Next: continue to Refinement on the Analysis page.`

**`tevrin analysis reject <id>`**: option `-c, --comment` ("Why: the Stack Analyst runs again with it"). Same flow, `POST …/artifacts/{profileArtifactId}/reject` with `{ comment }`. Owner only. Output: `Rejected stack profile v<n>: the Stack Analyst runs again. Next: tevrin analysis show <id>`.

**`tevrin analysis retry <id>`**: `POST api/projects/{id}/analysis/run` (no body). Writer only. Output: `Queued the Stack Analyst (job <id>). Next: tevrin analysis show <id>`. Server exit 4: `WRONG_PHASE`, `INVALID_STATE`.

### Sources

All paths are `api/projects/{id}/sources`. Items change only in analysis and refinement (else the server answers 409 `WRONG_PHASE`, which exits 4). The id argument `<import-id>` is shown by `sources list`.

**`tevrin sources list <id>`**: `GET …/sources`.

- Table `ID TRACKER STATUS ITEMS SELECTION BY CREATED`. Status gets `(<errorCode>)` when failed. Selection is up to 5 keys then `,… (<n>)`, or the query, or `all matching`, prefixed `<container>: `.
- Then `Snapshot: source_items v<n> (<12-char hash>)` or `none`. Extra lines: `Sources changed since project request v<n>: run another refinement round.` and `Items can't change now: …`. Empty: `No imports.`

**`tevrin sources import <id>`**

- Required: `--connection <guid>` and `--tracker` (`jira`, `linear`, `github`, `gitlab`).
- Exactly one of `--keys` (comma-separated, trimmed, de-duplicated, at most 200) or `--query` (JQL, or GitHub qualifiers). Both or neither exit 2.
- Optional: `--container` (Jira project key, Linear `team:KEY`, GitHub `owner/repo`, GitLab `group/project`) and `--comments` (off by default).
- Calls `POST …/sources` with `{ provider, connectionId, selection: { keys, query, containerId }, options: { includeChildren: true, includeComments, includeAssignees: false } }`. Writer only. Server errors: `WRONG_PHASE`, `TRACKER_NOT_CONNECTED`, `TRACKER_PERMISSION_MISSING` and `SOURCE_ITEMS_TOO_LARGE` exit 4 when the server answers 409, otherwise 1.
- Output: `Queued import <id> from <provider> (<status>). Next: tevrin sources list <id>`.

**`tevrin sources reimport <id> <import-id>`**: `POST …/sources/{importId}/reimport` (no body). Writer only. Output: `Queued the re-import import <id> from <provider> (<status>). Next: tevrin sources list <id>`. Server errors: `WRONG_PHASE` and `TRACKER_NOT_CONNECTED` exit 4 when the server answers 409, otherwise 1.

**`tevrin sources remove <id> <import-id>`**: `DELETE …/sources/{importId}` (no content). Writer only. Output: `Removed import <id>. Earlier snapshots stay readable.` With `--json`: `{ "importId": "…", "removed": true }`.

**`tevrin sources check <id>`**: `POST …/sources/check` (no body, never changes a snapshot). Table `KEY TRACKER DRIFT TITLE` (drift is `unchanged`, `changed`, `removed` or `unknown`), then `<n> of <total> changed or removed since import; re-import to take the changes.` Empty: `No imported items.`

### Artifacts

`<type>` is parsed case-insensitively: `project_request` (`request`), `technical_specification` (`spec`), `implementation_plan` (`plan`), `optimization_plan` (`optimization`). Anything else exits 2. Paths below are under `api/projects/{id}/artifacts`.

| Command | Details |
| --- | --- |
| `artifact list <id>` | `GET …/artifacts`. Table `ID TYPE VERSION STATUS INTEGRITY CREATED AUTHOR` (author is the user name, `agent` or `-`). Empty: `No artifacts yet.` |
| `artifact history <id> <type>` | `GET …/{type}/history`, same table. Empty: `No versions.` |
| `artifact show <id> <type>` | Options `-v, --version <n>` (at least 1) and `--content-only`. `GET …/{type}/latest`, or `…/{type}/versions/{n}`. Text: key-value rows `Artifact`, `Status`, `Integrity`, `Parent`, `Hash`, `Checkpoint`, `Author`, `Created`, `Approved`, `Rejection`, a blank line, then the Markdown content. `--content-only` prints only the content |
| `artifact approve <id> <artifact>` | Option `-m, --comment` (max 4000). `<artifact>` is a version GUID, or a type whose latest version is looked up first (`GET …/{type}/latest`). `POST …/{artifactId}/approve` with `{ comment }`. Owner only. Output `<type> v<n> is now <status>.`. Neither a GUID nor a type exits 2 |
| `artifact reject <id> <artifact>` | Same, `POST …/{artifactId}/reject`. Owner only |
| `artifact edit <id> <type>` | See below |
| `artifact restore <id> <type> <version>` | `version` at least 1. `POST …/{type}/versions/{n}/restore` (no body). Contributor or Owner. Output `Restored <type> v<n> as draft v<m> (parent v<p>, status <s>).`. Server exit 4: `RESTORE_NOT_ALLOWED` |

**`artifact edit <id> <type>`**

- Options: `-f, --file <path>` (`-` for stdin) and `--force`.
- Without `--file` and with stdin not a terminal: exit 2 ("stdin is not a terminal, so no editor can be opened; pass --file <path> (or --file - for stdin).").
- Flow: `GET …/{type}/latest` (a 404 `NOT_FOUND` is tolerated: no version yet). With `--file`, read it (an unreadable file exits 1). Otherwise write the latest content to `aide-<type>-<32 hex>.md` in the temp folder (created `CreateNew`, owner-only on Unix), open the editor and wait, then read it back.
- Unchanged content: stderr `No changes; no draft was created.`, exit 0. Empty content: exit 2 ("The content is empty; no draft was created.").
- Saves with `POST …/{type}` and `{ content, baseVersion }`. `baseVersion` is the latest version, or omitted with `--force` or when none existed. A stale base is a 409, exit 4.
- If the editor or the save fails, the temp file is kept and stderr says `Your edit was kept at <path> (re-upload it with --file).` On success or no change it is deleted.
- Output: `Saved <type> v<n> as <status> (parent v<p>, hash <hash>).`

### Lifecycle

| Command | Details |
| --- | --- |
| `execute <id>` | Options `--pause` and `--resume` (both together exit 2). `POST api/projects/{id}/execution/{start\|pause\|resume}` (no body). Contributor or Owner. Output `Execution started\|Pause requested\|Execution resumed: <status>[ (paused)], step <n>, <done>/<total> steps completed.` |
| `review <id>` | `POST api/projects/{id}/review` (no body). Contributor or Owner. Output `Review cycle <n> queued (job <id>).` |
| `release <id>` | Option `-m, --comment`. Owner only. `GET api/projects/{id}/gates?status=pending`. If a `pre_release` gate is pending: `POST …/gates/{gateId}/approve` with `{ comment }`, output `Pre-release gate <id> approved; the release is queued. Follow it with 'tevrin status <id>'.` Else `POST api/projects/{id}/release` (retry), output `Release retry queued: tag <t>, checkpoint <n>, attempt <n>, status <s>.` |
| `rollback <id> <checkpoint>` | `checkpoint` at least 1. Required `-r, --reason` (1 to 500 characters, trimmed; empty exits 2). `POST api/projects/{id}/rollback` with `{ targetCheckpoint, reason }`. Owner only. The server allows one per minute (429, exit 1, then `Retry after <n>s.`). Output `Rolled back from checkpoint <a> to checkpoint <b>; phase is now <p>. Invalidated <n> artifact(s), reset <n> step(s), cancelled <n> gate(s).` |

### Deployments, databases, migrations and logs

| Command | Details |
| --- | --- |
| `deployments <id>` | Options `--env` and `-t, --target` (both `preview` or `production`; `--target` is the 0.2.0 name), `--component` (`frontend` or `backend`). `GET api/projects/{id}/deployments?target&environment&component`. Table `ID TARGET COMPONENT VERSION SHA STATUS URL CREATED BY` (status gets `(<errorCode>)`; SHA is 7 characters; BY is the user or the trigger). Empty: `No deployments.` |
| `deployments groups <id>` | `GET api/projects/{id}/settings/deployment`. Table `PROVIDER PARTS STATUS NAME CONNECTION "SHARED PROJECT"`, then a line per part status (`<provider> <part> (<env>): <status>`) and a retry hint when `canRetry`. `--json` prints only the `groups` array. Empty: `No deployment groups.` |
| `databases <id>` | Same endpoint. Table `ENV PROVIDER STATUS NAME REGION PLAN EXPIRES URL` (`(paused)` after the status), then `<env>: <warning>` lines. `--json` prints only the `databases` array. Empty: `No databases.` |
| `migrations <id>` | `GET api/projects/{id}/migrations`. Table `ID ENV PROVIDER SHA STATUS FILES DURATION STARTED` (duration in ms). Empty: `No migration runs.` |
| `logs <id>` | See below |

**`logs <id>`** calls `GET api/projects/{id}/activity?type&before&limit`.

- Options: `-t, --type` (`transition`, `gate`, `artifact`, `run`, `step`, `review`, `release`, `rollback`, `project`, `integrity`), `-l, --limit` (1 to 100, default 50), `--before <cursor>`, `-f, --follow`, `--interval <seconds>` (1 to 3600, default 5). `--before` with `--follow` exits 2.
- Prints oldest first, one line per event: `<time>  <type, 28 wide> <actor, 16 wide> <summary>`. With `--json` and no `--follow` it prints the page body. Empty first page: `No activity.` When an older page exists, stderr gets `(older events: --before <cursor>)`.
- `--follow` prints the first page, then polls every `--interval` seconds (using the injected `TimeProvider`). It pages back through new events (up to 100 pages per poll, then a `warning:` on stderr), skips events already printed by id or older timestamp, and exits 0 on Ctrl+C. With `--json` it prints one compact JSON object per line.

## Configuration and auth

| Item | Detail |
| --- | --- |
| Config file | `TEVRIN_CONFIG` (or `AIDE_CONFIG`) if set. Otherwise `%APPDATA%\aide\config.json` on Windows or `~/.config/aide/config.json` elsewhere. The folder was still named `aide` |
| Format | JSON with `apiUrl`, `token` and `tokenApiUrl` (the normalised URL the token was saved for) |
| Permissions | On Unix the folder is 0700 and the file 0600. It is written to a temp file and swapped in |
| Default API URL | `http://localhost:8080` (the local Kubernetes URL) |
| Token | A personal API token, `aide_pat_…`, created on the Account page and sent as `Authorization: Bearer` |

| Variable | Purpose | Fallback |
| --- | --- | --- |
| `TEVRIN_TOKEN` | API token. Explicit: sent to whatever URL is in effect | `AIDE_TOKEN` |
| `TEVRIN_API_URL` | API base URL | `AIDE_API_URL` |
| `TEVRIN_CONFIG` | Config file path | `AIDE_CONFIG` |
| `TEVRIN_GIT_TOKEN` | Git host PAT for `project init` and `stack detect` | `AIDE_GIT_TOKEN` |
| `VISUAL`, `EDITOR` | Editor for `artifact edit`. Default `notepad` on Windows, else `vi` | none |

- **API URL order:** `--api-url`, then `TEVRIN_API_URL`, then `AIDE_API_URL`, then the config file, then the default. An empty value counts as unset. A trailing `/api` is dropped.
- **Token order:** `TEVRIN_TOKEN`, then `AIDE_TOKEN`, then the saved token.
- **Saved token binding:** the saved token is sent only to the URL it was saved for, compared by scheme, host, port and path. For any other URL the command exits 3 with a hint to log in again.
- **Plain http:** a token is never sent over `http` except to a loopback address (`localhost`, any `127.0.0.0/8` address, or `::1`; `CliSettings.IsAllowedForToken` uses `IPAddress.IsLoopback`). Otherwise the command exits 2.
- **Masking:** `config get` shows `aide_pat_********` plus the last four characters.
- **Terminal:** `ITerminal` reads prompts and stdin. `IsInteractive` is true when stdin is not redirected. `PromptSecret` does not echo. Prompts go to stderr so stdout stays clean.
- **Editor:** `ProcessEditorLauncher` splits `$VISUAL` or `$EDITOR` on whitespace (double quotes honoured, so `code --wait` works), appends the temp file and waits. A non-zero editor exit saves nothing. The temp file is `aide-<type>-<random>.md` and is created owner-only on Unix.

## Exit codes and error mapping

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Any other error, including cancellation |
| 2 | Usage error: bad or missing arguments, plain http to a non-loopback host, `--preset` |
| 3 | Auth: HTTP 401 or 403, or no token for this URL (not logged in, or the saved token belongs to another URL) |
| 4 | Conflict: HTTP 409, or one of the error codes below |

Errors print to stderr as `error: <status> <title> [<errorCode>]`, then indented lines for the problem `detail`, each stack violation (`- <rule> (<layer>: <option>): <reason>`) or `details` entry, each validation error (`- <field>: <message>`), `Retry after <n>s.` when the server sent `Retry-After`, a hint for 401 (`The token is missing, revoked or expired. Run 'tevrin login --token <token>'.`) and `traceId: <id>`. Non-API failures print `error: <message>`. Network failure exits 1 (`Could not reach the Tevrin API at <url>`), as does a timeout (100 seconds) or an unreadable response. These codes always exit 4, whatever their HTTP status (`ExitCodes.ConflictErrorCodes`): `VERSION_INVALID`, `VERSION_NOT_GREATER`, `VERSION_IN_PROGRESS`, `TAG_EXISTS`, `RELEASE_NOT_MERGED`, `NOT_RELEASED`, `LLM_PROVIDER_UNAVAILABLE`, `STACK_INCOMPATIBLE`, `STACK_LOCKED`, `STACK_PROFILE_INCOMPLETE`, `STACK_INFER_NO_CODE` and `SOURCE_ITEMS_TOO_LARGE`. `VCS_MISMATCH` exits 1. Any other failure exits 1. See [Error codes](error-codes.md).

## Dependencies

| Package | Version | Where |
| --- | --- | --- |
| `System.CommandLine` | 2.0.12 | `Directory.Packages.props`, group `Label="CLI"`. Referenced by `Aide.Cli` and `Aide.Cli.Alias` |
| `xunit.v3` | from the test group | `Aide.Cli.Tests` |

Both tool projects set `PackAsTool`, `SatelliteResourceLanguages=en`, `Authors`, `Copyright`, the project URL and pack `LICENSE.md` (PolyForm Shield, D-V6-21) from the repo root. `Aide.Cli` has `InternalsVisibleTo` for `Aide.Cli.Tests`. The alias compiles `../Aide.Cli/**/*.cs` (without `Program.cs`) as linked files. The CLI has no project reference to any other Tevrin assembly. The package version is `<Version>` in `Directory.Build.props`.

## Tests

`Aide.Cli.Tests` is an xUnit v3 executable. It runs the real command tree against a fake `HttpMessageHandler`, a fake terminal and a fake editor, with no network. `CliTestHarness.cs` holds the fakes and `Samples.cs` holds response bodies shaped like `api.json`.

| File | Covers |
| --- | --- |
| `CommandTests.cs` | FR-52: each command calls the right route with the right body and prints a useful result |
| `ConfigTests.cs` | FR-52: config location, precedence, token masking, login |
| `ExitCodeAndOutputTests.cs` | FR-52: usage errors, exit-code mapping, `--json` output, error reporting |
| `Fr93CommandTests.cs` | FR-93: `version start`, `versions`, `deployments`, `artifact restore`, `status`, release-version exit 4 codes |
| `Fr156CommandTests.cs` | FR-156, FR-159: LLM flags on `project create`, `LLM_PROVIDER_UNAVAILABLE` |
| `Fr156StackCommandTests.cs` | FR-156: `stack` commands, stack options of `version start`, listings, exit codes |
| `Fr194CommandTests.cs` | FR-194: `analysis`, `sources`, `deployments groups`, `--infer-stack`, new exit-4 codes |
| `Fr200CommandTests.cs` | FR-200: `tevrin` name, `aide` alias notice, `TEVRIN_*` before `AIDE_*`, `aide_pat_` tokens |
| `SecurityFixTests.cs` | FR-52 and NFR-SEC: token bound to its URL, plain http refused, secret prompt, editor temp file mode, `--follow` paging |

## Build, pack and install

From `backend/`:

```bash
dotnet build -warnaserror
dotnet test --project tests/Aide.Cli.Tests -c Release            # in CI this ran in the "Test (unit, CLI)" step
dotnet pack src/Aide.Cli -c Release -o ./nupkg                   # Tevrin.Cli
dotnet pack src/Aide.Cli.Alias -c Release -o ./nupkg             # Aide.Cli (the aide alias)
dotnet tool install --global Tevrin.Cli --add-source ./nupkg     # update: dotnet tool update --global Tevrin.Cli --add-source ./nupkg
tevrin login --token aide_pat_...
```

The CI step in `.github/workflows/ci.yml` at `e8c3990` was named "CLI packs as a .NET tool (FR-52)":

```yaml
- name: CLI packs as a .NET tool (FR-52)
  run: |
    dotnet pack src/Aide.Cli -c Release --no-build -o "$RUNNER_TEMP/nupkg"
    dotnet pack src/Aide.Cli.Alias -c Release --no-build -o "$RUNNER_TEMP/nupkg"
```

The test step ran `dotnet test --project tests/Aide.Cli.Tests` beside the unit tests, with the same `-c Release --no-build --results-directory ... --report-xunit-trx` options. Copy the exact lines from `git show e8c3990:.github/workflows/ci.yml`.

## How to restore

1. Check out the sources: `git checkout e8c3990 -- backend/src/Aide.Cli backend/src/Aide.Cli.Alias backend/tests/Aide.Cli.Tests`.
2. Re-add the three projects to `backend/Aide.slnx` (`src/Aide.Cli/Aide.Cli.csproj`, `src/Aide.Cli.Alias/Aide.Cli.Alias.csproj`, `tests/Aide.Cli.Tests/Aide.Cli.Tests.csproj`).
3. In `backend/Directory.Packages.props`, restore `<ItemGroup Label="CLI"><PackageVersion Include="System.CommandLine" Version="2.0.12" /></ItemGroup>`. Check for a newer 2.0.x first.
4. In `backend/Directory.Build.props`, add `Or '$(MSBuildProjectName)' == 'Aide.Cli.Tests'` back to the test-project condition, so the test project gets the shared test settings. Restore the comment that names the CLI packages next to `<Version>`.
5. Restore the CI pack and test steps in `.github/workflows/ci.yml` from `e8c3990`. Check that `scripts/ci/check-version.sh` still matches the version sources.
6. Restore the docs from `e8c3990` (`git checkout e8c3990 -- <path>` for whole files, `git diff e8c3990 -- <path>` to merge the rest):
   - `docs/guides/cli.md`, and its row `| [CLI](cli.md) | Drive Tevrin from a terminal |` in `docs/guides/index.md`.
   - `docs/reference/configuration.md`: the "CLI variables" section and the intro sentence "Only the CLI variables were renamed, see the last table."
   - `docs/reference/error-codes.md`: the line linking the CLI guide. `docs/reference/api-and-contract.md`: "for the CLI" in the Auth row and "Use it with the [CLI](../guides/cli.md)".
   - `docs/reference/repo-layout.md`: the `src/Aide.Cli/` and `src/Aide.Cli.Alias/` lines. `docs/reference/architecture.md` and `README.md`: the `CLI["tevrin CLI"] -->|"Bearer token"| Web` diagram node, "Aide.Cli" in the README backend line and "CLI" in its Guides row.
   - `docs/getting-started/first-project.md` (Script it row), `docs/guides/issue-trackers.md`, `docs/guides/stacks-and-inference.md`, `docs/guides/pipelines-and-packs.md` ("CLI commands"), `docs/operations/ci.md` ("CLI packs") and `docs/operations/releases.md` ("both CLI packages").
   - Frontend copy that mentioned the CLI at `e8c3990`:
     - `frontend/src/features/marketing/content/site-nav.ts`: the Developers link `{ label: 'CLI', href: docsUrl('guides/cli/'), external: true }`.
     - `frontend/src/features/marketing/content/faq.ts`: the `cli-and-api` entry ("Is there a CLI or API?", the `tevrin` .NET global tool answer, and `link: { label: 'CLI guide', href: docsUrl('guides/cli/') }`).
     - `frontend/src/features/marketing/content/product.ts`: the `cli-and-api` section titled "CLI and API" ("The tevrin command-line tool covers the lifecycle from a terminal, with personal API tokens.", its bullets), and the header comment's "CLI FR-52 and docs/guides/cli.md".
     - `frontend/src/features/marketing/content/pages.ts`: the Product page description ending "admin and the CLI."
     - `frontend/src/features/account/api-tokens-section.tsx`: "Personal tokens let the `tevrin` CLI act as you.", the empty state "Create one above to use the CLI.", the revoke text "Anything using this token, such as the CLI, stops working immediately.", and the name hint `Give the token a name, such as "Laptop CLI".`
     - Tests and mocks that assert that copy: `marketing-routes.test.tsx`, `faq-page.test.tsx`, `product-page.test.tsx`, `account-page.test.tsx`, `e2e/auth.spec.ts`, `src/mocks/handlers/auth.ts`.
   - Add a spec section and requirement IDs for the CLI again.
7. Check the API still matches. The CLI hand-copies the response shapes in `Api/Dtos.cs` and never uses the generated client. Diff each DTO and each endpoint above against `backend/openapi/api.json`, which will have moved on since 0.6.1. Pay attention to the lifecycle endpoints, which the engine work reshaped. Fix the DTOs, then run the CLI tests.
8. Run the gate on CI: `dotnet build -warnaserror`, `dotnet test`, `dotnet format --verify-no-changes`, and the two `dotnet pack` steps.

## What stays

Personal API tokens remain. Create one on the **Account** page (`/api/me/tokens`) for scripts and the API. They keep the `aide_pat_` prefix and are sent as `Authorization: Bearer`. See [API and contract](api-and-contract.md#create-an-api-token).
