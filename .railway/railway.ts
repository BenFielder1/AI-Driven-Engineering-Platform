/**
 * AIDE on Railway (SPEC FR-107, FR-108, FR-109; STACK.md "Deploy"). One file for the whole project and both
 * environments (`preview`, `production`): `railway config plan` / `railway config apply` evaluate it against the
 * environment the CLI is linked to (or the one the project token is scoped to). Field names follow the `railway`
 * package's IaC types (`railway/iac`, 3.16.0) and https://docs.railway.com/infrastructure-as-code/reference.
 *
 * - No secret values here. Secrets are set per environment in Railway (deploy/railway/variables.md). Every API variable
 *   an operator may set in Railway (the required signing key and the full optional LLM / GitHub / SMTP set) is
 *   declared with `preserve()`. IaC owns every variable on a service, so an undeclared variable that exists in
 *   Railway is planned for deletion; that would also make CI's `railway config plan --detailed-exit-code` fail.
 *   `preserve()` keeps whatever Railway holds. The `railway` package only emits a `{ type: "preserve" }` marker (the
 *   diff engine is in the CLI), so "preserve() on a variable that isn't set plans no change" is a first-plan check in
 *   deploy/railway/README.md, with the per-environment fallback described there.
 * - `${{service.VAR}}` inside a plain string is Railway's own reference syntax, resolved by Railway at deploy time.
 *   Ordinary (non-template) string literals are used for those values so TypeScript doesn't interpolate them.
 * - Not expressible here (one-time dashboard/CLI steps in deploy/railway/README.md): the generated `*.up.railway.app`
 *   domain for `web`, Postgres and volume backup schedules, and the GitHub repo connection (none: deploys come from
 *   GitHub Actions with `railway up`, SPEC D-V3-7).
 */
import { database, defineRailway, preserve, project, service, volume } from 'railway/iac'

// FR-107 / FR-112: API variables set per environment in Railway, never in git (deploy/railway/variables.md). All are
// declared up front, so setting one in either environment needs no change here and never shows up as drift.
const operatorSetApiVariables = {
  // Required secret: one-click approval links (FR-48), 64 random characters, different per environment.
  Approvals__SigningKey: preserve(),
  // Optional: system LLM keys (FR-79) and a reachable Ollama.
  Llm__AllowSystemKeys: preserve(),
  Llm__Providers__Anthropic__ApiKey: preserve(),
  Llm__Providers__OpenAI__ApiKey: preserve(),
  Llm__Providers__Ollama__BaseUrl: preserve(),
  // Optional: GitHub App override (all five or none) and v1 OAuth sign-in.
  GitHub__App__AppId: preserve(),
  GitHub__App__ClientId: preserve(),
  GitHub__App__ClientSecret: preserve(),
  GitHub__App__PrivateKey: preserve(),
  GitHub__App__Slug: preserve(),
  GitHub__OAuth__ClientId: preserve(),
  GitHub__OAuth__ClientSecret: preserve(),
  // Optional: email channel (FR-47).
  Smtp__Host: preserve(),
  Smtp__Port: preserve(),
  Smtp__Username: preserve(),
  Smtp__Password: preserve(),
  Smtp__From: preserve(),
  Smtp__EnableSsl: preserve(),
  Smtp__Timeout: preserve(),
}

export default defineRailway(() => {
  // Railway Postgres 17 (SPEC §3.5). `postgres()` would provision the template's current default (postgres-ssl:18), so
  // the same template image is pinned to 17 through the generic helper, with postgres()'s own output and mount path.
  const db = database('postgres', 'postgres', {
    image: 'ghcr.io/railwayapp-templates/postgres-ssl:17',
    output: 'DATABASE_URL',
    defaultMountPath: '/var/lib/postgresql/data',
  })

  // Data Protection keys (/data/keys: they decrypt credentials stored in the DB) and Git working copies
  // (/data/workspaces). Each environment gets its own volume. 5 GB default (SPEC FR-107, Q2).
  const apiData = volume('api-data', { sizeMB: 5120 })

  const api = service('api', {
    rootDirectory: 'backend',
    build: { builder: 'DOCKERFILE', watchPatterns: ['/backend/**'] },
    // FR-109: migrations before the new container starts; a failure stops the deploy and the old one keeps serving.
    // Railway runs Dockerfile commands in exec form (no variable expansion), so a shell expands the variable.
    preDeploy: `/bin/sh -c 'exec /app/efbundle --connection "$ConnectionStrings__Aide"'`,
    healthcheck: '/api/health/ready',
    replicas: 1,
    // preDeployTimeoutSeconds: a hung migration fails the deploy after 10 minutes instead of blocking it.
    deploy: { restartPolicyType: 'ON_FAILURE', restartPolicyMaxRetries: 10, preDeployTimeoutSeconds: 600 },
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
      ...operatorSetApiVariables,
    },
  })

  const web = service('web', {
    rootDirectory: 'frontend',
    build: { builder: 'DOCKERFILE', watchPatterns: ['/frontend/**'] },
    healthcheck: '/healthz',
    replicas: 1,
    deploy: { restartPolicyType: 'ON_FAILURE', restartPolicyMaxRetries: 10 },
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

  return project('aide', { resources: [db, apiData, api, web] })
})
