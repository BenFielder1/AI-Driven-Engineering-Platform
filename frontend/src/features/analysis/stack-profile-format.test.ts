import { describe, expect, it } from 'vitest'

import { sampleProfile, unknownBackendProfile, unsupportedBackendProfile } from '../../mocks/handlers/analysis'
import {
  hostsReady,
  optionsRequestOfProfile,
  parseStackProfile,
  profileFieldOfKey,
  renderStackProfile,
  unresolvedLayers,
  withHost,
  withHostsOf,
  withLayerChoice,
  selectionOfProfile,
} from './stack-profile-format'

describe('stack profile format (§4.27, FR-183)', () => {
  it('FR183_render_then_parse_gives_back_the_same_document_and_narrative', () => {
    const doc = unknownBackendProfile()
    const markdown = renderStackProfile('  A Vue app on Supabase.\r\n\r\nNo backend found.  ', doc)
    expect(markdown.startsWith('A Vue app on Supabase.\n\nNo backend found.\n\n```aide-stack-profile\n')).toBe(true)
    expect(markdown.endsWith('\n```\n')).toBe(true)
    const parsed = parseStackProfile(markdown)
    expect(parsed).toEqual({ narrative: 'A Vue app on Supabase.\n\nNo backend found.', document: doc })
    // Rendering the parsed profile again gives the same text.
    expect(renderStackProfile(parsed?.narrative, parsed?.document ?? doc)).toBe(markdown)
  })

  it('FR183_a_blank_narrative_renders_only_the_block', () => {
    const doc = sampleProfile()
    const markdown = renderStackProfile('   ', doc)
    expect(markdown.startsWith('```aide-stack-profile\n')).toBe(true)
    expect(parseStackProfile(markdown)).toEqual({ narrative: '', document: doc })
  })

  it('FR183_parse_needs_exactly_one_closed_block_holding_a_json_object', () => {
    const block = '```aide-stack-profile\n{"schemaVersion": 1}\n```'
    expect(parseStackProfile('no block')).toBeNull()
    expect(parseStackProfile(`${block}\n\n${block}`)).toBeNull()
    expect(parseStackProfile('```aide-stack-profile\n{"schemaVersion": 1}')).toBeNull()
    expect(parseStackProfile('```aide-stack-profile\nnot json\n```')).toBeNull()
    expect(parseStackProfile('```aide-stack-profile\n[1, 2]\n```')).toBeNull()
    expect(parseStackProfile(`Before.\n\n${block}\n\nAfter.`)?.narrative).toBe('Before.\n\nAfter.')
  })

  it('FR181_unknown_conflict_and_unsupported_block_approval_unless_generalist_for_unsupported', () => {
    expect(unresolvedLayers(sampleProfile())).toEqual([])
    // §4.27 worked example 2: Backend unknown.
    expect(unresolvedLayers(unknownBackendProfile())).toEqual(['backend'])
    // Worked example 4: unsupported with no generalist choice; example 5: generalist.
    expect(unresolvedLayers(unsupportedBackendProfile())).toEqual(['backend'])
    expect(unresolvedLayers({ ...unsupportedBackendProfile(), generalist: true })).toEqual([])
    expect(hostsReady(unknownBackendProfile())).toBe(false)
    expect(hostsReady(sampleProfile())).toBe(true)
  })

  it('FR184_options_request_mirrors_the_server_selection', () => {
    expect(optionsRequestOfProfile(sampleProfile())).toEqual({
      frontend: { pack: 'fe-vue' },
      backend: { pack: 'none' },
      data: { pack: 'data-supabase', mode: 'baas' },
      vcs: { pack: 'vcs-github' },
      deploy: { frontendHost: 'none', backendHost: 'none', databaseHost: 'supabase', local: 'none' },
      originModel: 'same-origin',
    })
    // An undecided layer is "not chosen yet".
    expect(optionsRequestOfProfile(unknownBackendProfile()).backend).toBeUndefined()
  })

  it('FR181_answering_a_layer_sets_status_user_and_drops_its_question', () => {
    const answered = withLayerChoice(unknownBackendProfile(), 'backend', 'be-express', { path: 'server' })
    expect(answered.layers.backend).toMatchObject({ option: 'be-express', path: 'server', status: 'user', candidates: [] })
    expect(answered.questions).toEqual([])
    expect(unresolvedLayers(answered)).toEqual([])
    const none = withLayerChoice(unknownBackendProfile(), 'backend', 'none')
    expect(none.layers.backend).toMatchObject({ option: null, path: null, status: 'none' })
    // Confirming the agent's own option keeps it inferred.
    expect(withLayerChoice(sampleProfile(), 'frontend', 'fe-vue').layers.frontend.status).toBe('inferred')
  })

  it('FR184_hosts_none_is_null_and_a_changed_host_is_no_longer_detected', () => {
    const doc = sampleProfile()
    const changed = withHost(doc, 'databaseHost', 'none')
    expect(changed.hosts.databaseHost).toBeNull()
    expect(changed.hosts.detected).toEqual([])
    expect(withHost(doc, 'frontendHost', 'deploy-vercel').hosts).toMatchObject({ frontendHost: 'deploy-vercel', detected: ['databaseHost'] })
    const cleared = withHostsOf(doc, { ...selectionOfProfile(doc), databaseHost: '' })
    expect(cleared.hosts.databaseHost).toBeNull()
    expect(cleared.hosts.detected).toEqual([])
  })

  it('FR183_server_field_keys_map_to_editor_fields', () => {
    expect(profileFieldOfKey('layers.backend.option')).toBe('backend')
    expect(profileFieldOfKey('layers.frontend.path')).toBe('frontend.path')
    expect(profileFieldOfKey('hosts.frontendHost')).toBe('frontendHost')
    expect(profileFieldOfKey('structure[0].path')).toBeNull()
  })
})
