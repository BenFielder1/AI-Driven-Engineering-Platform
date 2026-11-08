---
title: CLI
parent: Guides
nav_order: 10
---

# Command-line tool

`tevrin` is a .NET global tool (`backend/src/Aide.Cli`). It is published as the `Tevrin.Cli` package (command `tevrin`). The old `Aide.Cli` package (command `aide`, from `backend/src/Aide.Cli.Alias`) still works as a deprecated alias: it prints a notice to stderr, then behaves identically. Environment variables `TEVRIN_TOKEN`, `TEVRIN_API_URL`, `TEVRIN_CONFIG` and `TEVRIN_GIT_TOKEN` are read first, then their `AIDE_*` equivalents; an empty value counts as unset.

```bash
cd backend
dotnet pack src/Aide.Cli -c Release -o ./nupkg
dotnet tool install --global Tevrin.Cli --add-source ./nupkg      # update: dotnet tool update ...
tevrin login                                # prompts for a token, or: tevrin login --token aide_pat_...
```

Create the token on the Account page of the app (shown once). Tokens still start with `aide_pat_`, and tokens created before the rename keep working.


| Command | What it does |
| --- | --- |
| `tevrin projects list` | List your projects (`--search`, `--phase`, `--status`, paging) |
| `tevrin project init` (alias `create`) | Create a project (like the wizard). A GitHub token is read from `TEVRIN_GIT_TOKEN` (or `AIDE_GIT_TOKEN`) or prompted. LLM flags: `--llm-provider <p> --llm-model <id> [--llm-connection <id>]`; omit them for the server's default |
| `tevrin status <id>` | Phase, status, released and in-development versions, checkpoint, stack composition, production URLs, latest deployment, latest migration run, artifacts, execution, review, gates. `--json` adds `stack` and `latestMigrationRun` |
| `tevrin version start <id> <semver> --request <text\|@file> [stack flags]` | Start a new version of a released project (Owner). `@-` reads stdin. Stack flags are those of `tevrin stack set`, also as `--stack-frontend` etc. `--preset` exits 2: presets were removed in 0.4.0 |
| `tevrin versions <id>` | List release versions |
| `tevrin project create --infer-stack`, `tevrin version start --infer-stack` | Start in the Analysis phase and infer the stack from the repository. `project create` also takes `--import-version`, `--released-tag` and `--local-path` for an imported repo |
| `tevrin analysis show\|approve\|reject\|retry <id>` | Work with the inferred stack profile. `approve` and `reject` take `--comment`. An unresolved layer exits 4 |
| `tevrin sources list\|check <id>` | List tracker imports, or run the drift check |
| `tevrin sources import <id> --connection <id> --tracker <p> (--keys <k,…> \| --query <q>) [--comments]` | Import tracker items as a snapshot |
| `tevrin sources reimport\|remove <id> <importId>` | Re-run or remove an import |
| `tevrin deployments groups <id>` | List deployment groups |
| `tevrin stack catalog` | List the catalog's layers and options |
| `tevrin stack show <id>` | Show a project's composition and whether it is locked |
| `tevrin stack set <id> [--frontend <opt>] [--backend <opt>] [--data <opt>[:baas]] [--vcs <opt>] [--frontend-host <opt>] [--backend-host <opt>] [--database-host <opt>] [--local compose\|none]` | Change the version in development's stack (Owner). Named layers merge into the current composition; `none` clears a layer. Changing frontend, backend or their hosts resets the origin model, with a warning on stderr. Exit 4 prints `STACK_INCOMPATIBLE` violations or `STACK_LOCKED` |
| `tevrin stack detect <repo-url> [--provider <p>] [--ref <ref>] [--instance <id>] [--connection <id>]` | Suggest a stack from a connected repo. Nothing is stored. The provider comes from the URL for github.com, gitlab.com and bitbucket.org. A PAT is read from `TEVRIN_GIT_TOKEN` (or `AIDE_GIT_TOKEN`) |
| `tevrin deployments <id> [--component frontend\|backend] [--env preview\|production]` | List deployments (`--target`/`-t` still works as the old name of `--env`) |
| `tevrin databases <id>` | List managed databases per environment: provider, status, region, plan, expiry, warnings |
| `tevrin migrations <id>` | List migration runs, newest first |
| `tevrin artifact list\|history\|show\|approve\|reject\|edit <id> ...` | Work with artifacts. `type` accepts `spec`, `plan` aliases |
| `tevrin artifact restore <id> <type> <version>` | Restore an older version as a new draft |
| `tevrin execute <id> [--pause\|--resume]` | Start, pause or resume execution |
| `tevrin review <id>` | Start a review cycle |
| `tevrin release <id>` | Approve the pending pre-release gate, else retry the release |
| `tevrin rollback <id> <checkpoint> --reason "..."` | Roll back to an earlier checkpoint of the version in development |
| `tevrin logs <id> [--type] [--follow]` | Activity feed |
| `tevrin config set\|get\|path`, `tevrin logout` | Settings and saved token |

Add `--json` for JSON output and `--api-url <url>` to pick a server.

| Setting | Detail |
| --- | --- |
| `TEVRIN_API_URL` | API base URL. Default `http://localhost:8080` |
| `TEVRIN_TOKEN` | Token that overrides the saved one |
| `TEVRIN_CONFIG` | Config file path. Default `%APPDATA%\aide\config.json` or `~/.config/aide/config.json` |
| `AIDE_API_URL`, `AIDE_TOKEN`, `AIDE_CONFIG` | Old names. Used only when the matching `TEVRIN_*` variable is unset or empty |
| Precedence | flag, then `TEVRIN_*`, then `AIDE_*`, then config file, then default |

A saved token is bound to the URL it was saved for and is sent only there. Plain `http` is allowed only for localhost, 127.0.0.1 and ::1. Exit codes: 0 success, 1 error, 2 usage, 3 auth (401/403), 4 conflict (409). `STACK_INCOMPATIBLE`, `STACK_LOCKED` and `LLM_PROVIDER_UNAVAILABLE` exit 4; `VCS_MISMATCH` exits 1.
