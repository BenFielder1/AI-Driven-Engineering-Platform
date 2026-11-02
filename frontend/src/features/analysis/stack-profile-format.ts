import type { Schemas } from '../../api/client'
import type { StackCompositionRequest } from '../stack/stack-api'
import { NONE, type StackSelection } from '../stack/stack-model'

export type StackProfileDocument = Schemas['StackProfileDocumentDto']
export type StackProfileLayer = Schemas['StackProfileLayerDto']
export type StackProfileHosts = Schemas['StackProfileHostsDto']
export type StackProfileLayerStatus = Schemas['StackProfileLayerStatus']
export type StackProfileBlank = Schemas['StackProfileBlankDto']

/** §4.27: the four layers of a profile, in layer order (the Deployment layer is `hosts`). */
export const profileLayers = ['frontend', 'backend', 'data', 'vcs'] as const
export type ProfileLayer = (typeof profileLayers)[number]

/** The layers a person decides (VCS is fixed by the repository's host). */
export const editableLayers = ['frontend', 'backend', 'data'] as const satisfies readonly ProfileLayer[]
export type EditableLayer = (typeof editableLayers)[number]

/** FR-184: the host choices of a profile, in catalog order. */
export const hostChoices = ['frontendHost', 'backendHost', 'databaseHost'] as const
export type HostChoice = (typeof hostChoices)[number]

/** The fence tag of the profile block (backend `StackProfile.FenceTag`). */
export const STACK_PROFILE_FENCE = 'aide-stack-profile'

export const isProfileLayer = (value: string): value is ProfileLayer => (profileLayers as readonly string[]).includes(value)

export const profileLayerLabels: Record<ProfileLayer, string> = {
  frontend: 'Frontend',
  backend: 'Backend',
  data: 'Data',
  vcs: 'Version control',
}

/** "Backend", or the layer id itself for one the UI doesn't know (e.g. `hosts`). */
export const layerLabel = (layer: string) => (isProfileLayer(layer) ? profileLayerLabels[layer] : layer === 'hosts' ? 'Hosts' : layer)

/** §4.27 layer statuses, in words. */
export const layerStatusLabels: Record<StackProfileLayerStatus, string> = {
  inferred: 'Inferred',
  user: 'Set by a person',
  none: 'Not in the repository',
  fixed: 'Fixed by the repository host',
  unknown: 'Unknown',
  conflict: 'Conflicting evidence',
  unsupported: 'Not supported',
}

export const confidenceLabels: Record<NonNullable<Schemas['StackProfileConfidence']>, string> = {
  high: 'High confidence',
  medium: 'Medium confidence',
  low: 'Low confidence',
}

/** §4.27 `structure` roles, in words. */
export const structureRoleLabels: Readonly<Record<string, string>> = {
  frontend: 'Frontend',
  backend: 'Backend',
  data: 'Data',
  migrations: 'Migrations',
  tests: 'Tests',
  infra: 'Infrastructure',
  ci: 'CI',
  docs: 'Docs',
  other: 'Other',
}

/** §4.27: `unknown` and `conflict` always block approval; `unsupported` does unless the profile chose No stack (generalist). */
export function blocksApproval(status: StackProfileLayerStatus, generalist: boolean): boolean {
  return status === 'unknown' || status === 'conflict' || (status === 'unsupported' && !generalist)
}

/** FR-181: the layers that block approval, in layer order (mirrors `StackProfile.Unresolved`). */
export function unresolvedLayers(doc: StackProfileDocument): ProfileLayer[] {
  return profileLayers.filter((l) => blocksApproval(doc.layers[l].status, doc.generalist))
}

/** FR-184: the hosts can be chosen once the frontend, backend and data layers are resolved. */
export const hostsReady = (doc: StackProfileDocument) =>
  editableLayers.every((l) => !blocksApproval(doc.layers[l].status, false))

/** FR-181: whether any layer is outside the catalog (the page then offers "Continue with No stack (general)"). */
export const hasUnsupported = (doc: StackProfileDocument) => profileLayers.some((l) => doc.layers[l].status === 'unsupported')

// ---- Markdown: narrative + fenced block (backend StackProfile.Parse / Render) ----------------------------------------

/**
 * §4.27, FR-183: the Markdown of a profile the editor saves: the narrative (trimmed; left out when blank), a blank line,
 * then the fenced `aide-stack-profile` block holding the document's JSON. `parseStackProfile` gives the same back.
 */
export function renderStackProfile(narrative: string | null | undefined, doc: StackProfileDocument): string {
  const text = (narrative ?? '').replace(/\r\n?/g, '\n').trim()
  const block = `\`\`\`${STACK_PROFILE_FENCE}\n${JSON.stringify(doc, null, 2)}\n\`\`\`\n`
  return text ? `${text}\n\n${block}` : block
}

const isOpeningFence = (line: string) => {
  const t = line.trim()
  return t.startsWith('```') && t.slice(3).trim() === STACK_PROFILE_FENCE
}

/**
 * The narrative and document of a `stack_profile`'s Markdown, or `null` when there isn't exactly one closed
 * `aide-stack-profile` block holding a JSON object. Shape checks are the server's (it validates every save).
 */
export function parseStackProfile(markdown: string): { narrative: string; document: StackProfileDocument } | null {
  const lines = markdown.replace(/\r\n?/g, '\n').split('\n')
  const opens = lines.flatMap((line, i) => (isOpeningFence(line) ? [i] : []))
  const open = opens[0]
  if (opens.length !== 1 || open === undefined) {
    return null
  }
  const close = lines.findIndex((line, i) => i > open && line.trim() === '```')
  if (close < 0) {
    return null
  }
  let parsed: unknown
  try {
    parsed = JSON.parse(lines.slice(open + 1, close).join('\n'))
  } catch {
    return null
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return null
  }
  const before = lines.slice(0, open).join('\n').trim()
  const after = lines.slice(close + 1).join('\n').trim()
  const narrative = after === '' ? before : before === '' ? after : `${before}\n\n${after}`
  return { narrative, document: parsed as StackProfileDocument }
}

// ---- Selections: the profile as a `/api/stack/options` request (backend StackProfileMapping.ToRequest) --------------

/** A frontend or backend layer as a selection: `none` for a layer that isn't there, '' while undecided. */
const layerOption = (layer: StackProfileLayer) => (layer.status === 'none' ? NONE : (layer.option ?? ''))

/**
 * The profile as a `StackSelection` (stack-model): undecided layers are '' (not chosen yet), hosts `null` are None, the
 * local dev loop and origin model '' (the server's default) when unset.
 */
export function selectionOfProfile(doc: StackProfileDocument): StackSelection {
  const { frontend, backend, data, vcs } = doc.layers
  return {
    frontend: layerOption(frontend),
    backend: layerOption(backend),
    data: layerOption(data),
    vcs: vcs.option ?? '',
    frontendHost: doc.hosts.frontendHost ?? NONE,
    backendHost: doc.hosts.backendHost ?? NONE,
    databaseHost: doc.hosts.databaseHost ?? NONE,
    local: doc.hosts.local ?? '',
    originModel: doc.hosts.originModel ?? '',
    frontendPath: frontend.path ?? '',
    backendPath: backend.path ?? '',
  }
}

/**
 * FR-184: the `POST /api/stack/options` body of a profile (mirrors the server's `selection`): the decided layers, every
 * host (None when unset), the local loop and origin model when set, and the separate parts. Paths are left out: they
 * don't change option states.
 */
export function optionsRequestOfProfile(doc: StackProfileDocument): StackCompositionRequest {
  const sel = selectionOfProfile(doc)
  const pick = (value: string) => (value === '' ? undefined : value)
  const frontend = pick(sel.frontend)
  const backend = pick(sel.backend)
  const data = pick(sel.data)
  const vcs = pick(sel.vcs)
  return {
    ...(frontend ? { frontend: { pack: frontend } } : {}),
    ...(backend ? { backend: { pack: backend } } : {}),
    ...(data ? { data: { pack: data, ...(doc.layers.data.mode ? { mode: doc.layers.data.mode } : {}) } } : {}),
    ...(vcs ? { vcs: { pack: vcs } } : {}),
    deploy: {
      frontendHost: sel.frontendHost,
      backendHost: sel.backendHost,
      databaseHost: sel.databaseHost,
      ...(sel.local ? { local: sel.local } : {}),
      ...(doc.hosts.separateParts.length > 0 ? { separateParts: [...doc.hosts.separateParts] } : {}),
    },
    ...(sel.originModel ? { originModel: sel.originModel } : {}),
  }
}

// ---- Edits (FR-181, FR-183, FR-184): each returns a new document ---------------------------------------------------

/**
 * A person resolves a layer: an option of the catalog (status `user`, its path and the data mode), `none` (no such
 * layer) or '' (left undecided: the layer keeps what the agent found). The agent's question for it goes once answered.
 */
export function withLayerChoice(
  doc: StackProfileDocument,
  layer: EditableLayer,
  option: string,
  { path, mode }: { path?: string | null; mode?: string | null } = {},
): StackProfileDocument {
  const current = doc.layers[layer]
  if (option === '') {
    return doc
  }
  const next: StackProfileLayer =
    option === NONE
      ? { ...current, option: null, path: null, mode: null, status: 'none', technology: null, candidates: [] }
      : {
          ...current,
          option,
          path: layer === 'data' ? null : (path ?? current.path ?? null),
          mode: layer === 'data' ? (mode ?? null) : null,
          status: current.option === option && current.status === 'inferred' ? 'inferred' : 'user',
          technology: null,
          candidates: [],
        }
  return {
    ...doc,
    layers: { ...doc.layers, [layer]: next },
    questions: doc.questions.filter((q) => q.layer !== layer),
  }
}

/** A frontend or backend folder (FR-119 rules are the server's). */
export function withLayerPath(doc: StackProfileDocument, layer: 'frontend' | 'backend', path: string): StackProfileDocument {
  const current = doc.layers[layer]
  return { ...doc, layers: { ...doc.layers, [layer]: { ...current, path } } }
}

/** A host choice: an option id, or `none` (stored as `null`, §4.27). A changed host is no longer "detected". */
export function withHost(doc: StackProfileDocument, choice: HostChoice | 'local' | 'originModel', option: string): StackProfileDocument {
  const value = option === '' || (option === NONE && choice !== 'local') ? null : option
  const changed = doc.hosts[choice] !== value
  const detected = changed ? doc.hosts.detected.filter((d) => d !== choice) : doc.hosts.detected
  return { ...doc, hosts: { ...doc.hosts, [choice]: value, detected } }
}

/**
 * FR-114, FR-184: applies the host choices of a selection the server re-evaluated (stack-model `applyOptions`) back to
 * the profile: hosts it cleared become None.
 */
export function withHostsOf(doc: StackProfileDocument, sel: StackSelection): StackProfileDocument {
  const host = (value: string) => (value === '' || value === NONE ? null : value)
  const hosts: StackProfileHosts = {
    ...doc.hosts,
    frontendHost: host(sel.frontendHost),
    backendHost: host(sel.backendHost),
    databaseHost: host(sel.databaseHost),
    local: sel.local === '' ? null : sel.local,
    originModel: sel.originModel === '' ? null : sel.originModel,
  }
  hosts.detected = doc.hosts.detected.filter((d) => (hostChoices as readonly string[]).includes(d) && hosts[d as HostChoice] !== null)
  return { ...doc, hosts }
}

/**
 * Server VALIDATION keys of a profile save (`layers.backend.option`, `hosts.frontendHost`…) → the editor field they're
 * about, or `null` when the editor has no such field (shown in the summary).
 */
export function profileFieldOfKey(key: string): string | null {
  const k = key.replace(/^content\./i, '')
  const layer = /^layers\.(frontend|backend|data)\.(option|path|mode|status|candidates)$/i.exec(k)
  if (layer?.[1] && layer[2]) {
    const which = layer[1].toLowerCase()
    return layer[2].toLowerCase() === 'path' ? `${which}.path` : which
  }
  const host = /^hosts\.(frontendHost|backendHost|databaseHost|local|originModel)$/i.exec(k)
  if (host?.[1]) {
    const lower = host[1].toLowerCase()
    return [...hostChoices, 'local', 'originModel'].find((c) => c.toLowerCase() === lower) ?? null
  }
  return null
}
