import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { lineDiff } from './artifacts'
import { currentUser, unauthenticated } from './auth'
import { pipelineDefinitions } from './pipeline-definitions'
import { pipelinesDb, type StoredPipelineRun } from './pipeline-store'
import { problem } from './problem'
import { access } from './projects'

type Item = Schemas['PipelineTimelineItemDto']

/** Mirrors the engine API's replay cap (D-0.7.0-35: 200,000 characters per input and output). */
export const maxReplayTextLength = 200_000

const notFound = () => problem(404, 'NOT_FOUND', 'The pipeline run was not found.')

/** The run, if the caller can see its project (404 otherwise, as the API hides which). */
function runAccess(runId: unknown): StoredPipelineRun | Response {
  if (!currentUser()) {
    return unauthenticated()
  }
  const stored = pipelinesDb.runs.find((r) => r.run.id === runId)
  if (!stored) {
    return notFound()
  }
  const a = access(stored.run.projectId)
  return a instanceof Response ? notFound() : stored
}

/** Timeline items without the replay-only fields (GET /timeline). */
function withoutReplay(item: Item): Item {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const { input, inputTruncated, output, outputTruncated, ...rest } = item
  return rest
}

function cap(text: string | null | undefined): [string | null, boolean] {
  if (text == null) {
    return [null, false]
  }
  return text.length > maxReplayTextLength ? [text.slice(0, maxReplayTextLength), true] : [text, false]
}

/** Cursor paging as the API does it (limit 1–200, default 50; the cursor here is the next index). */
function page(request: Request, items: readonly Item[], map: (item: Item) => Item): Response {
  const q = new URL(request.url).searchParams
  const limit = q.get('limit') === null ? 50 : Number(q.get('limit'))
  const start = q.get('cursor') === null ? 0 : Number(q.get('cursor'))
  if (!Number.isInteger(limit) || limit < 1 || limit > 200 || !Number.isInteger(start) || start < 0) {
    return problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors: { limit: ['limit must be between 1 and 200.'] } })
  }
  const slice = items.slice(start, start + limit)
  return HttpResponse.json<Schemas['PipelineTimelinePageDto']>({
    items: slice.map(map),
    next: start + limit < items.length ? String(start + limit) : null,
  })
}

export const pipelineHandlers = [
  // FR-299: registered pipelines (signed in).
  http.get('*/api/pipelines', () =>
    currentUser()
      ? HttpResponse.json<Schemas['PipelineSummaryDto'][]>(
          pipelineDefinitions.map((d) => ({ id: d.pipelineId, title: d.title, version: d.version, hash: d.hash, packId: d.packId })),
        )
      : unauthenticated(),
  ),
  http.get('*/api/pipelines/:pipelineId/definitions/:hash', ({ params }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const def = pipelineDefinitions.find((d) => d.pipelineId === params.pipelineId && d.hash === params.hash)
    return def ? HttpResponse.json<Schemas['PipelineDefinitionDto']>(def) : problem(404, 'NOT_FOUND', 'The pipeline definition was not found.')
  }),
  // FR-298: prompt comparison over one project (project viewer).
  http.get('*/api/pipelines/:pipelineId/prompt-stats', ({ request, params }) => {
    const projectId = new URL(request.url).searchParams.get('projectId')
    const a = access(projectId)
    if (a instanceof Response) {
      return a
    }
    if (!pipelineDefinitions.some((d) => d.pipelineId === params.pipelineId)) {
      return problem(404, 'NOT_FOUND', 'The pipeline was not found.')
    }
    return HttpResponse.json<Schemas['PromptStatsDto'][]>(
      pipelinesDb.promptStats
        .filter((s) => s.pipelineId === params.pipelineId && s.projectId === projectId)
        // eslint-disable-next-line @typescript-eslint/no-unused-vars
        .map(({ pipelineId, projectId: _p, ...row }) => row),
    )
  }),
  // FR-300: the project's runs, newest first.
  http.get('*/api/projects/:id/pipeline-runs', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    return HttpResponse.json<Schemas['PipelineRunDto'][]>(
      pipelinesDb.runs
        .filter((r) => r.run.projectId === params.id)
        .map((r) => r.run)
        .sort((x, y) => y.startedAt.localeCompare(x.startedAt)),
    )
  }),
  http.get('*/api/pipeline-runs/:runId', ({ params }) => {
    const r = runAccess(params.runId)
    return r instanceof Response ? r : HttpResponse.json<Schemas['PipelineRunDto']>(r.run)
  }),
  http.get('*/api/pipeline-runs/:runId/stages', ({ params }) => {
    const r = runAccess(params.runId)
    return r instanceof Response ? r : HttpResponse.json<Schemas['PipelineStageDto'][]>(r.stages)
  }),
  http.get('*/api/pipeline-runs/:runId/timeline', ({ request, params }) => {
    const r = runAccess(params.runId)
    return r instanceof Response ? r : page(request, r.timeline, withoutReplay)
  }),
  // FR-303: the timeline with agent input and output, capped.
  http.get('*/api/pipeline-runs/:runId/replay', ({ request, params }) => {
    const r = runAccess(params.runId)
    if (r instanceof Response) {
      return r
    }
    return page(request, r.timeline, (item) => {
      if (item.kind !== 'agent_run') {
        return withoutReplay(item)
      }
      const [input, inputCut] = cap(item.input)
      const [output, outputCut] = cap(item.output)
      return { ...item, input, inputTruncated: inputCut || item.inputTruncated === true, output, outputTruncated: outputCut || item.outputTruncated === true }
    })
  }),
  // FR-301: artifacts by key, versions newest first; a version; a diff; the gates.
  http.get('*/api/pipeline-runs/:runId/artifacts', ({ params }) => {
    const r = runAccess(params.runId)
    if (r instanceof Response) {
      return r
    }
    const def = pipelineDefinitions.find((d) => d.hash === r.run.definitionHash)
    return HttpResponse.json<Schemas['PipelineArtifactDto'][]>(
      [...r.artifacts].map(([key, versions]) => ({
        key,
        title: def?.artifacts.find((a) => a.key === key)?.title ?? key,
        versions: versions.map((v) => v.summary).reverse(),
      })),
    )
  }),
  http.get('*/api/pipeline-runs/:runId/artifacts/:key/versions/:version', ({ params }) => {
    const r = runAccess(params.runId)
    if (r instanceof Response) {
      return r
    }
    const key = String(params.key)
    const v = r.artifacts.get(key)?.find((x) => x.summary.version === Number(params.version))
    if (!v) {
      return problem(404, 'NOT_FOUND', 'The artifact version was not found.')
    }
    const def = pipelineDefinitions.find((d) => d.hash === r.run.definitionHash)
    return HttpResponse.json<Schemas['PipelineArtifactVersionDto']>({
      id: v.summary.id,
      key,
      title: def?.artifacts.find((a) => a.key === key)?.title ?? key,
      version: v.summary.version,
      parentVersion: v.summary.version > 1 ? v.summary.version - 1 : null,
      status: v.summary.status,
      hash: v.summary.hash,
      intact: v.intact,
      content: v.content,
      agentRunId: null,
      createdAt: v.summary.createdAt,
      approvedAt: null,
    })
  }),
  http.get('*/api/pipeline-runs/:runId/artifacts/:key/diff', ({ request, params }) => {
    const r = runAccess(params.runId)
    if (r instanceof Response) {
      return r
    }
    const q = new URL(request.url).searchParams
    const from = Number(q.get('from'))
    const to = Number(q.get('to'))
    const key = String(params.key)
    const versions = r.artifacts.get(key) ?? []
    const left = versions.find((v) => v.summary.version === from)
    const right = versions.find((v) => v.summary.version === to)
    if (!left || !right) {
      return problem(404, 'NOT_FOUND', 'The artifact version was not found.')
    }
    return HttpResponse.json<Schemas['PipelineArtifactDiffDto']>({ key, from, to, unifiedDiff: '', lines: lineDiff(left.content, right.content) })
  }),
  http.get('*/api/pipeline-runs/:runId/gates', ({ params }) => {
    const r = runAccess(params.runId)
    return r instanceof Response ? r : HttpResponse.json<Schemas['PipelineGateDto'][]>(r.gates)
  }),
]
