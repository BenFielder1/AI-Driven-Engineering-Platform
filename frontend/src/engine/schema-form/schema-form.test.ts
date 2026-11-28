import { describe, expect, it } from 'vitest'

import { sessionLogSchema } from '../../mocks/handlers/engine-run-control'
import { flatFields, isBlank, toJsonValue, zodForFields } from './flat-fields'
import { asSchema, describeError, validateJson, type JsonSchema } from './json-schema'
import { schemaOutline, schemaSkeleton } from './schema-outline'

const sessionLog: JsonSchema = asSchema(sessionLogSchema) ?? {}

describe('FR-362: the input schema subset', () => {
  it('FR362_validator_passes_a_valid_session_log', () => {
    expect(validateJson(sessionLog, { sessions: [{ date: '2026-11-02', type: 'tempo', intensity: 'hard', rpe: 7 }] })).toEqual([])
  })

  it('FR362_validator_reports_paths_for_each_failure', () => {
    const errors = validateJson(sessionLog, { sessions: [{ date: '2026-02-30', type: '', intensity: 'brisk', rpe: 11 }, {}] })
    expect(errors.map(describeError)).toEqual([
      'sessions[0].date must be a date (YYYY-MM-DD)',
      'sessions[0].type can’t be empty',
      'sessions[0].intensity must be one of easy, moderate, hard, race',
      'sessions[0].rpe must be at most 10',
      'sessions[1].date is required',
      'sessions[1].type is required',
    ])
  })

  it('FR362_validator_checks_types_and_the_root', () => {
    expect(validateJson(sessionLog, [])).toEqual([{ path: '', message: 'must be an object' }])
    expect(validateJson(sessionLog, {})).toEqual([{ path: 'sessions', message: 'is required' }])
    expect(validateJson({ type: 'integer' }, 1.5).map(describeError)).toEqual(['The value must be a whole number'])
    expect(validateJson({ type: 'object', additionalProperties: false, properties: {} }, { x: 1 })).toEqual([
      { path: 'x', message: 'isn’t a known field' },
    ])
  })

  it('FR362_session_log_is_not_flat_so_gets_the_json_editor_with_a_guide', () => {
    expect(flatFields(sessionLog)).toBeNull()
    const outline = schemaOutline(sessionLog)
    expect(outline.map((e) => e.path)).toEqual([
      'sessions',
      'sessions[].date',
      'sessions[].type',
      'sessions[].intensity',
      'sessions[].distanceKm',
      'sessions[].durationMin',
      'sessions[].rpe',
      'sessions[].notes',
    ])
    expect(outline[1]).toMatchObject({ summary: 'date (YYYY-MM-DD)', required: true })
    expect(schemaSkeleton(sessionLog)).toEqual({ sessions: [{ date: '', type: '' }] })
  })

  it('FR362_flat_schema_builds_a_form_and_zod_rules', () => {
    const schema: JsonSchema = {
      type: 'object',
      required: ['name', 'level'],
      properties: {
        name: { type: 'string', maxLength: 5 },
        level: { enum: ['a', 'b'] },
        on: { type: 'string', format: 'date' },
        count: { type: 'integer', minimum: 1 },
        ok: { type: 'boolean' },
      },
    }
    const fields = flatFields(schema)
    expect(fields?.map((f) => [f.key, f.kind, f.required])).toEqual([
      ['name', 'text', true],
      ['level', 'enum', true],
      ['on', 'date', false],
      ['count', 'number', false],
      ['ok', 'boolean', false],
    ])
    if (!fields) {
      return
    }
    const zod = zodForFields(fields)
    const bad = zod.safeParse({ name: 'toolong', level: '', on: '2026-13-01', count: '1.5', ok: false })
    expect(bad.success).toBe(false)
    expect(bad.error?.issues.map((i) => i.message)).toEqual([
      'Name must be at most 5 characters.',
      'Choose level.',
      'On must be a date (YYYY-MM-DD).',
      'Count must be a whole number.',
    ])
    const values = { name: 'ok', level: 'b', on: '', count: '3', ok: true }
    expect(zod.safeParse(values).success).toBe(true)
    expect(toJsonValue(fields, values)).toEqual({ name: 'ok', level: 'b', count: 3, ok: true })
    expect(isBlank(fields, { name: '', level: '', on: '', count: ' ', ok: false })).toBe(true)
  })
})
