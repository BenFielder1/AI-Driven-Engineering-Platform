---
title: Configuration
parent: Reference
nav_order: 1
---

# Configuration and environment variables

ASP.NET Core reads `appsettings.json`, `appsettings.Development.json`, user-secrets (Development), then environment variables. In an environment variable, replace `:` with `__` (for example `Llm__Providers__Anthropic__ApiKey`).

- **Dev loop:** non-secrets in `backend/src/Aide.Api/appsettings*.json` or environment variables; secrets with `dotnet user-secrets set "<Key>" "<value>" --project backend/src/Aide.Api` (run from the repo root).
- **Kubernetes:** secrets and any extra key go in `deploy/k8s/overlays/local/secrets.env` (`KEY=value`, no quotes). Non-secrets are in `deploy/k8s/base/configmap.yaml` and `deploy/k8s/overlays/local/app-config-local.yaml`. The API loads both as environment variables. Re-run `deploy-local.sh` after a change.
- **Railway (FR-112):** every variable per service and environment, and whether it is a literal, a reference, a secret or operator-set, is in [`deploy/railway/variables.md`](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/blob/main/deploy/railway/variables.md). Literals and references live in [`.railway/railway.ts`](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/blob/main/.railway/railway.ts). Secrets and optional values are set per environment in Railway (`railway variable set <NAME> --stdin ...`) and listed in `variables.ignore` in `railway.ts`, so IaC doesn't plan to delete them.

Values are never shown here. "Secret" means: never commit it, set it only through user-secrets, `secrets.env` or Railway variables.

Server settings keep their internal `aide` names (for example `ConnectionStrings__Aide`). Only the CLI variables were renamed, see the last table.

## Settings

| Key (env var form) | Purpose | Default | Secret |
| --- | --- | --- | --- |
| `ConnectionStrings:Aide` (`ConnectionStrings__Aide`) | PostgreSQL connection | none (required) | yes |
| `POSTGRES_PASSWORD` | Password for the Postgres container (first init only) | none | yes |
| `POSTGRES_DB`, `POSTGRES_USER` | Compose / StatefulSet database name and user | `aide` | no |
| `DataProtection:ApplicationName` | Data Protection app name (keep stable) | `aide` | no |
| `DataProtection:KeysPath` | Key ring directory | `/data/keys` in a container, else `%LOCALAPPDATA%/aide/keys` | no |
| `Workspaces:Root` | Git working copies and local repos | `/data/workspaces` in a container, else `%LOCALAPPDATA%/aide/workspaces` | no |
| `Telemetry:ConsoleExporter` | OpenTelemetry console exporter | `false` (`true` in Development) | no |
| `Seed:Enabled` | Create the seed admin and demo projects | `false` (`true` in Development and the local k8s overlay) | no |
| `App:PublicBaseUrl` | Browser-facing base URL (absolute http(s), validated at start-up): notification links, the GitHub App manifest's callback and setup URLs, PR links back to Tevrin | `http://localhost:8080` (`http://localhost:3000` in Development) | no |
| `Approvals:SigningKey` | HMAC key for one-click approval links, at least 32 chars | none: no links are created | yes |
| `Auth:Cookie:SecurePolicy` | Auth cookie `Always`, `SameAsRequest` or `None` | `SameAsRequest` | no |
| `Auth:RateLimits:LoginPerMinute` | Login attempts per IP per minute | `5` | no |
| `Auth:RateLimits:RegisterPerMinute` | Registrations per IP per minute | `5` | no |
| `Live:AllowedOrigins` (`Live__AllowedOrigins__0`, ...) | Extra browser origins allowed on the SignalR hub | empty | no |
| `Jobs:Enabled` | Run the background job worker | `true` | no |
| `Jobs:PollInterval` | Idle poll interval | `00:00:02` | no |
| `Jobs:Concurrency` | Jobs run at once | `4` | no |
| `Jobs:LeaseDuration` | Lease before a stalled job is re-queued | `00:05:00` | no |
| `Llm:DefaultProvider` | `anthropic`, `openai`, `ollama` or `fake` | `anthropic` | no |
| `Llm:RequestTimeout` | Whole provider call, including a streamed answer (FR-161) | `00:30:00` | no |
| `Llm:StreamIdleTimeout` | Fails a streamed call that receives no data for this long (FR-161) | `00:02:00` | no |
| `Llm:KeyCheckTimeout` | Per key check when a connection is added or re-tested (FR-78); a timeout is `UPSTREAM_ERROR` | `00:00:15` | no |
| `Llm:MaxOutputTokens` | Output token limit per call for models **not** in their provider's catalog (and, for Anthropic, whose limit `GET /v1/models/{id}` couldn't report). Listed models use their catalog `MaxOutputTokens` (FR-160). An answer cut off at the limit is continued and joined; one still cut off records the `output_truncated` run warning | `16000` | no |
| `Llm:MaxOutputTokensCeiling` | Optional cap (at least 1) on every call's output token limit, whatever the catalog says (FR-160) | unset | no |
| `Llm:Providers:<Name>:Enabled` | Whether the provider (`Anthropic`, `OpenAI`, `Ollama`, `Fake`) is offered on this deployment (FR-157). A disabled provider is absent from every list, refused on project create and LLM settings (`LLM_PROVIDER_UNAVAILABLE`), and fails the runs of projects still set to it. At least one must be enabled, and `Llm:DefaultProvider` must be one of them. Railway sets `Ollama` and `Fake` to `false` | `true` | no |
| `Llm:Providers:<Name>:Catalog:<n>:Id`, `Name`, `MaxOutputTokens`, `ContextTokens`, `Recommended` | The provider's model catalog (FR-158, §4.24): what the wizard offers and each model's output limit and context window. Shipped in `appsettings.json`; extend it per deployment with the next index (e.g. `Llm__Providers__Anthropic__Catalog__7__Id`). Ids must be unique per provider, `MaxOutputTokens` at least 1. The OpenAI entries (`gpt-6-astra`, `gpt-6.1-sol`: 16,000 output, 400,000 context) are **unverified** placeholders until checked against OpenAI's model docs (SPEC §11 Q5) | see `appsettings.json` | no |
| `Llm:AllowSystemKeys` | Whether the system keys below may be used, and then only for projects with no bound connection (D-V2-5, FR-79). Otherwise runs use the key an Owner bound from Account → Connections | `false` | no |
| `Llm:Providers:Anthropic:ApiKey` | Anthropic system key (used only with `Llm:AllowSystemKeys=true`) | none | yes |
| `Llm:Providers:OpenAI:ApiKey` | OpenAI system key (used only with `Llm:AllowSystemKeys=true`) | none | yes |
| `Llm:Providers:<Name>:BaseUrl` | Endpoint override (`Anthropic`, `OpenAI`, `Ollama`, `Fake`). Ollama: `Llm__Providers__Ollama__BaseUrl` | Ollama: `http://localhost:11434` (Development, no container) or `http://host.docker.internal:11434` (container) | no |
| `Llm:Providers:<Name>:DefaultModel` | Model when an agent has no own entry | see section below | no |
| `Llm:Providers:<Name>:Models:<Agent>` | Model per agent, e.g. `Llm__Providers__Anthropic__Models__StackAnalyst` for the 0.5.0 Stack Analyst (FR-180) | see section below | no |
| `Llm:Pricing:<model>:InputPerMTok`, `OutputPerMTok` | USD per million tokens, for cost estimates | see appsettings; unpriced = 0 | no |
| `GitHub:App:AppId` | Tevrin GitHub App id (FR-80). All five `GitHub:App:*` values together override the App registered from Admin → Integrations → GitHub; set none or all | none: the registered App, if any | no |
| `GitHub:App:ClientId` | The App's client id (sign-in and connections) | none | no |
| `GitHub:App:ClientSecret` | The App's client secret | none | yes |
| `GitHub:App:PrivateKey` | The App's PEM private key; `\n` escapes allowed (one line in `secrets.env`) | none | yes |
| `GitHub:App:Slug` | The App's slug (install link `https://github.com/apps/<slug>/installations/new`) | none | no |
| `GitHub:OAuth:ClientId` | GitHub sign-in OAuth App client id (0.1.0; used only while no GitHub App exists, D-V2-7) | none: sign-in disabled | no (treat as config) |
| `GitHub:OAuth:ClientSecret` | GitHub sign-in OAuth App secret | none | yes |
| `GitHub:GitBaseUrl` | Where repos are cloned from and pushed to | `https://github.com/` | no |
| `GitHub:ApiBaseUrl` | GitHub REST API | `https://api.github.com/` | no |
| `GitHub:ReleaseMergeMethod` | PR merge at release: `merge`, `squash`, `rebase` | `merge` | no |
| `GitLab:AllowPrivateNetworks` | Allow GitLab hosts that resolve to private ranges (10/8, 172.16/12, 192.168/16, fc00::/7, `*.railway.internal`). Loopback, link-local and metadata addresses are always refused (SPEC §8). `false` on Railway | `true` (dev loop and the local k8s overlay) | no |
| `GitLab:Instances:<n>:Name`, `BaseUrl`, `ClientId`, `AllowInsecureHttp` | Config-defined GitLab instance (FR-130), e.g. `GitLab__Instances__0__BaseUrl`. `BaseUrl` empty = `https://gitlab.com`. Overrides a DB-registered instance (Admin → Integrations → Git hosts) with the same host. Validated at start-up. `AllowInsecureHttp` permits a plain-`http` self-managed instance | none: the DB-registered instances only (`AllowInsecureHttp` `false`) | no |
| `GitLab:Instances:<n>:ClientSecret` | That instance's GitLab OAuth application secret | none | yes |
| `GitLab:RequestTimeout` | One GitLab API request | `00:00:20` | no |
| `Bitbucket:OAuth:ClientId` | Bitbucket Cloud OAuth consumer key (FR-133), e.g. `Bitbucket__OAuth__ClientId`. Overrides the consumer saved in Admin → Integrations → Bitbucket. Set together with the secret (validated at start-up) | none: the DB-saved consumer only | no |
| `Bitbucket:OAuth:ClientSecret` | That consumer's secret | none | yes |
| `Bitbucket:RequestTimeout` | One Bitbucket API request | `00:00:20` | no |
| `Git:Local:ImportRoot` | Folder under which a Local import (`git.localSourcePath`) must live. Empty disables Local import. E2E and dev only | unset (`/data/import` in the E2E overlay) | no |
| `Deployments:Provider` | `Vercel` (the real platform APIs of every deployment provider; the name is historical) or `Fake`. Fake makes every deployment check deterministic and lets Local Git projects use deployments | `Vercel` (`Fake` in the E2E overlay) | no |
| `Deployments:AllowFake` | Allows `Fake` (for deployments, databases and trackers) outside Development and Testing. Without it the API refuses to start with Fake | `false` (`true` in the E2E overlay) | no |
| `Databases:Provider` | `Platform` or `Fake`. Fake makes database provisioning and the migration runner deterministic, with no network and no real database. Set it with `Deployments:Provider`: each is read on its own | `Platform` (`Fake` in Development and the E2E overlay) | no |
| `Notifications:AllowPrivateTargets` | Let Slack, Teams, webhooks reach private addresses (local testing only) | `false` | no |
| `Notifications:RequestTimeout` | One webhook request, at most 60 s | `00:00:10` | no |
| `Notifications:SweepEnabled` | Run delivery retries and gate reminders | `true` | no |
| `Notifications:DeliverySweepInterval` | How often due retries run | `00:01:00` | no |
| `Notifications:GateSweepInterval` | How often gates are checked for reminders | `01:00:00` | no |
| `Notifications:DatabaseExpirySweepInterval` | How often the `database_expiry_sweep` job is queued: it reads each database's state to raise "database expiring" and mark `expiring`/`paused` (FR-155). Positive | `01:00:00` | no |
| `Notifications:ConnectionExpirySweepInterval` | How often Jira and Linear connections are checked for a user-entered expiry within 14 days, raising "connection expiring" (FR-165). No provider calls. Positive | `01:00:00` | no |
| `Trackers:Provider` | Issue trackers for imports (Jira, Linear, GitHub, GitLab; FR-165..FR-169): unset or `Real` = the real APIs with each user's own connection, `Fake` = recorded fixtures with no network. Any other value stops the API at start-up. `Fake` outside Development and Testing also needs `Deployments:AllowFake=true`. Never set on Railway | unset (real; `Fake` in CI E2E and the E2E overlay) | no |
| `Trackers:SearchTimeout` | One tracker search or fetch; over it the call fails with `UPSTREAM_ERROR`. Greater than zero and at most `00:02:00` | `00:00:15` | no |
| `Notifications:SweepBatchSize` | Deliveries retried per sweep | `50` | no |
| `Smtp:Host` | SMTP server. Blank: email skipped, other channels still send | blank | no |
| `Smtp:Port` | SMTP port | `587` | no |
| `Smtp:Username` | SMTP user | none | no |
| `Smtp:Password` | SMTP password | none | yes |
| `Smtp:From` | Sender address | `aide@aide.local` | no |
| `Smtp:EnableSsl` | TLS for SMTP | `true` | no |
| `Smtp:Timeout` | One whole send, positive and at most `00:01:00` | `00:00:15` | no |
| `BACKEND_URL` | Web server only (`frontend/server.ts`): API origin it proxies `/api` to, WebSocket included | `http://localhost:5080` | no |
| `TRUST_EDGE_FORWARDED` | Web server only: keep the edge's `X-Forwarded-Proto`/`-Host`. `true` on Railway | unset (off) | no |
| `PORT` | Web server listen port (and Railway healthcheck port) | `8080` (`3000` in dev) | no |
| `ForwardedHeaders:ForwardLimit` (`ForwardedHeaders__ForwardLimit`) | Forwarded hops the API trusts. `2` on Railway (edge, web) | `1` | no |
| `ASPNETCORE_ENVIRONMENT` | `Development` (dev loop) or `Production` (k8s) | set by launch profile / configmap | no |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | Trust the web server's `X-Forwarded-*` | `true` in the k8s configmap | no |

`GitHub:AllowInsecureGitBaseUrl` exists for tests only. Do not set it.

**Egress (0.5.0 issue imports):** with real trackers the API calls `*.atlassian.net` (only `https://<site>/_edge/tenant_info`, to resolve a Jira site), `api.atlassian.com` (Jira REST) and `api.linear.app` (Linear GraphQL). GitHub and GitLab issues use the hosts Tevrin already reaches (`api.github.com`, registered GitLab instances). Allow these if outbound traffic is filtered. `Trackers:Provider=Fake` makes no tracker calls.

## Where each value lives

| Kind | Dev loop | Kubernetes |
| --- | --- | --- |
| Secrets (marked "yes") | `dotnet user-secrets set "<Key>" "<value>" --project backend/src/Aide.Api` | `deploy/k8s/overlays/local/secrets.env`, as `Key__Sub=value` |
| `App:PublicBaseUrl` | `appsettings.Development.json` (`http://localhost:3000`) | `app-config-local.yaml` |
| `Deployments:*`, `Databases:Provider`, `Trackers:Provider`, `Git:Local:ImportRoot`, `Llm:DefaultProvider` for E2E | environment variables | `deploy/k8s/overlays/local-e2e/app-config-e2e.yaml` (`--e2e` only) |
| Other non-secrets | `appsettings*.json` or environment variables | `deploy/k8s/base/configmap.yaml` |

Platform tokens (Vercel, Netlify, Cloudflare, Render, Railway, Supabase), database connection strings, user LLM keys, GitHub, GitLab and Bitbucket user tokens, registered GitLab instances and the Bitbucket consumer, the registered GitHub App and deployment env values are not configuration. They are stored encrypted in the database and are never shown.

## CLI variables

| Variable | Purpose | Fallback |
| --- | --- | --- |
| `TEVRIN_TOKEN` | API token for the CLI | `AIDE_TOKEN` |
| `TEVRIN_API_URL` | API base URL | `AIDE_API_URL` |
| `TEVRIN_CONFIG` | CLI config file path | `AIDE_CONFIG` |
| `TEVRIN_GIT_TOKEN` | Git host PAT for `project init` and `stack detect` | `AIDE_GIT_TOKEN` |

Resolution order: flag, `TEVRIN_*`, `AIDE_*`, config file, default. An empty value counts as unset. See the [CLI guide](../guides/cli.md).

## Railway

Every Railway variable per service and environment is in [`deploy/railway/variables.md`](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/blob/main/deploy/railway/variables.md). Literals and references live in [`.railway/railway.ts`](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/blob/main/.railway/railway.ts). See [Railway](../operations/railway.md).
