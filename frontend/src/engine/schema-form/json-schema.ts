/**
 * FR-362: a small JSON Schema subset for input artifacts. The editors check an input in the browser so schema errors
 * show inline before submitting; the API validates it again (400 INPUT_INVALID), so keywords outside this subset are
 * ignored here rather than guessed at. No dependency: the CSP has no 'unsafe-eval' (FR-99), which rules out compiling
 * validators.
 *
 * Supported: type (one or a list), enum, const, required, properties, additionalProperties (false or a schema), items,
 * minItems, maxItems, minLength, maxLength, pattern, minimum, maximum, exclusiveMinimum, exclusiveMaximum, and the
 * formats date, date-time and email.
 */

export type JsonType = 'object' | 'array' | 'string' | 'number' | 'integer' | 'boolean' | 'null'

export interface JsonSchema {
  title?: string
  description?: string
  type?: JsonType | JsonType[]
  enum?: unknown[]
  const?: unknown
  required?: string[]
  properties?: Record<string, JsonSchema>
  additionalProperties?: boolean | JsonSchema
  items?: JsonSchema
  minItems?: number
  maxItems?: number
  minLength?: number
  maxLength?: number
  pattern?: string
  format?: string
  minimum?: number
  maximum?: number
  exclusiveMinimum?: number
  exclusiveMaximum?: number
}

/** One schema failure: `path` is `sessions[0].date` style (empty for the value itself). */
export interface SchemaError {
  path: string
  message: string
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

/** The definition's `schema` (a JSON element) as a schema, or null when there is none or it isn't an object. */
export function asSchema(value: unknown): JsonSchema | null {
  return isRecord(value) ? value : null
}

export function typeOf(value: unknown): JsonType {
  if (value === null) {
    return 'null'
  }
  if (Array.isArray(value)) {
    return 'array'
  }
  if (typeof value === 'number') {
    return Number.isInteger(value) ? 'integer' : 'number'
  }
  if (typeof value === 'string') {
    return 'string'
  }
  if (typeof value === 'boolean') {
    return 'boolean'
  }
  return 'object'
}

const typeNames: Record<JsonType, string> = {
  object: 'an object',
  array: 'a list',
  string: 'text',
  number: 'a number',
  integer: 'a whole number',
  boolean: 'true or false',
  null: 'null',
}

function matchesType(value: unknown, type: JsonType): boolean {
  const actual = typeOf(value)
  return actual === type || (type === 'number' && actual === 'integer')
}

/** `YYYY-MM-DD` that is a real calendar date. */
export function isIsoDate(text: string): boolean {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text)
  if (!m) {
    return false
  }
  const [y, mo, d] = [Number(m[1]), Number(m[2]), Number(m[3])]
  const date = new Date(Date.UTC(y, mo - 1, d))
  return date.getUTCFullYear() === y && date.getUTCMonth() === mo - 1 && date.getUTCDate() === d
}

const formatChecks: Record<string, { test: (text: string) => boolean; message: string }> = {
  date: { test: isIsoDate, message: 'must be a date (YYYY-MM-DD)' },
  'date-time': {
    test: (t) => /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/i.test(t) && isIsoDate(t.slice(0, 10)),
    message: 'must be a date and time (e.g. 2026-11-27T09:00:00Z)',
  },
  email: { test: (t) => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(t), message: 'must be an email address' },
}

const join = (path: string, key: string | number) =>
  typeof key === 'number' ? `${path}[${String(key)}]` : path ? `${path}.${key}` : key

const show = (value: unknown) => JSON.stringify(value)

function check(schema: JsonSchema, value: unknown, path: string, errors: SchemaError[]): void {
  const add = (message: string) => errors.push({ path, message })
  if (schema.type !== undefined) {
    const types = Array.isArray(schema.type) ? schema.type : [schema.type]
    if (!types.some((t) => matchesType(value, t))) {
      add(`must be ${types.map((t) => typeNames[t]).join(' or ')}`)
      return
    }
  }
  if (schema.const !== undefined && show(schema.const) !== show(value)) {
    add(`must be ${show(schema.const)}`)
  }
  if (schema.enum && !schema.enum.some((option) => show(option) === show(value))) {
    add(`must be one of ${schema.enum.map((o) => (typeof o === 'string' ? o : show(o))).join(', ')}`)
  }
  if (typeof value === 'string') {
    if (schema.minLength !== undefined && value.length < schema.minLength) {
      add(schema.minLength === 1 ? 'can’t be empty' : `must be at least ${String(schema.minLength)} characters`)
    }
    if (schema.maxLength !== undefined && value.length > schema.maxLength) {
      add(`must be at most ${String(schema.maxLength)} characters`)
    }
    if (schema.pattern !== undefined && !safePattern(schema.pattern, value)) {
      add(`must match the pattern ${schema.pattern}`)
    }
    const format = schema.format ? formatChecks[schema.format] : undefined
    if (format && !format.test(value)) {
      add(format.message)
    }
  }
  if (typeof value === 'number') {
    if (schema.minimum !== undefined && value < schema.minimum) {
      add(`must be at least ${String(schema.minimum)}`)
    }
    if (schema.maximum !== undefined && value > schema.maximum) {
      add(`must be at most ${String(schema.maximum)}`)
    }
    if (schema.exclusiveMinimum !== undefined && value <= schema.exclusiveMinimum) {
      add(`must be more than ${String(schema.exclusiveMinimum)}`)
    }
    if (schema.exclusiveMaximum !== undefined && value >= schema.exclusiveMaximum) {
      add(`must be less than ${String(schema.exclusiveMaximum)}`)
    }
  }
  if (Array.isArray(value)) {
    if (schema.minItems !== undefined && value.length < schema.minItems) {
      add(`must have at least ${String(schema.minItems)} item${schema.minItems === 1 ? '' : 's'}`)
    }
    if (schema.maxItems !== undefined && value.length > schema.maxItems) {
      add(`must have at most ${String(schema.maxItems)} item${schema.maxItems === 1 ? '' : 's'}`)
    }
    const items = schema.items
    if (items) {
      value.forEach((item, i) => {
        check(items, item, join(path, i), errors)
      })
    }
  }
  if (isRecord(value)) {
    for (const key of schema.required ?? []) {
      if (!(key in value)) {
        errors.push({ path: join(path, key), message: 'is required' })
      }
    }
    const properties = schema.properties ?? {}
    for (const [key, child] of Object.entries(value)) {
      const propertySchema = properties[key]
      if (propertySchema) {
        check(propertySchema, child, join(path, key), errors)
      } else if (schema.additionalProperties === false) {
        errors.push({ path: join(path, key), message: 'isn’t a known field' })
      } else if (isRecord(schema.additionalProperties)) {
        check(schema.additionalProperties, child, join(path, key), errors)
      }
    }
  }
}

function safePattern(pattern: string, value: string): boolean {
  try {
    return new RegExp(pattern, 'u').test(value)
  } catch {
    // A pattern this browser can't compile is left to the API.
    return true
  }
}

/** Every failure of `value` against `schema`, in document order (empty when it passes this subset). */
export function validateJson(schema: JsonSchema, value: unknown): SchemaError[] {
  const errors: SchemaError[] = []
  check(schema, value, '', errors)
  return errors
}

/** "sessions[0].date must be a date (YYYY-MM-DD)", or the bare message for the value itself. */
export function describeError(error: SchemaError): string {
  return error.path ? `${error.path} ${error.message}` : `The value ${error.message}`
}
