# Railway variables (FR-112)

Every variable per Railway service, for both environments (`preview` and `production`). Sources:

- **Literal:** fixed value in [`.railway/railway.ts`](../../.railway/railway.ts), applied by `railway config apply`.
- **Reference:** Railway reference syntax `${{service.VAR}}`, also in `railway.ts`. Railway resolves it at deploy time.
- **Secret:** set by an operator per environment in Railway (dashboard **Variables**, or
  `… | railway variable set <NAME> --stdin --service api --environment <env> --skip-deploys`, so the value never
  appears in argv). Never in git, in `railway.ts` or in CI logs. Each environment gets its **own** values. Ignored by
  IaC (`variables.ignore`) in `railway.ts`.
- **Operator-set:** a non-secret optional value, set in Railway per environment only when used. Ignored by IaC
  (`variables.ignore`) in `railway.ts`, so it differs per environment with no code change.
- **Railway:** injected by Railway. Don't set it.

Configuration keys use `__` for `:` (`Auth__Cookie__SecurePolicy` = `Auth:Cookie:SecurePolicy`). Defaults and the meaning
of every API key are in the [README configuration reference](../../README.md#configuration-reference).

## `api` (ASP.NET Core, private only)

| Variable | Source | Value | Required | Notes |
| --- | --- | --- | --- | --- |
| `ASPNETCORE_ENVIRONMENT` | literal | `Production` | yes | Both environments (FR-108). Production config: no seed, no Fake providers |
| `ASPNETCORE_HTTP_PORTS` | literal | `8080` | yes | Kestrel binds `http://*:8080`, which is IPv6Any in dual mode. So `api.railway.internal` works whether it resolves to IPv4, IPv6 or both. No `ASPNETCORE_URLS` needed |
| `PORT` | literal | `8080` | yes | Railway healthchecks target `$PORT`. Kestrel ignores it |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | literal | `true` | yes | FR-105 |
| `ForwardedHeaders__ForwardLimit` | literal | `2` | yes | Browser → Railway edge → web → api. The rate limits see the client IP the edge appended |
| `DataProtection__KeysPath` | literal | `/data/keys` | yes | On the `api-data` volume. **Back it up** (see README) |
| `DataProtection__ApplicationName` | literal | `aide` | yes | Never change it: the key ring is bound to it |
| `Auth__Cookie__SecurePolicy` | literal | `Always` | yes | §8 security |
| `Seed__Enabled` | literal | `false` | yes | FR-108. No seed admin on Railway: register the first Admin yourself |
| `Telemetry__ConsoleExporter` | literal | `false` | yes | JSON logs only |
| `ConnectionStrings__Aide` | reference | `Host=${{postgres.PGHOST}};Port=${{postgres.PGPORT}};Database=${{postgres.PGDATABASE}};Username=${{postgres.PGUSER}};Password=${{postgres.PGPASSWORD}}` | yes | D-V3-9. Private network. Used by the app and by the pre-deploy migration bundle |
| `App__PublicBaseUrl` | reference | `https://${{web.RAILWAY_PUBLIC_DOMAIN}}` | yes | Resolves once `web` has a generated domain. Validated at start-up, so generate the domain before the first deploy |
| `Llm__Providers__Ollama__Enabled` | literal | `false` | yes | FR-157. Ollama has no reachable endpoint on Railway (the default `host.docker.internal:11434` doesn't resolve, and AIDE doesn't host an Ollama service), so it is turned off and hidden from every provider list |
| `Llm__Providers__Fake__Enabled` | literal | `false` | yes | FR-157. The Fake LLM never serves real users. Projects still bound to Fake from before v4 fail their next run with `LLM_PROVIDER_UNAVAILABLE` |
| `GitLab__AllowPrivateNetworks` | literal | `false` | yes | SPEC §8 SSRF guard. On Railway private addresses (10/8, 172.16/12, 192.168/16, fc00::/7, `*.railway.internal`) are AIDE's own services, so GitLab hosts resolving there are refused. Backend default `true` (Docker Desktop, dev loop) |
| `Approvals__SigningKey` | **secret** | 64 random characters | yes | One-click approval links (FR-48). Different per environment. Ignored in IaC (`railway.ts`). Under 32 characters means no approval links |
| `Llm__AllowSystemKeys` | operator-set (ignored in IaC) | `true` | no | Only if system LLM keys should be used (FR-79). Default `false` |
| `Llm__Providers__Anthropic__ApiKey` | **secret** (ignored in IaC) | | no | System key, used only with `Llm__AllowSystemKeys=true` |
| `Llm__Providers__OpenAI__ApiKey` | **secret** (ignored in IaC) | | no | Same as above |
| `Llm__MaxOutputTokensCeiling` | operator-set (ignored in IaC) | integer ≥ 1 | no | FR-160. Caps every call's resolved max output tokens (`min(catalog/discovered, ceiling)`). Unset = no ceiling |
| `Llm__StreamIdleTimeout` | operator-set (ignored in IaC) | `00:02:00` | no | FR-161. Fails a streaming call that receives no data for this long. Default `00:02:00` |
| `Llm__RequestTimeout` | operator-set (ignored in IaC) | `00:30:00` | no | FR-161. Whole-call ceiling for one LLM call. Default `00:30:00` (was shorter before v4) |
| `Llm__Providers__<P>__Catalog__<n>__Id`, `__Name`, `__MaxOutputTokens`, `__ContextTokens`, `__Recommended` | operator-set (**not** yet ignored in IaC) | | no | §4.24. Extends the model catalog shipped in `appsettings.json` for `Anthropic` or `OpenAI`. `<n>` continues after the shipped entries (a lower index overrides that shipped entry). Each exact name must be added to `operatorSetApiVariables` in `railway.ts` before it is set, or the next plan deletes it |
| `Llm__Providers__Ollama__BaseUrl` | — | **unset** | — | Ollama is disabled on Railway (FR-157), so it is no longer in `variables.ignore`. If an environment still has it, the next plan deletes it (not a secret; harmless) |
| `GitHub__App__AppId`, `GitHub__App__ClientId`, `GitHub__App__Slug` | operator-set (ignored in IaC) | | no | Only to override the App registered in Admin → Integrations. Set all five `GitHub__App__*` values or none |
| `GitHub__App__ClientSecret`, `GitHub__App__PrivateKey` | **secret** (ignored in IaC) | | no | Same as above. PEM with `\n` escapes |
| `GitHub__OAuth__ClientId` | operator-set (ignored in IaC) | | no | v1 GitHub sign-in, used only while no GitHub App exists |
| `GitHub__OAuth__ClientSecret` | **secret** (ignored in IaC) | | no | Same as above |
| `GitLab__Instances__0__Name` | operator-set (ignored in IaC) | e.g. `gitlab.com` | no | FR-130. Optional config-defined GitLab instance. Overrides a DB-registered instance (Admin → Integrations → Git hosts) with the same host. Validated at start-up: a bad instance stops `api` from starting. Set Name, ClientId and ClientSecret together. More instances (`__1__*`, ...) must be added to `operatorSetApiVariables` by exact name first |
| `GitLab__Instances__0__BaseUrl` | operator-set (ignored in IaC) | empty or `https://gitlab.example.com` | no | Empty or unset = `https://gitlab.com`. A host resolving to a private address is refused on Railway (`GitLab__AllowPrivateNetworks=false`), as are loopback, link-local and metadata addresses everywhere |
| `GitLab__Instances__0__ClientId` | operator-set (ignored in IaC) | | no | GitLab OAuth application id. Register the application with the redirect URI `https://<web-domain>/api/auth/gitlab/callback` for **this** environment (see README step 7) |
| `GitLab__Instances__0__ClientSecret` | **secret** (ignored in IaC) | | no | GitLab OAuth application secret. Set with `--stdin` |
| `GitLab__Instances__0__AllowInsecureHttp` | operator-set (ignored in IaC) | `false` | no | Default `false`. Only for a self-managed instance on plain `http`. Leave unset on Railway |
| `GitLab__RequestTimeout` | operator-set (ignored in IaC) | `00:00:20` | no | One GitLab API request. Default `00:00:20` |
| `Bitbucket__OAuth__ClientId` | operator-set (ignored in IaC) | | no | M28. Optional Bitbucket Cloud OAuth consumer, overriding the one registered in Admin → Integrations. Callback `https://<web-domain>/api/auth/bitbucket/callback` |
| `Bitbucket__OAuth__ClientSecret` | **secret** (ignored in IaC) | | no | Same as above. Set with `--stdin` |
| `Smtp__Host`, `Smtp__Username`, `Smtp__From`, `Smtp__Port`, `Smtp__EnableSsl`, `Smtp__Timeout` | operator-set (ignored in IaC) | | no | Email channel (FR-47). Leave a key out rather than blank: a blank number fails binding |
| `Smtp__Password` | **secret** (ignored in IaC) | | no | |
| `Deployments__AllowFake`, `Deployments__Provider=Fake`, `Databases__Provider=Fake`, `Git__Local__ImportRoot` | — | **unset** | — | Never on Railway (FR-108) |
| `Llm__Providers__Anthropic__Enabled`, `Llm__Providers__OpenAI__Enabled` | — | **unset** | — | Default `true`. Never disable both: startup fails unless at least one provider is enabled and `Llm:DefaultProvider` (default `anthropic`) is enabled (FR-157) |
| `RAILWAY_RUN_UID` | — | **unset** | — | The image starts as root and its entrypoint drops to `$APP_UID` (FR-106, D-V3-10) |
| `RAILWAY_PRIVATE_DOMAIN`, `RAILWAY_ENVIRONMENT`, … | Railway | | | Injected |

**Using an optional variable:** `railway config plan` / `apply` treat a variable that exists in Railway but not in
`railway.ts` as a deletion (and CI's drift check fails). Every optional variable above is already listed in
`operatorSetApiVariables` in `railway.ts` (the project's `variables.ignore`), so an environment starts using one by
setting it in Railway. Nothing else changes. For a name **not** in this table, add it to `operatorSetApiVariables`
first (or declare a literal, if it's the same non-secret value in both environments). `railway config plan` must then
show no changes and no destroys. While a listed name is unset, the plan prints
`variables.ignore pattern "<name>" matches no variable`: expected and harmless.

## `web` (Next.js custom server, public)

| Variable | Source | Value | Required | Notes |
| --- | --- | --- | --- | --- |
| `BACKEND_URL` | reference | `http://${{api.RAILWAY_PRIVATE_DOMAIN}}:8080` | yes | Server-only, read at runtime (never `NEXT_PUBLIC_*`). `/api` and the SignalR WebSockets go there |
| `PORT` | literal | `8080` | yes | Listen port, healthcheck port and the generated domain's target port |
| `NODE_ENV` | literal | `production` | yes | Also set in the image |
| `TRUST_EDGE_FORWARDED` | literal | `true` | yes | Keeps the `X-Forwarded-Proto`/`-Host` values Railway's edge sets, for the API's Secure cookies and the Live origin check (FR-98). Not set in Kubernetes |

## `postgres` (Railway Postgres 18)

Railway manages these (`PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`, `PGPASSWORD`, `DATABASE_URL`, `DATABASE_PUBLIC_URL`,
`POSTGRES_*`). Don't edit them. The API only uses the five `PG*` values, through `ConnectionStrings__Aide`.
`DATABASE_PUBLIC_URL` exists only if a TCP proxy is enabled. Keep it disabled.

## GitHub Actions (deploy workflow, FR-110)

| Name | Kind | Where | Notes |
| --- | --- | --- | --- |
| `RAILWAY_TOKEN` | secret | GitHub environment `preview`, and GitHub environment `production` | A Railway **project token** scoped to that Railway environment. Used by `railway up` and the read-only deployment, domain and TCP-proxy queries |
| `RAILWAY_API_TOKEN` | secret | both GitHub environments | A Railway **workspace token**. Used only by the read-only `railway config plan` drift check, because IaC rejects project tokens ("Unauthorized") |
| `SMOKE_EMAIL`, `SMOKE_PASSWORD` | secret | both GitHub environments | The per-environment smoke account (README). Optional: without them only the read-only checks run |
| `RAILWAY_WEB_URL` | variable | both GitHub environments | `https://<web domain>`. The smoke test base URL |
