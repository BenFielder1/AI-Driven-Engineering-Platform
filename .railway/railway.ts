/**
 * AIDE on Railway (SPEC FR-107, FR-108, FR-109; STACK.md "Deploy"). One file for the whole project and both
 * environments (`preview`, `production`): `railway config plan` / `railway config apply` evaluate it against the
 * environment the CLI is linked to (or the one the project token is scoped to). Field names follow the `railway`
 * package's IaC types (`railway/iac`, 3.16.0) and https://docs.railway.com/infrastructure-as-code/reference.
 *
 * - No secret values here. Secrets and optional settings are set per environment in Railway (deploy/railway/variables.md)
 *   and listed in the project's `variables.ignore` (`operatorSetApiVariables` below). IaC owns every variable it isn't
 *   told to ignore, so without this list a variable set in Railway would be planned for deletion (and CI's
 *   `railway config plan --detailed-exit-code` would fail). `variables.ignore` rather than `preserve()`: an ignored name
 *   may be set or unset, and plan warns (harmlessly) while it is unset. What apply does with `preserve()` for a variable
 *   that doesn't exist was never established: the early applies on preview failed on the volume size, not the variables. `variables` on `project()` is supported by the CLI's IaC engine (CLI 5.71) but not yet typed in the
 *   `railway` 3.16 package, hence the cast.
 * - `${{service.VAR}}` inside a plain string is Railway's own reference syntax, resolved by Railway at deploy time.
 *   Ordinary (non-template) string literals are used for those values so TypeScript doesn't interpolate them.
 * - Not expressible here (one-time dashboard/CLI steps in deploy/railway/README.md): the generated `*.up.railway.app`
 *   domain for `web`, Postgres and volume backup schedules, and the GitHub repo connection (none: deploys come from
 *   GitHub Actions with `railway up`, SPEC D-V3-7).
 */
import { database, defineRailway, project, service, volume } from 'railway/iac'

// FR-107 / FR-112: variables set per environment in Railway, never in git (deploy/railway/variables.md). IaC ignores
// these names, so setting or removing one in either environment needs no change here and never shows up as drift.
const operatorSetApiVariables = [
  // Required secret: one-click approval links (FR-48), 64 random characters, different per environment.
  'Approvals__SigningKey',
  // Optional: system LLM keys (FR-79). No Ollama base URL: Ollama is disabled on Railway (FR-157, env below).
  'Llm__AllowSystemKeys',
  'Llm__Providers__Anthropic__ApiKey',
  'Llm__Providers__OpenAI__ApiKey',
  // Optional: output-token ceiling (FR-160) and streaming timeouts (FR-161). Catalog extensions
  // (`Llm__Providers__<P>__Catalog__<n>__*`) are added here by exact name when an environment needs one.
  'Llm__MaxOutputTokensCeiling',
  'Llm__StreamIdleTimeout',
  'Llm__RequestTimeout',
  // Optional: GitHub App override (all five or none) and v1 OAuth sign-in.
  'GitHub__App__AppId',
  'GitHub__App__ClientId',
  'GitHub__App__ClientSecret',
  'GitHub__App__PrivateKey',
  'GitHub__App__Slug',
  'GitHub__OAuth__ClientId',
  'GitHub__OAuth__ClientSecret',
  // Optional: one config-defined GitLab instance (FR-130; overrides a DB-registered instance with the same host) and
  // the GitLab request timeout. More instances (`GitLab__Instances__1__*`, ...) are added here by exact name.
  // GitLab__AllowPrivateNetworks is a literal on `api` below, not operator-set.
  'GitLab__Instances__0__Name',
  'GitLab__Instances__0__BaseUrl',
  'GitLab__Instances__0__ClientId',
  'GitLab__Instances__0__ClientSecret',
  'GitLab__Instances__0__AllowInsecureHttp',
  'GitLab__RequestTimeout',
  // Optional: Bitbucket Cloud OAuth consumer override (M28).
  'Bitbucket__OAuth__ClientId',
  'Bitbucket__OAuth__ClientSecret',
  // Optional: email channel (FR-47).
  'Smtp__Host',
  'Smtp__Port',
  'Smtp__Username',
  'Smtp__Password',
  'Smtp__From',
  'Smtp__EnableSsl',
  'Smtp__Timeout',
]

export default defineRailway(() => {
  // Railway Postgres 18 (SPEC §3.5, D-V3-13). Railway always provisions a database from its template default
  // (postgres-ssl:18) and applies a different `image` only afterwards as an update. Postgres can't open a newer major's
  // data directory, so pinning 17 left the server unable to start (preview, 2026-10-24). The pin matches what Railway
  // creates, so a change to the template default can't silently change the major version. Local Kubernetes, compose,
  // CI and Testcontainers stay on 17.
  const db = database('postgres', 'postgres', {
    image: 'ghcr.io/railwayapp-templates/postgres-ssl:18',
    output: 'DATABASE_URL',
    defaultMountPath: '/var/lib/postgresql/data',
  })

  // Data Protection keys (/data/keys: they decrypt credentials stored in the DB) and Git working copies
  // (/data/workspaces). Each environment gets its own volume. 500 MB is the current Railway plan's maximum (the first
  // applies on preview failed with "Max size of 500 MB on current plan"). Raise it (SPEC Q2: 5 GB) after upgrading the plan.
  // Region pinned to where Railway placed it (us-east, `iad`): leaving it unset plans a destructive volume change.
  const apiData = volume('api-data', { sizeMB: 500, region: 'iad' })

  // No watchPatterns on either service: deploys are explicit `railway up` uploads from deploy.yml, and with watch paths
  // Railway skips the build when a commit only touches other paths (it skipped api's build, 2026-10-24). Note: omitting
  // the field doesn't clear existing patterns on apply; they were cleared once by applying `watchPatterns: []`.
  const api = service('api', {
    // Railway records a repo-less service with a root directory as source type "github" (no repo is connected:
    // deploys come from `railway up` in deploy.yml). Declared as Railway stores it, so plan shows no drift.
    source: { type: 'github', rootDirectory: 'backend' },
    build: { builder: 'DOCKERFILE', dockerfilePath: 'Dockerfile' },
    // FR-109: migrations before the new container starts; a failure stops the deploy and the old one keeps serving.
    // Railway runs Dockerfile commands in exec form (no variable expansion), so a shell expands the variable.
    preDeploy: `/bin/sh -c 'exec /app/efbundle --connection "$ConnectionStrings__Aide"'`,
    healthcheck: '/api/health/ready',
    replicas: 1,
    // preDeployTimeoutSeconds: a hung migration fails the deploy after 10 minutes instead of blocking it.
    // Restart policy is Railway's default (ON_FAILURE, 10 retries). Railway stores defaults as null, so declaring them
    // here would show as permanent drift.
    deploy: { preDeployTimeoutSeconds: 600 },
    // No `domains` and no generated domain: the API is reachable only on the private network (SPEC §8).
    volumeMounts: { '/data': apiData },
    env: {
      ASPNETCORE_ENVIRONMENT: 'Production',
      // Kestrel binds http://*:8080, i.e. IPv6Any in dual mode, so api.railway.internal works over IPv4 and IPv6.
      ASPNETCORE_HTTP_PORTS: '8080',
      // Railway healthchecks target $PORT; Kestrel itself ignores PORT.
      PORT: '8080',
      // FR-105: browser -> Railway edge -> web -> api. Two trusted X-Forwarded-For hops.
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true',
      ForwardedHeaders__ForwardLimit: '2',
      DataProtection__KeysPath: '/data/keys',
      DataProtection__ApplicationName: 'aide',
      Auth__Cookie__SecurePolicy: 'Always',
      Seed__Enabled: 'false',
      Telemetry__ConsoleExporter: 'false',
      // D-V3-9: one Npgsql connection string for the app and the migration bundle, over the private network.
      ConnectionStrings__Aide:
        'Host=${{postgres.PGHOST}};Port=${{postgres.PGPORT}};Database=${{postgres.PGDATABASE}};' +
        'Username=${{postgres.PGUSER}};Password=${{postgres.PGPASSWORD}}',
      App__PublicBaseUrl: 'https://${{web.RAILWAY_PUBLIC_DOMAIN}}',
      // FR-157: only Anthropic and OpenAI on Railway. Ollama has no reachable default here (host.docker.internal
      // doesn't resolve) and Fake must never serve real users. Startup validation requires the default provider
      // (appsettings) to be enabled.
      Llm__Providers__Ollama__Enabled: 'false',
      Llm__Providers__Fake__Enabled: 'false',
      // SPEC §8: on Railway, private addresses (10/8, *.railway.internal, ...) are AIDE's own services, so GitLab hosts
      // there are refused (SSRF). The backend default (true) suits Docker Desktop and the dev loop.
      GitLab__AllowPrivateNetworks: 'false',
    },
  })

  const web = service('web', {
    source: { type: 'github', rootDirectory: 'frontend' },
    build: { builder: 'DOCKERFILE', dockerfilePath: 'Dockerfile' },
    healthcheck: '/healthz',
    replicas: 1,
    // The public *.up.railway.app domain is generated once per environment (`railway domain --service web`, target
    // port 8080). Generated domains are not part of IaC; a custom domain would go in `domains` (SPEC Q3).
    env: {
      BACKEND_URL: 'http://${{api.RAILWAY_PRIVATE_DOMAIN}}:8080',
      PORT: '8080',
      NODE_ENV: 'production',
      // Railway's edge always sets X-Forwarded-Proto/Host; keep them for the API (FR-98, FR-105).
      TRUST_EDGE_FORWARDED: 'true',
    },
  })

  // `variables` isn't in the railway 3.16 types yet; project() passes unknown fields through to the CLI engine.
  return project('aide', {
    resources: [db, apiData, api, web],
    variables: { managed: true, ignore: operatorSetApiVariables },
  } as Parameters<typeof project>[1])
})
