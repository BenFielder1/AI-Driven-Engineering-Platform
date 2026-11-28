import { zodResolver } from '@hookform/resolvers/zod'
import { useImperativeHandle, useMemo, useState, type ChangeEvent, type Ref } from 'react'
import { useForm } from 'react-hook-form'

import { Button } from '../../components/ui/button'
import { fieldClass, labelClass } from '../../components/ui/field-classes'
import { SelectField } from '../../components/ui/select-field'
import { TextField } from '../../components/ui/text-field'
import {
  emptyValues,
  flatFields,
  fromJsonValue,
  isBlank,
  toJsonValue,
  zodForFields,
  type FlatField,
  type FlatValues,
} from './flat-fields'
import { describeError, validateJson, type JsonSchema, type SchemaError } from './json-schema'
import { schemaOutline, schemaSkeleton } from './schema-outline'

/** What an editor holds when the dialog submits: a value, nothing (an optional input left for later), or errors. */
export type CollectResult = { kind: 'value'; value: unknown } | { kind: 'empty' } | { kind: 'invalid' }

export interface InputEditorHandle {
  /** Validates (showing the errors inline) and returns the value. */
  collect: () => Promise<CollectResult>
}

export interface InputEditorProps {
  /** Prefix of the controls' ids (unique per dialog and artifact). */
  idPrefix: string
  /** The artifact's title from the definition (FR-305). */
  title: string
  schema: JsonSchema | null
  /** Submit input needs a value; at Start run an input may be left for later (the run waits for it). */
  required: boolean
  ref?: Ref<InputEditorHandle>
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

function FileUpload({ id, onText }: { id: string; onText: (text: string) => void }) {
  const onChange = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    if (file) {
      void file.text().then(onText)
    }
    event.target.value = ''
  }
  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={id} className={labelClass}>
        Upload a JSON file
      </label>
      <input id={id} type="file" accept="application/json,.json" onChange={onChange} className="text-sm" />
    </div>
  )
}

function FlatControl({ field, id, form }: { field: FlatField; id: string; form: ReturnType<typeof useForm<FlatValues>> }) {
  const error = form.formState.errors[field.key]?.message
  const label = field.required ? field.label : `${field.label} (optional)`
  const hint = field.schema.description
  const registered = form.register(field.key)
  switch (field.kind) {
    case 'boolean':
      return (
        <div className="flex items-center gap-2">
          <input id={id} type="checkbox" className="size-4 accent-primary" {...registered} />
          <label htmlFor={id} className={labelClass}>
            {field.label}
          </label>
        </div>
      )
    case 'enum':
      return (
        <SelectField id={id} label={label} hint={hint} error={error} {...registered}>
          <option value="">Choose…</option>
          {field.options.map((option) => (
            <option key={option} value={option}>
              {option}
            </option>
          ))}
        </SelectField>
      )
    case 'date':
      return <TextField id={id} type="date" label={label} hint={hint} error={error} {...registered} />
    case 'number':
      return <TextField id={id} inputMode={field.integer ? 'numeric' : 'decimal'} label={label} hint={hint} error={error} {...registered} />
    case 'text':
      return <TextField id={id} label={label} hint={hint} error={error} {...registered} />
  }
}

/** FR-362: a form generated from a flat object schema, validated with Zod built from it, then the schema itself. */
function FlatForm({ idPrefix, title, schema, required, fields, ref }: InputEditorProps & { schema: JsonSchema; fields: FlatField[] }) {
  const resolver = useMemo(() => zodResolver(zodForFields(fields)), [fields])
  const form = useForm<FlatValues>({ resolver, defaultValues: emptyValues(fields), mode: 'onTouched' })
  const [uploadError, setUploadError] = useState<string | null>(null)

  useImperativeHandle(ref, () => ({
    collect: async () => {
      const values = form.getValues()
      if (!required && isBlank(fields, values)) {
        form.clearErrors()
        return { kind: 'empty' }
      }
      if (!(await form.trigger())) {
        return { kind: 'invalid' }
      }
      const value = toJsonValue(fields, form.getValues())
      const errors = validateJson(schema, value)
      for (const error of errors) {
        const key = error.path.split(/[.[]/)[0] ?? ''
        form.setError(fields.some((f) => f.key === key) ? key : 'root', { type: 'schema', message: describeError(error) })
      }
      return errors.length > 0 ? { kind: 'invalid' } : { kind: 'value', value }
    },
  }))

  const rootError = form.formState.errors.root?.message
  return (
    <fieldset className="flex flex-col gap-3">
      <legend className="mb-1 text-sm font-semibold">{required ? title : `${title} (optional now)`}</legend>
      {!required && <p className="text-sm text-muted-foreground">Leave it blank to add it later: the run waits for it.</p>}
      {fields.map((field) => (
        <FlatControl key={field.key} field={field} id={`${idPrefix}-${field.key}`} form={form} />
      ))}
      {rootError && <p className="text-sm text-destructive">{rootError}</p>}
      <FileUpload
        id={`${idPrefix}-file`}
        onText={(text) => {
          try {
            const parsed: unknown = JSON.parse(text)
            if (!isRecord(parsed)) {
              setUploadError('The file must hold a JSON object.')
              return
            }
            setUploadError(null)
            form.reset(fromJsonValue(fields, parsed))
          } catch {
            setUploadError('That file isn’t valid JSON.')
          }
        }}
      />
      {uploadError && <p className="text-sm text-destructive">{uploadError}</p>}
    </fieldset>
  )
}

type JsonCheck = { kind: 'empty' } | { kind: 'errors'; messages: string[] } | { kind: 'value'; value: unknown }

function checkJson(text: string, schema: JsonSchema | null, required: boolean, title: string): JsonCheck {
  if (text.trim() === '') {
    return required ? { kind: 'errors', messages: [`Enter the ${title.toLowerCase()} as JSON.`] } : { kind: 'empty' }
  }
  let value: unknown
  try {
    value = JSON.parse(text)
  } catch (error) {
    return { kind: 'errors', messages: [`This isn’t valid JSON${error instanceof Error ? `: ${error.message}` : '.'}`] }
  }
  const errors: SchemaError[] = schema ? validateJson(schema, value) : []
  return errors.length > 0 ? { kind: 'errors', messages: errors.map(describeError) } : { kind: 'value', value }
}

/** FR-362: the JSON editor for any other schema, with the schema's errors listed inline and a field guide. */
function JsonEditor({ idPrefix, title, schema, required, ref }: InputEditorProps) {
  const [text, setText] = useState('')
  const [checked, setChecked] = useState(false)
  const result = checked ? checkJson(text, schema, required, title) : null
  const messages = result?.kind === 'errors' ? result.messages : []
  const id = `${idPrefix}-json`
  const hintId = `${id}-hint`
  const errorId = `${id}-errors`
  const outline = useMemo(() => (schema ? schemaOutline(schema) : []), [schema])

  useImperativeHandle(ref, () => ({
    collect: () => {
      setChecked(true)
      const r = checkJson(text, schema, required, title)
      return Promise.resolve(r.kind === 'errors' ? { kind: 'invalid' } : r)
    },
  }))

  return (
    <fieldset className="flex flex-col gap-3">
      <legend className="mb-1 text-sm font-semibold">{required ? title : `${title} (optional now)`}</legend>
      {outline.length > 0 && (
        <details className="rounded-lg border border-border/60 p-3 text-sm">
          <summary className="cursor-pointer font-medium">Fields of {title.toLowerCase()}</summary>
          <ul className="mt-2 flex flex-col gap-1" aria-label={`Fields of ${title.toLowerCase()}`}>
            {outline.map((entry) => (
              <li key={entry.path} style={{ paddingLeft: `${String(entry.depth * 1.25)}rem` }}>
                <code className="font-mono">{entry.path}</code>: {entry.summary}
                {entry.required ? ' (required)' : ''}
              </li>
            ))}
          </ul>
        </details>
      )}
      <div className="flex flex-col gap-1">
        <label htmlFor={id} className={labelClass}>
          {title} (JSON)
        </label>
        <p id={hintId} className="text-sm text-muted-foreground">
          {required ? 'Paste or type the JSON, or upload a file.' : 'Paste or type the JSON, or upload a file. Leave it empty to add it later: the run waits for it.'}
        </p>
        <textarea
          id={id}
          rows={10}
          spellCheck={false}
          value={text}
          onChange={(e) => {
            setText(e.target.value)
          }}
          aria-invalid={messages.length > 0 ? true : undefined}
          aria-describedby={messages.length > 0 ? `${hintId} ${errorId}` : hintId}
          className={fieldClass(messages.length > 0, 'font-mono')}
        />
        {messages.length > 0 && (
          <ul id={errorId} className="list-disc pl-5 text-sm text-destructive">
            {messages.map((m) => (
              <li key={m}>{m}</li>
            ))}
          </ul>
        )}
      </div>
      <div className="flex flex-wrap items-end gap-3">
        <FileUpload
          id={`${idPrefix}-file`}
          onText={(uploaded) => {
            setText(uploaded)
            setChecked(true)
          }}
        />
        {schema && (
          <Button
            variant="secondary"
            onClick={() => {
              setText(JSON.stringify(schemaSkeleton(schema), null, 2))
            }}
          >
            Insert template
          </Button>
        )}
      </div>
    </fieldset>
  )
}

/**
 * FR-362: one declared input artifact's editor, rendered from its schema: a generated form for a flat object schema,
 * else a JSON editor. Either takes a JSON file upload. The parent collects every editor's value on submit.
 */
export function InputEditor(props: InputEditorProps) {
  const fields = useMemo(() => flatFields(props.schema), [props.schema])
  return fields && props.schema ? <FlatForm {...props} schema={props.schema} fields={fields} /> : <JsonEditor {...props} />
}
