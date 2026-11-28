import type { JsonSchema, JsonType } from './json-schema'

/** One line of the JSON editor's field guide, rendered from the schema (FR-362). */
export interface OutlineEntry {
  path: string
  depth: number
  summary: string
  required: boolean
}

const typeWords: Record<JsonType, string> = {
  object: 'object',
  array: 'list',
  string: 'text',
  number: 'number',
  integer: 'whole number',
  boolean: 'true or false',
  null: 'null',
}

function describe(schema: JsonSchema): string {
  if (schema.enum) {
    return `one of ${schema.enum.map((o) => (typeof o === 'string' ? o : JSON.stringify(o))).join(', ')}`
  }
  const types = schema.type === undefined ? [] : Array.isArray(schema.type) ? schema.type : [schema.type]
  let text = types.map((t) => typeWords[t]).join(' or ') || 'any value'
  if (schema.format === 'date') {
    text = 'date (YYYY-MM-DD)'
  } else if (schema.format === 'date-time') {
    text = 'date and time (ISO 8601)'
  } else if (schema.format) {
    text = `${text} (${schema.format})`
  }
  const bounds: string[] = []
  if (schema.minimum !== undefined) {
    bounds.push(`≥ ${String(schema.minimum)}`)
  }
  if (schema.maximum !== undefined) {
    bounds.push(`≤ ${String(schema.maximum)}`)
  }
  if (schema.minLength !== undefined && schema.minLength > 0) {
    bounds.push(schema.minLength === 1 ? 'not empty' : `at least ${String(schema.minLength)} characters`)
  }
  if (schema.maxLength !== undefined) {
    bounds.push(`at most ${String(schema.maxLength)} characters`)
  }
  return bounds.length > 0 ? `${text}, ${bounds.join(', ')}` : text
}

/** The schema's fields, depth first (list items as `name[]`), for the guide beside the JSON editor. */
export function schemaOutline(schema: JsonSchema, path = '', depth = 0, out: OutlineEntry[] = []): OutlineEntry[] {
  const required = new Set(schema.required ?? [])
  for (const [key, child] of Object.entries(schema.properties ?? {})) {
    const childPath = path ? `${path}.${key}` : key
    out.push({ path: childPath, depth, summary: describe(child), required: required.has(key) })
    const item = child.items
    if (item?.properties) {
      schemaOutline(item, `${childPath}[]`, depth + 1, out)
    } else if (child.properties) {
      schemaOutline(child, childPath, depth + 1, out)
    }
  }
  return out
}

/** A starting value with every required field (blank placeholders), for "Insert template". */
export function schemaSkeleton(schema: JsonSchema): unknown {
  const type = Array.isArray(schema.type) ? schema.type[0] : schema.type
  if (schema.const !== undefined) {
    return schema.const
  }
  if (schema.enum && schema.enum.length > 0) {
    return schema.enum[0]
  }
  switch (type ?? (schema.properties ? 'object' : undefined)) {
    case 'object': {
      const required = new Set(schema.required ?? [])
      const out: Record<string, unknown> = {}
      for (const [key, child] of Object.entries(schema.properties ?? {})) {
        if (required.has(key)) {
          out[key] = schemaSkeleton(child)
        }
      }
      return out
    }
    case 'array':
      return schema.items ? [schemaSkeleton(schema.items)] : []
    case 'number':
    case 'integer':
      return schema.minimum ?? 0
    case 'boolean':
      return false
    case 'null':
      return null
    default:
      return ''
  }
}
