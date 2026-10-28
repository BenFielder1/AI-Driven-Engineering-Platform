import type { Schemas } from '../../api/client'

/**
 * A trimmed stand-in for the shipped stack catalog (FR-113) and its compatibility engine (§4.17), for Vitest only. The
 * real rules live in Aide.Domain (StackCompatibility); this evaluates the subset the UI tests exercise, with the same
 * reason texts. The UI never evaluates rules itself (D-V4-19): it only renders what these handlers answer.
 */

type Catalog = Schemas['StackCatalogDto']
type Option = Schemas['StackOptionDto']
type OptionState = Schemas['StackOptionStateDto']
type ChoiceState = Schemas['StackChoiceStateDto']
type Request = Schemas['StackCompositionRequest']
type Composition = Schemas['StackCompositionDto']

export const STACK_CATALOG_VERSION = '2026.10.1'
export const STACK_CATALOG_HASH = 'sha256:5f1c0a7e9b2d4c6a8e0f1b3d5c7a9e1f3b5d7c9a1e3f5b7d9c1a3e5f7b9d1c3a'

const source = (plugin: string) => ({ plugin, version: '1.0.0', snapshot: '2026-10-20' })

function pack(id: string, name: string, description: string, chips: string[] = [], mode: string | null = null): Option {
  return {
    id,
    name,
    description,
    chips,
    pack: id,
    packVersion: '1.0.0+aide.1',
    source: source(id),
    modes: [],
    mode,
  }
}

function plain(id: string, name: string, description: string, packId: string | null = null): Option {
  return { id, name, description, chips: [], pack: packId, packVersion: packId ? '1.0.0+aide.1' : null, source: null, modes: [], mode: null }
}

const none = (what: string) => plain('none', 'None', what)

export const stackCatalog: Catalog = {
  version: STACK_CATALOG_VERSION,
  contentHash: STACK_CATALOG_HASH,
  layers: [
    { id: 'frontend', name: 'Frontend', description: 'The user interface.' },
    { id: 'backend', name: 'Backend', description: 'The API and background work.' },
    { id: 'data', name: 'Data', description: 'The database and how the code talks to it.' },
    { id: 'vcs', name: 'VCS', description: 'Where the repository lives and CI runs.' },
    { id: 'deploy', name: 'Deployment', description: 'Where each part runs, and the local dev loop.' },
  ],
  choices: [
    {
      id: 'frontend',
      layer: 'frontend',
      name: 'Frontend',
      options: [
        pack('fe-react-vite', 'React + Vite', 'A single-page app built with Vite.', ['TypeScript', 'SPA']),
        pack('fe-nextjs', 'Next.js', 'App Router, full-stack or the UI of a backend.', ['TypeScript', 'SSR']),
        pack('fe-vue', 'Vue', 'A Vue 3 single-page app.', ['TypeScript', 'SPA']),
        none('No user interface (API only).'),
      ],
    },
    {
      id: 'backend',
      layer: 'backend',
      name: 'Backend',
      options: [
        pack('be-aspnet', 'ASP.NET Core', 'Minimal APIs on .NET.', ['C#']),
        pack('be-fastapi', 'FastAPI', 'Async Python APIs.', ['Python']),
        pack('be-express', 'Express', 'Node.js APIs.', ['TypeScript']),
        none('No separate backend.'),
      ],
    },
    {
      id: 'data',
      layer: 'data',
      name: 'Data',
      options: [
        pack('data-efcore-postgres', 'EF Core + PostgreSQL', 'EF Core migrations on PostgreSQL.', ['ORM']),
        pack('data-efcore-sqlserver', 'EF Core + SQL Server', 'EF Core migrations on SQL Server.', ['ORM']),
        pack('data-sqlalchemy', 'SQLAlchemy', 'SQLAlchemy with Alembic migrations.', ['ORM']),
        pack('data-prisma', 'Prisma', 'Prisma schema and migrations.', ['ORM']),
        pack('data-supabase', 'Supabase (BaaS)', 'Supabase database, auth and row-level security.', ['BaaS'], 'baas'),
        none('No database.'),
      ],
    },
    {
      id: 'vcs',
      layer: 'vcs',
      name: 'Version control',
      options: [
        pack('vcs-github', 'GitHub', 'GitHub with GitHub Actions.'),
        pack('vcs-gitlab', 'GitLab', 'GitLab with GitLab CI.'),
      ],
    },
    {
      id: 'frontendHost',
      layer: 'deploy',
      name: 'Frontend host',
      options: [
        pack('deploy-vercel', 'Vercel', 'Preview and production deployments.'),
        pack('deploy-netlify', 'Netlify', 'Static sites and functions.'),
        pack('deploy-render', 'Render', 'Static sites and services.'),
        pack('deploy-railway', 'Railway', 'Containers on Railway.'),
        none('Local only.'),
      ],
    },
    {
      id: 'backendHost',
      layer: 'deploy',
      name: 'Backend host',
      options: [
        pack('deploy-vercel', 'Vercel', 'Serverless functions.'),
        pack('deploy-render', 'Render', 'Docker services.'),
        pack('deploy-railway', 'Railway', 'Containers on Railway.'),
        none('Local only.'),
      ],
    },
    {
      id: 'databaseHost',
      layer: 'deploy',
      name: 'Database host',
      options: [
        plain('supabase', 'Supabase', 'Managed PostgreSQL on Supabase.', 'data-supabase'),
        plain('render-postgres', 'Render Postgres', 'Managed PostgreSQL on Render.', 'deploy-render'),
        plain('railway-postgres', 'Railway Postgres', 'PostgreSQL on Railway.', 'deploy-railway'),
        plain('external', 'External', 'A connection string you enter (e.g. Neon or Azure SQL).'),
        none('Local only.'),
      ],
    },
    {
      id: 'local',
      layer: 'deploy',
      name: 'Local dev loop',
      options: [plain('compose', 'Docker Compose', 'The database and backend in Docker Compose.', 'local-docker-desktop'), none('Run everything yourself.')],
    },
    {
      id: 'originModel',
      layer: 'deploy',
      name: 'Origin model',
      options: [
        plain('same-origin', 'Same origin', 'The browser calls /api on the same origin.'),
        plain('proxy', 'Proxy', 'The frontend host proxies /api to the backend.'),
        plain('cross-origin', 'Cross-origin', 'The browser calls the backend URL directly (CORS).'),
      ],
    },
  ],
  rules: [
    { id: 'R1', statement: "Frontend and Backend can't both be None." },
    { id: 'R3', statement: 'Data packs need their backend.' },
  ],
  recommendations: [
    {
      id: 'react-aspnet',
      name: 'React + ASP.NET Core',
      choices: { frontend: ['fe-react-vite'], backend: ['be-aspnet'], data: ['data-efcore-postgres'] },
    },
  ],
}

type Facts = Partial<Record<string, string>>

interface Verdict {
  state: 'allowed' | 'caveat' | 'forbidden'
  reasons: Schemas['StackReasonDto'][]
}

const ok: Verdict = { state: 'allowed', reasons: [] }
const no = (rule: string, text: string): Verdict => ({ state: 'forbidden', reasons: [{ rule, text }] })
const warn = (rule: string, text: string): Verdict => ({ state: 'caveat', reasons: [{ rule, text }] })

const dataNeeds: Record<string, { backends: string[]; text: string; nextjsFullStack?: boolean }> = {
  'data-efcore-postgres': { backends: ['be-aspnet'], text: 'EF Core + PostgreSQL needs ASP.NET Core' },
  'data-efcore-sqlserver': { backends: ['be-aspnet'], text: 'EF Core + SQL Server needs ASP.NET Core' },
  'data-sqlalchemy': { backends: ['be-fastapi'], text: 'SQLAlchemy needs FastAPI' },
  'data-prisma': { backends: ['be-express'], text: 'Prisma needs Express, NestJS or Next.js full-stack', nextjsFullStack: true },
  'data-supabase': { backends: ['none'], text: 'Supabase (BaaS) needs Backend None' },
}

/** R10 (trimmed): the origin models allowed for the facts so far, and the default. */
function originModels(f: Facts): { allowed: string[]; default: string | null; text: string } {
  if (f.frontend === 'none') {
    return { allowed: [], default: null, text: "API only: there's no frontend, so there's no origin model" }
  }
  if (f.backend === 'none') {
    return { allowed: ['same-origin'], default: 'same-origin', text: 'No separate backend: the frontend serves its own /api, or calls none' }
  }
  if (!f.frontendHost || f.frontendHost === 'none') {
    return { allowed: ['same-origin'], default: 'same-origin', text: "The frontend isn't deployed: same-origin through the local proxy" }
  }
  if (f.frontendHost === 'deploy-vercel' && f.frontend !== 'fe-nextjs') {
    return { allowed: ['cross-origin'], default: 'cross-origin', text: 'A static SPA on Vercel is cross-origin only' }
  }
  return { allowed: ['proxy', 'cross-origin'], default: 'proxy', text: 'The frontend host proxies /api to the backend’s URL' }
}

function verdict(choice: string, option: string, f: Facts): Verdict {
  switch (choice) {
    case 'backend':
      return option === 'none' && f.frontend === 'none' ? no('R1', "Frontend and Backend can't both be None") : ok
    case 'data': {
      const need = dataNeeds[option]
      if (!need) {
        return ok
      }
      const fullStack = need.nextjsFullStack && f.frontend === 'fe-nextjs' && f.backend === 'none'
      if (!fullStack && !need.backends.includes(f.backend ?? '')) {
        return no('R3', need.text)
      }
      return option === 'data-efcore-sqlserver'
        ? warn('R4', "No managed SQL Server on the catalog's platforms: use an External connection string (e.g. Azure SQL) or local only.")
        : ok
    }
    case 'frontendHost':
      if (option === 'none') {
        return ok
      }
      if (f.frontend === 'none') {
        return no('R6', "There's no frontend to host")
      }
      return option === 'deploy-railway' && f.vcs === 'vcs-gitlab' ? no('R9', 'Railway only builds from GitHub') : ok
    case 'backendHost':
      if (option === 'none') {
        return ok
      }
      if (f.backend === 'none') {
        return no('R7', "There's no backend to host")
      }
      if (option === 'deploy-vercel' && f.backend === 'be-aspnet') {
        return no('R7', "Vercel can't run ASP.NET Core")
      }
      if (option === 'deploy-railway' && f.vcs === 'vcs-gitlab') {
        return no('R9', 'Railway only builds from GitHub')
      }
      return option === 'deploy-vercel' ? warn('R7', 'Serverless functions: no background work, pooled DB connections') : ok
    case 'databaseHost':
      if (f.data === 'data-supabase') {
        return option === 'supabase' ? ok : no('R8', 'Supabase (BaaS) runs on Supabase')
      }
      if (option === 'none') {
        return ok
      }
      if (f.data === 'none') {
        return no('R8', "There's no data layer, so no database to host")
      }
      if (f.data === 'data-efcore-sqlserver' && option !== 'external') {
        return no('R8', `${option === 'supabase' ? 'Supabase' : option === 'render-postgres' ? 'Render Postgres' : 'Railway Postgres'} is PostgreSQL: SQL Server needs an External connection string or local only`)
      }
      if (option === 'render-postgres' && f.backendHost !== 'deploy-render') {
        return warn('R8', 'Database on a different platform from the backend: higher latency, public connection')
      }
      if (option === 'railway-postgres' && f.backendHost !== 'deploy-railway') {
        return warn('R8', 'Database on a different platform from the backend: higher latency, public connection')
      }
      return ok
    case 'local':
      return option === 'compose' && f.backend === 'none' && f.data === 'none'
        ? no('R12', 'The Compose dev loop needs a backend or a database')
        : ok
    case 'originModel': {
      const models = originModels(f)
      return models.allowed.includes(option) ? ok : no('R10', models.text)
    }
    default:
      return ok
  }
}

function defaultOf(choice: string, f: Facts, states: OptionState[]): string | null {
  const allowed = (id: string | undefined) => (id ? states.some((s) => s.id === id && s.state !== 'forbidden') : false)
  switch (choice) {
    case 'backendHost':
      return allowed(f.frontendHost) && f.frontendHost !== 'none' ? (f.frontendHost ?? null) : null
    case 'local':
      return allowed('compose') ? 'compose' : 'none'
    case 'originModel':
      return originModels(f).default
    default:
      return null
  }
}

/** The selection of a request, one option id per choice ('' when not chosen). */
export function selectionOfRequest(r: Request): Record<string, string> {
  return {
    frontend: r.frontend?.pack ?? '',
    backend: r.backend?.pack ?? '',
    data: r.data?.pack ?? '',
    vcs: r.vcs?.pack ?? '',
    frontendHost: r.deploy?.frontendHost ?? '',
    backendHost: r.deploy?.backendHost ?? '',
    databaseHost: r.deploy?.databaseHost ?? '',
    local: r.deploy?.local ?? '',
    originModel: r.originModel ?? '',
  }
}

const recommended = (choice: string, option: string) =>
  stackCatalog.recommendations.some((r) => r.choices[choice]?.includes(option) ?? false)

/** §4.17 `Evaluate`: every choice's option states in order; a selection earlier choices now forbid is `invalidated`. */
export function evaluate(selection: Record<string, string>): Schemas['StackOptionsDto'] {
  const facts: Facts = {}
  const choices: ChoiceState[] = []
  for (const choice of stackCatalog.choices) {
    const selected = selection[choice.id] ?? ''
    const options: OptionState[] = choice.options.map((o) => {
      const v = verdict(choice.id, o.id, facts)
      const state = o.id === selected && v.state === 'forbidden' ? 'invalidated' : v.state
      return { id: o.id, state, reasons: v.reasons, recommended: recommended(choice.id, o.id) }
    })
    const selectedState = options.find((o) => o.id === selected)
    if (selected && selectedState && selectedState.state !== 'invalidated') {
      facts[choice.id] = selected
    }
    choices.push({
      choice: choice.id,
      layer: choice.layer,
      selected: selected || null,
      default: defaultOf(choice.id, facts, options),
      options,
    })
  }
  const nextjsMode = facts.frontend === 'fe-nextjs' ? (facts.backend === 'none' ? 'full-stack' : facts.backend ? 'ui' : null) : null
  return { catalogVersion: STACK_CATALOG_VERSION, choices, nextjsMode }
}

export type ValidateResult =
  | { ok: true; composition: Composition }
  | { ok: false; violations: Schemas['HttpValidationProblemDetails']['violations'] }
  | { ok: false; errors: Record<string, string[]> }

/** `value`, or `fallback` when it is empty or missing. */
const orDefault = (value: string | undefined, fallback: string) => (value === undefined || value === '' ? fallback : value)

/** FR-116, FR-119 `Validate`: left-out layers are None, vcs is required, paths must differ and stay in the repo. */
export function validateComposition(request: Request): ValidateResult {
  const sel = selectionOfRequest(request)
  if (!sel.vcs) {
    return { ok: false, errors: { 'vcs.pack': ['is required.'] } }
  }
  for (const c of ['frontend', 'backend', 'data', 'frontendHost', 'backendHost', 'databaseHost'] as const) {
    sel[c] = orDefault(sel[c], 'none')
  }
  const errors: Record<string, string[]> = {}
  const frontendPath = request.frontend?.path ?? 'frontend'
  const backendPath = request.backend?.path ?? 'backend'
  for (const [layer, path] of [['frontend', frontendPath], ['backend', backendPath]] as const) {
    if (path.split('/').includes('..') || path.startsWith('/')) {
      errors[`${layer}.path`] = ["must be relative and stay inside the repository (no '..')."]
    }
  }
  if (sel.frontend !== 'none' && sel.backend !== 'none' && frontendPath === backendPath) {
    errors['backend.path'] = [`must differ from the frontend path ('${frontendPath}').`]
  }
  if (Object.keys(errors).length > 0) {
    return { ok: false, errors }
  }
  let evaluation = evaluate(sel)
  const defaults = Object.fromEntries(evaluation.choices.map((c) => [c.choice, c.default]))
  sel.local = orDefault(sel.local, defaults.local ?? 'none')
  sel.originModel = orDefault(sel.originModel, defaults.originModel ?? '')
  evaluation = evaluate(sel)
  const violations = evaluation.choices.flatMap((c) =>
    c.options
      .filter((o) => o.id === sel[c.choice] && (o.state === 'invalidated' || o.state === 'forbidden'))
      .map((o) => ({ layer: c.choice, option: o.id, rule: o.reasons[0]?.rule ?? 'R0', reason: o.reasons[0]?.text ?? '' })),
  )
  if (violations.length > 0) {
    return { ok: false, violations }
  }
  const caveats = evaluation.choices.flatMap((c) =>
    c.options
      .filter((o) => o.id === sel[c.choice] && o.state === 'caveat')
      .map((o) => ({ layer: c.choice, option: o.id, rule: o.reasons[0]?.rule ?? '', text: o.reasons[0]?.text ?? '' })),
  )
  const orNull = (v: string | undefined) => (!v || v === 'none' ? null : v)
  const agents = [
    'scaffolder',
    ...(orNull(sel.data) ? ['data-engineer'] : []),
    ...(orNull(sel.backend) ? ['backend-engineer'] : []),
    ...(orNull(sel.frontend) ? ['frontend-engineer'] : []),
    'test-engineer',
    'ci-engineer',
    ...(orNull(sel.frontendHost) || orNull(sel.backendHost) || sel.local === 'compose' ? ['deploy-engineer'] : []),
    'docs-writer',
  ]
  return {
    ok: true,
    composition: {
      catalogVersion: STACK_CATALOG_VERSION,
      catalogHash: STACK_CATALOG_HASH,
      frontend: orNull(sel.frontend) ? { pack: sel.frontend ?? '', path: frontendPath } : null,
      backend: orNull(sel.backend) ? { pack: sel.backend ?? '', path: backendPath } : null,
      data: orNull(sel.data) ? { pack: sel.data ?? '', mode: sel.data === 'data-supabase' ? 'baas' : null } : null,
      vcs: { pack: sel.vcs, instanceId: null, selfManaged: false },
      deploy: {
        frontendHost: orNull(sel.frontendHost),
        backendHost: orNull(sel.backendHost),
        databaseHost: orNull(sel.databaseHost),
        local: sel.local || 'none',
      },
      originModel: sel.frontend === 'none' ? null : sel.originModel || null,
      derived: {
        nextjsMode: evaluation.nextjsMode,
        agents,
        originModelOptions: originModels(sel).allowed,
        specSections: [],
        envNames: [],
        gates: [],
      },
      caveats,
    },
  }
}

/** §5 `stack_packs`: the pack ids of a composition. */
export function packsOf(c: Composition): string[] {
  const optionPack = (choice: string, option: string | null | undefined) =>
    option ? (stackCatalog.choices.find((ch) => ch.id === choice)?.options.find((o) => o.id === option)?.pack ?? null) : null
  return [
    optionPack('frontend', c.frontend?.pack),
    optionPack('backend', c.backend?.pack),
    optionPack('data', c.data?.pack),
    optionPack('vcs', c.vcs.pack),
    optionPack('frontendHost', c.deploy.frontendHost),
    optionPack('backendHost', c.deploy.backendHost),
    optionPack('databaseHost', c.deploy.databaseHost),
    optionPack('local', c.deploy.local),
  ].filter((p, i, all): p is string => p !== null && all.indexOf(p) === i)
}

/** A valid composition for seeds: React + Vite · ASP.NET Core · EF Core + PostgreSQL · GitHub. */
export function sampleComposition(overrides: Request = {}): Composition {
  const result = validateComposition({
    frontend: { pack: 'fe-react-vite', path: 'frontend' },
    backend: { pack: 'be-aspnet', path: 'backend' },
    data: { pack: 'data-efcore-postgres' },
    vcs: { pack: 'vcs-github' },
    deploy: { frontendHost: 'deploy-render', backendHost: 'deploy-render', databaseHost: 'render-postgres', local: 'compose' },
    ...overrides,
  })
  if (!result.ok) {
    throw new Error('sampleComposition: invalid composition')
  }
  return result.composition
}
