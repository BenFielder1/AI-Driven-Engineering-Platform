import { z } from 'zod'

import { humanise } from '../format'
import { isIsoDate, type JsonSchema, type JsonType } from './json-schema'

/** One control of a generated form (FR-362): a top-level property of a flat object schema. */
export type FlatField =
  | { kind: 'text'; key: string; label: string; required: boolean; schema: JsonSchema }
  | { kind: 'date'; key: string; label: string; required: boolean; schema: JsonSchema }
  | { kind: 'number'; key: string; label: string; required: boolean; integer: boolean; schema: JsonSchema }
  | { kind: 'boolean'; key: string; label: string; required: boolean; schema: JsonSchema }
  | { kind: 'enum'; key: string; label: string; required: boolean; options: string[]; schema: JsonSchema }

/** Form values: text, dates, numbers and enums as strings (blank = not given), booleans as booleans. */
export type FlatValues = Record<string, string | boolean>

function singleType(schema: JsonSchema): JsonType | undefined {
  if (Array.isArray(schema.type)) {
    return schema.type.length === 1 ? schema.type[0] : undefined
  }
  return schema.type
}

function fieldFor(key: string, schema: JsonSchema, required: boolean): FlatField | null {
  const label = schema.title ?? humanise(key)
  if (schema.enum) {
    return schema.enum.every((o) => typeof o === 'string')
      ? { kind: 'enum', key, label, required, options: schema.enum, schema }
      : null
  }
  switch (singleType(schema)) {
    case 'string':
      if (schema.format === 'date') {
        return { kind: 'date', key, label, required, schema }
      }
      return { kind: 'text', key, label, required, schema }
    case 'number':
      return { kind: 'number', key, label, required, integer: false, schema }
    case 'integer':
      return { kind: 'number', key, label, required, integer: true, schema }
    case 'boolean':
      return { kind: 'boolean', key, label, required, schema }
    default:
      return null
  }
}

/**
 * The form's fields when `schema` is a flat object (every property a string, number, boolean, enum or date), else
 * null: nested objects, lists and unions get the JSON editor instead (FR-362).
 */
export function flatFields(schema: JsonSchema | null): FlatField[] | null {
  if (!schema || singleType(schema) !== 'object' || !schema.properties) {
    return null
  }
  const required = new Set(schema.required ?? [])
  const fields: FlatField[] = []
  for (const [key, property] of Object.entries(schema.properties)) {
    const field = fieldFor(key, property, required.has(key))
    if (!field) {
      return null
    }
    fields.push(field)
  }
  return fields.length > 0 ? fields : null
}

export function emptyValues(fields: readonly FlatField[]): FlatValues {
  return Object.fromEntries(fields.map((f) => [f.key, f.kind === 'boolean' ? false : '']))
}

/** Whether nothing was entered (blank text, unchecked boxes): an optional input left for later. */
export function isBlank(fields: readonly FlatField[], values: FlatValues): boolean {
  return fields.every((f) => (f.kind === 'boolean' ? values[f.key] !== true : String(values[f.key] ?? '').trim() === ''))
}

function stringRule(field: FlatField & { kind: 'text' | 'date' | 'enum' }) {
  const { schema, label } = field
  return z.string().superRefine((raw, ctx) => {
    const text = raw.trim()
    if (text === '') {
      if (field.required) {
        ctx.addIssue({ code: 'custom', message: field.kind === 'enum' ? `Choose ${label.toLowerCase()}.` : `Enter ${label.toLowerCase()}.` })
      }
      return
    }
    if (field.kind === 'date' && !isIsoDate(text)) {
      ctx.addIssue({ code: 'custom', message: `${label} must be a date (YYYY-MM-DD).` })
    }
    if (field.kind === 'enum' && !field.options.includes(text)) {
      ctx.addIssue({ code: 'custom', message: `${label} must be one of ${field.options.join(', ')}.` })
    }
    if (schema.minLength !== undefined && text.length < schema.minLength) {
      ctx.addIssue({ code: 'custom', message: `${label} must be at least ${String(schema.minLength)} characters.` })
    }
    if (schema.maxLength !== undefined && text.length > schema.maxLength) {
      ctx.addIssue({ code: 'custom', message: `${label} must be at most ${String(schema.maxLength)} characters.` })
    }
  })
}

function numberRule(field: FlatField & { kind: 'number' }) {
  const { schema, label } = field
  return z.string().superRefine((raw, ctx) => {
    const text = raw.trim()
    if (text === '') {
      if (field.required) {
        ctx.addIssue({ code: 'custom', message: `Enter ${label.toLowerCase()}.` })
      }
      return
    }
    const n = Number(text)
    if (!Number.isFinite(n) || (field.integer && !Number.isInteger(n))) {
      ctx.addIssue({ code: 'custom', message: `${label} must be ${field.integer ? 'a whole number' : 'a number'}.` })
      return
    }
    if (schema.minimum !== undefined && n < schema.minimum) {
      ctx.addIssue({ code: 'custom', message: `${label} must be at least ${String(schema.minimum)}.` })
    }
    if (schema.maximum !== undefined && n > schema.maximum) {
      ctx.addIssue({ code: 'custom', message: `${label} must be at most ${String(schema.maximum)}.` })
    }
  })
}

/** The Zod schema of a generated form, built from the JSON schema's properties (RHF + Zod, FR-362). */
export function zodForFields(fields: readonly FlatField[]) {
  const shape: Record<string, z.ZodType<string | boolean, string | boolean>> = {}
  for (const field of fields) {
    shape[field.key] = field.kind === 'boolean' ? z.boolean() : field.kind === 'number' ? numberRule(field) : stringRule(field)
  }
  return z.object(shape)
}

/** The JSON value the form stands for: blank optional fields are left out. */
export function toJsonValue(fields: readonly FlatField[], values: FlatValues): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  for (const field of fields) {
    const raw = values[field.key]
    if (field.kind === 'boolean') {
      out[field.key] = raw === true
      continue
    }
    const text = String(raw ?? '').trim()
    if (text !== '') {
      out[field.key] = field.kind === 'number' ? Number(text) : text
    }
  }
  return out
}

/** Form values from an uploaded JSON object (unknown keys dropped, wrong types left blank). */
export function fromJsonValue(fields: readonly FlatField[], value: Record<string, unknown>): FlatValues {
  const values = emptyValues(fields)
  for (const field of fields) {
    const raw = value[field.key]
    if (field.kind === 'boolean') {
      values[field.key] = raw === true
    } else if (typeof raw === 'string' || typeof raw === 'number') {
      values[field.key] = String(raw)
    }
  }
  return values
}
