import { useCallback, useId, useState } from 'react'

import { Alert, ErrorSummary, type SummaryError } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { TextAreaField } from '../../components/ui/textarea-field'
import { TextField } from '../../components/ui/text-field'
import { errorMessage, isApiError } from '../../lib/errors'
import type { Artifact } from '../artifacts/artifacts-api'
import { useProjectAccess } from '../projects/project-context'
import type { StackCatalog } from '../stack/stack-api'
import { catalogOption, NONE, optionName } from '../stack/stack-model'
import { useSaveStackProfile } from './analysis-api'
import { HostPickers, hostPickerFieldId, LayerOptionSelect, type HostPickerChoice } from './profile-pickers'
import {
  blocksApproval,
  confidenceLabels,
  editableLayers,
  hostsReady,
  layerStatusLabels,
  profileFieldOfKey,
  profileLayerLabels,
  selectionOfProfile,
  withHost,
  withLayerChoice,
  withLayerPath,
  type EditableLayer,
  type StackProfileDocument,
} from './stack-profile-format'
import { useProfileOptions } from './use-profile-options'

export const HOSTS_WAIT_REASON =
  'Resolve the frontend, backend and data layers first: the hosts depend on them.'

/** The element id of a layer's picker or path field in a form with `idPrefix`. */
export const layerFieldId = (idPrefix: string, layer: EditableLayer, part: 'option' | 'path' = 'option') =>
  part === 'path' ? `${idPrefix}-${layer}-path` : `${idPrefix}-${layer}`

/**
 * Server VALIDATION `errors` of a profile save → messages per form field id (`unmatched` for the summary only).
 * Keys look like `layers.backend.option`, `layers.frontend.path` or `hosts.frontendHost`.
 */
export function profileFieldErrors(error: unknown, idPrefix: string): { byId: Record<string, string>; unmatched: string[] } {
  const byId: Record<string, string> = {}
  const unmatched: string[] = []
  if (!isApiError(error, 'VALIDATION')) {
    return { byId, unmatched }
  }
  for (const [key, messages] of Object.entries(error.fieldErrors)) {
    const field = profileFieldOfKey(key)
    const message = messages.join(' ')
    if (field === null) {
      unmatched.push(`${key}: ${message}`)
    } else if (field.endsWith('.path')) {
      byId[layerFieldId(idPrefix, field.slice(0, -5) as EditableLayer, 'path')] = message
    } else if ((editableLayers as readonly string[]).includes(field)) {
      byId[layerFieldId(idPrefix, field as EditableLayer)] = message
    } else {
      byId[hostPickerFieldId(idPrefix, field as HostPickerChoice)] = message
    }
  }
  return { byId, unmatched }
}

/** The label of each form field id, for the error summary. */
function fieldLabel(catalog: StackCatalog, idPrefix: string, id: string): string {
  for (const layer of editableLayers) {
    if (id === layerFieldId(idPrefix, layer)) {
      return profileLayerLabels[layer]
    }
    if (id === layerFieldId(idPrefix, layer, 'path')) {
      return `${profileLayerLabels[layer]} folder`
    }
  }
  const choice = id.slice(idPrefix.length + 1)
  return catalog.choices.find((c) => c.id === choice)?.name ?? choice
}

/**
 * FR-183 Edit (and FR-180 "Fill in manually"): a form over the structured profile, not raw Markdown. Each layer's option
 * (filtered by `/api/stack/options`) and folder, the hosts (FR-184) and the narrative. Saving renders the profile block
 * and posts it as a new draft by the user, from `baseVersion` (CONFLICT when someone saved meanwhile); the server's
 * field errors land on their fields.
 */
export function ProfileEditor({
  catalog,
  initial,
  narrative,
  baseVersion,
  title,
  intro,
  onCancel,
  onSaved,
}: {
  catalog: StackCatalog
  initial: StackProfileDocument
  narrative: string
  baseVersion: number | null
  title: string
  intro: string
  onCancel: () => void
  onSaved: (created: Artifact) => void
}) {
  const { project } = useProjectAccess()
  const idPrefix = `profile-editor${useId().replace(/[^a-zA-Z0-9-]/g, '')}`
  const [doc, setDoc] = useState(initial)
  const [text, setText] = useState(narrative)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [summary, setSummary] = useState<string[]>([])
  const save = useSaveStackProfile(project.id)
  const onHostsCleared = useCallback((next: StackProfileDocument) => {
    setDoc(next)
  }, [])
  const options = useProfileOptions(catalog, doc, onHostsCleared)
  const sel = selectionOfProfile(doc)
  const ready = hostsReady(doc)
  const reasonId = `${idPrefix}-hosts-reason`

  const change = (next: StackProfileDocument) => {
    setFieldErrors({})
    setDoc(next)
  }

  const onSubmit = () => {
    setSummary([])
    setFieldErrors({})
    save.mutate(
      { narrative: text, document: doc, baseVersion },
      {
        onSuccess: (created) => {
          onSaved(created)
        },
        onError: (error) => {
          const { byId, unmatched } = profileFieldErrors(error, idPrefix)
          setFieldErrors(byId)
          setSummary(isApiError(error, 'VALIDATION') ? unmatched : [errorMessage(error, 'createArtifactDraft')])
        },
      },
    )
  }

  const summaryErrors: SummaryError[] = [
    ...summary.map((message) => ({ message })),
    ...Object.entries(fieldErrors).map(([fieldId, message]) => ({ fieldId, message: `${fieldLabel(catalog, idPrefix, fieldId)}: ${message}` })),
  ]

  return (
    <form
      noValidate
      aria-labelledby={`${idPrefix}-title`}
      className="flex flex-col gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        onSubmit()
      }}
    >
      <div>
        <h4 id={`${idPrefix}-title`} className="text-base font-semibold">
          {title}
        </h4>
        <p className="text-sm text-muted-foreground">{intro}</p>
      </div>
      <ErrorSummary title="We couldn't save the profile" errors={summaryErrors} />

      {editableLayers.map((layer) => {
        const value = doc.layers[layer]
        const state = options.data?.choices.find((c) => c.choice === layer)
        const picked = sel[layer]
        const facts = [
          layerStatusLabels[value.status],
          value.confidence ? confidenceLabels[value.confidence] : null,
          value.status === 'unsupported' && value.technology ? `found ${value.technology}` : null,
        ].filter(Boolean)
        return (
          <fieldset key={layer} className="flex flex-col gap-2 rounded-lg border border-border/60 p-3">
            <legend className="px-1 font-medium">{profileLayerLabels[layer]}</legend>
            <p className="text-sm text-muted-foreground">{facts.join(' · ')}</p>
            <LayerOptionSelect
              catalog={catalog}
              choice={layer}
              state={state}
              id={layerFieldId(idPrefix, layer)}
              label={`${profileLayerLabels[layer]} option`}
              value={picked}
              allowUndecided={blocksApproval(value.status, false)}
              error={fieldErrors[layerFieldId(idPrefix, layer)]}
              onChange={(option) => {
                change(
                  withLayerChoice(doc, layer, option, {
                    path: layer === 'data' ? null : (value.path ?? (layer === 'frontend' ? 'frontend' : 'backend')),
                    mode: layer === 'data' ? (catalogOption(catalog, 'data', option)?.mode ?? null) : null,
                  }),
                )
              }}
            />
            {layer !== 'data' && picked !== '' && picked !== NONE && (
              <TextField
                id={layerFieldId(idPrefix, layer, 'path')}
                label={`${profileLayerLabels[layer]} folder`}
                hint="Relative to the repository root, for example frontend or apps/web. Use . for the root."
                autoComplete="off"
                spellCheck={false}
                maxLength={200}
                value={value.path ?? ''}
                error={fieldErrors[layerFieldId(idPrefix, layer, 'path')]}
                onChange={(e) => {
                  change(withLayerPath(doc, layer, e.target.value))
                }}
              />
            )}
          </fieldset>
        )
      })}

      <p className="text-sm">
        <span className="font-medium">Version control: </span>
        {doc.layers.vcs.option ? optionName(catalog, 'vcs', doc.layers.vcs.option) : 'Not known'}{' '}
        <span className="text-muted-foreground">(set by the repository&apos;s host)</span>
      </p>

      <fieldset disabled={!ready} aria-describedby={ready ? undefined : reasonId} className="flex flex-col gap-3 rounded-lg border border-border/60 p-3">
        <legend className="px-1 font-medium">Hosts</legend>
        {!ready && (
          <p id={reasonId} className="text-sm text-muted-foreground">
            {HOSTS_WAIT_REASON}
          </p>
        )}
        <HostPickers
          catalog={catalog}
          options={options.data}
          doc={doc}
          idPrefix={idPrefix}
          disabled={!ready}
          errors={Object.fromEntries(
            Object.entries(fieldErrors).flatMap(([id, message]) =>
              id.startsWith(`${idPrefix}-`) ? [[id.slice(idPrefix.length + 1), message]] : [],
            ),
          )}
          onChange={(choice, option) => {
            change(withHost(doc, choice, option))
          }}
        />
      </fieldset>

      <TextAreaField
        id={`${idPrefix}-narrative`}
        label="Summary"
        hint="What the agents should know about this stack. Shown above the findings."
        rows={4}
        maxLength={8000}
        value={text}
        onChange={(e) => {
          setText(e.target.value)
        }}
      />

      {options.isError && (
        <Alert tone="warning" title="The stack options couldn't load">
          <p>{errorMessage(options.error)} All options are listed; the server checks them when you save.</p>
        </Alert>
      )}

      <div className="flex flex-wrap justify-end gap-2">
        <Button variant="secondary" onClick={onCancel} disabled={save.isPending}>
          Cancel
        </Button>
        <Button type="submit" pending={save.isPending} pendingLabel="Saving…">
          Save as new draft
        </Button>
      </div>
    </form>
  )
}
