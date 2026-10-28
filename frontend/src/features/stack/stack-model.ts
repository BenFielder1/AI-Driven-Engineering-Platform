import type {
  StackCatalog,
  StackCatalogOption,
  StackChoiceState,
  StackComposition,
  StackCompositionRequest,
  StackDetection,
  StackOptions,
} from './stack-api'

/**
 * The Stack step's state (FR-114, FR-115, FR-119): one option id per choice ('' = not chosen yet) and the layer paths.
 * The wizard keeps no compatibility logic of its own (D-V4-19): which options are allowed, forbidden or invalidated
 * always comes from `POST /api/stack/options`. This module only shapes requests and reacts to the server's states.
 */
export interface StackSelection {
  frontend: string
  backend: string
  data: string
  vcs: string
  frontendHost: string
  backendHost: string
  databaseHost: string
  local: string
  originModel: string
  frontendPath: string
  backendPath: string
}

/** The choice ids (§4.17 evaluation order); the catalog lists its choices in this order too. */
export const stackChoiceIds = [
  'frontend',
  'backend',
  'data',
  'vcs',
  'frontendHost',
  'backendHost',
  'databaseHost',
  'local',
  'originModel',
] as const
export type StackChoiceId = (typeof stackChoiceIds)[number]

export const isStackChoiceId = (value: string): value is StackChoiceId =>
  (stackChoiceIds as readonly string[]).includes(value)

/** The choices that must be made before the Deployment sub-step (None is an explicit choice). */
export const requiredChoices: readonly StackChoiceId[] = ['frontend', 'backend', 'data', 'vcs']

/** FR-119: the stack-packs STACK template defaults. */
export const DEFAULT_FRONTEND_PATH = 'frontend'
export const DEFAULT_BACKEND_PATH = 'backend'
export const NONE = 'none'

export const emptyStackSelection: StackSelection = {
  frontend: '',
  backend: '',
  data: '',
  vcs: '',
  frontendHost: '',
  backendHost: '',
  databaseHost: '',
  local: '',
  originModel: '',
  frontendPath: DEFAULT_FRONTEND_PATH,
  backendPath: DEFAULT_BACKEND_PATH,
}

/** The five sub-steps (the catalog's layers, in wizard order) when the catalog hasn't loaded yet. */
export const stackLayerIds = ['frontend', 'backend', 'data', 'vcs', 'deploy'] as const

/** Display names of the catalog layers (activity summaries, Settings → Stack). */
export const stackLayerLabels: Readonly<Record<string, string>> = {
  frontend: 'Frontend',
  backend: 'Backend',
  data: 'Data',
  vcs: 'VCS',
  deploy: 'Deployment',
}

const chosen = (value: string) => (value === '' ? undefined : value)

function component(pack: string, path: string, withPath: boolean) {
  return withPath && pack !== NONE && path.trim() ? { pack, path: path.trim() } : { pack }
}

/**
 * The request for `POST /api/stack/options` / `/validate` / create / PUT: chosen layers only (a missing layer is "not
 * chosen yet" for `/options`). `withPaths` adds the layer paths (they don't change option states, so `/options` is
 * sent without them). `omitVcs`: the server sets the VCS from the repository (R5).
 */
export function toStackRequest(
  sel: StackSelection,
  { withPaths = true, omitVcs = false }: { withPaths?: boolean; omitVcs?: boolean } = {},
): StackCompositionRequest {
  const deploy = {
    ...(chosen(sel.frontendHost) ? { frontendHost: sel.frontendHost } : {}),
    ...(chosen(sel.backendHost) ? { backendHost: sel.backendHost } : {}),
    ...(chosen(sel.databaseHost) ? { databaseHost: sel.databaseHost } : {}),
    ...(chosen(sel.local) ? { local: sel.local } : {}),
  }
  return {
    ...(chosen(sel.frontend) ? { frontend: component(sel.frontend, sel.frontendPath, withPaths) } : {}),
    ...(chosen(sel.backend) ? { backend: component(sel.backend, sel.backendPath, withPaths) } : {}),
    ...(chosen(sel.data) ? { data: { pack: sel.data } } : {}),
    ...(chosen(sel.vcs) && !omitVcs ? { vcs: { pack: sel.vcs } } : {}),
    ...(Object.keys(deploy).length > 0 ? { deploy } : {}),
    ...(chosen(sel.originModel) ? { originModel: sel.originModel } : {}),
  }
}

/** Whether every choice before the Deployment sub-step is made (then `/validate` can run). */
export const isComplete = (sel: StackSelection) => requiredChoices.every((c) => sel[c] !== '')

/** The option card of a choice in the catalog. */
export function catalogOption(catalog: StackCatalog | undefined, choice: string, option: string): StackCatalogOption | undefined {
  return catalog?.choices.find((c) => c.id === choice)?.options.find((o) => o.id === option)
}

/** An option's name ("SQLAlchemy"), falling back to its id. */
export const optionName = (catalog: StackCatalog | undefined, choice: string, option: string) =>
  catalogOption(catalog, choice, option)?.name ?? option

/**
 * FR-114 toast: "SQLAlchemy was cleared: it needs FastAPI". The server's reason usually starts with the option's
 * name ("SQLAlchemy needs FastAPI"), which reads as "it" after "was cleared".
 */
export function clearedMessage(name: string, reason: string | undefined): string {
  const text = (reason ?? '').trim().replace(/\.$/, '')
  if (!text) {
    return `${name} was cleared: it isn't available with your choices`
  }
  const rest = text.startsWith(`${name} `) ? `it ${text.slice(name.length + 1)}` : text.charAt(0).toLowerCase() + text.slice(1)
  return `${name} was cleared: ${rest}`
}

export interface ClearedChoice {
  choice: StackChoiceId
  option: string
  message: string
}

/**
 * Reacts to an `/options` answer: a selection the server reports as `invalidated` (or `forbidden`) is cleared, with a
 * message naming it and the server's reason (FR-114). The origin model isn't announced: it is re-derived. Deployment
 * choices that are still empty take the server's `default` when `fillDefaults` (the user is on the Deployment
 * sub-step). Returns the same object when nothing changes.
 */
export function applyOptions(
  sel: StackSelection,
  options: StackOptions,
  catalog: StackCatalog | undefined,
  { fillDefaults = false }: { fillDefaults?: boolean } = {},
): { selection: StackSelection; cleared: ClearedChoice[] } {
  let next = sel
  const cleared: ClearedChoice[] = []
  for (const state of options.choices) {
    if (!isStackChoiceId(state.choice)) {
      continue
    }
    const choice = state.choice
    const current = next[choice]
    const option = current ? state.options.find((o) => o.id === current) : undefined
    if (current && (!option || option.state === 'invalidated' || option.state === 'forbidden')) {
      next = { ...next, [choice]: '' }
      if (choice !== 'originModel' && choice !== 'local') {
        cleared.push({ choice, option: current, message: clearedMessage(optionName(catalog, choice, current), option?.reasons[0]?.text) })
      }
    }
    if (fillDefaults && state.layer === 'deploy' && next[choice] === '' && state.default) {
      const fallback = state.options.find((o) => o.id === state.default)
      if (fallback && (fallback.state === 'allowed' || fallback.state === 'caveat')) {
        next = { ...next, [choice]: state.default }
      }
    }
  }
  return { selection: next, cleared }
}

/** Options a user can pick (allowed or caveat) and those shown in "Not available with your choices". */
export function splitOptions(state: StackChoiceState) {
  const available = state.options.filter((o) => o.state === 'allowed' || o.state === 'caveat')
  const unavailable = state.options.filter((o) => o.state === 'forbidden' || o.state === 'invalidated')
  return { available, unavailable }
}

/** The selection of a pinned composition (Settings → Stack "Change stack", the start-version dialog). */
export function selectionFromComposition(composition: StackComposition | null | undefined): StackSelection {
  if (!composition) {
    return emptyStackSelection
  }
  return {
    frontend: composition.frontend?.pack ?? NONE,
    backend: composition.backend?.pack ?? NONE,
    data: composition.data?.pack ?? NONE,
    vcs: composition.vcs.pack,
    frontendHost: composition.deploy.frontendHost ?? NONE,
    backendHost: composition.deploy.backendHost ?? NONE,
    databaseHost: composition.deploy.databaseHost ?? NONE,
    local: composition.deploy.local,
    originModel: composition.originModel ?? '',
    frontendPath: composition.frontend?.path ?? DEFAULT_FRONTEND_PATH,
    backendPath: composition.backend?.path ?? DEFAULT_BACKEND_PATH,
  }
}

/** FR-118: the detected choices (and paths) over an empty selection; conflicts and undetected choices stay empty. */
export function selectionFromDetection(detection: StackDetection, base: StackSelection): StackSelection {
  let next = { ...base }
  for (const s of detection.suggestions) {
    if (!isStackChoiceId(s.choice)) {
      continue
    }
    next = { ...next, [s.choice]: s.option }
    if (s.choice === 'frontend' && s.path) {
      next.frontendPath = s.path
    }
    if (s.choice === 'backend' && s.path) {
      next.backendPath = s.path
    }
  }
  return next
}

/** Whether detection found any layer worth composing (not just the VCS, which the repository fixes anyway). */
export const detectedAnything = (detection: StackDetection) =>
  detection.suggestions.some((s) => s.choice !== 'vcs') || detection.conflicts.length > 0

/**
 * Server VALIDATION keys of a composition (`frontend.path`, `stack.backend.pack`, `stack.deploy.frontendHost`…) →
 * selection fields; `undefined` for keys that aren't about the stack.
 */
export function stackFieldOfKey(key: string): keyof StackSelection | undefined {
  const k = key.toLowerCase().replace(/^stack\./, '')
  if (k === 'frontend.path') {
    return 'frontendPath'
  }
  if (k === 'backend.path') {
    return 'backendPath'
  }
  const [head = '', second = ''] = k.split('.')
  const choice = head === 'deploy' ? second : head
  return stackChoiceIds.find((c) => c.toLowerCase() === choice)
}
