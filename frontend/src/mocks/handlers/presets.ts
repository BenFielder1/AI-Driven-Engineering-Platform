import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { currentUser, unauthenticated } from './auth'
import { addEvent } from './lifecycle'
import { findPreset, presetCatalog, toPresetSummary } from './preset-catalog'
import { problem } from './problem'
import { developmentVersionOf, ownerMutation, projectDto, record } from './projects'

/**
 * FR-74 presets: GET /api/presets (summaries, ordered by id), GET /api/presets/:id (detail) and PATCH
 * /api/projects/:id/preset (Owner; PRESET_NOT_FOUND 400, PRESET_LOCKED 409 once the version's implementation plan
 * has a locked version or nothing is in development).
 */
export const presetHandlers = [
  http.get('*/api/presets', () => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const sorted = [...presetCatalog.presets].sort((a, b) => a.id.localeCompare(b.id))
    return HttpResponse.json<Schemas['PresetSummaryDto'][]>(sorted.map(toPresetSummary))
  }),

  http.get('*/api/presets/:presetId', ({ params }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const preset = findPreset(String(params.presetId))
    return preset
      ? HttpResponse.json<Schemas['PresetDetailDto']>(preset)
      : problem(404, 'NOT_FOUND', 'The preset was not found.')
  }),

  http.patch('*/api/projects/:id/preset', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['UpdateProjectPresetRequest']
    const presetId = body.presetId ?? null
    const preset = presetId === null ? undefined : findPreset(presetId)
    if (presetId !== null && !preset) {
      return problem(400, 'PRESET_NOT_FOUND', `The preset '${presetId}' was not found.`)
    }
    const p = a.project
    const version = developmentVersionOf(p)
    const planLocked = p.artifacts.some(
      (x) => x.type === 'implementation_plan' && x.status === 'locked' && x.releaseVersionId === version?.id,
    )
    if (!version || planLocked) {
      return problem(409, 'PRESET_LOCKED', "The version's implementation plan is locked.")
    }
    const from = { presetId: version.presetId, presetVersion: version.presetVersion }
    version.presetId = preset?.id ?? null
    version.presetVersion = preset?.version ?? null
    version.presetName = preset?.name ?? null
    addEvent(p, {
      type: 'PRESET_CHANGED',
      category: 'project',
      summary: preset ? `Preset changed to ${preset.id}@${preset.version}` : 'Preset removed',
      payload: {
        releaseVersionId: version.id,
        version: version.version,
        fromPresetId: from.presetId,
        fromPresetVersion: from.presetVersion,
        presetId: version.presetId,
        presetVersion: version.presetVersion,
      },
    })
    return HttpResponse.json<Schemas['ProjectDto']>(projectDto(p, a.userId))
  }),
]
