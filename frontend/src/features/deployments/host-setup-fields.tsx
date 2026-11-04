import type { UseFormRegisterReturn } from 'react-hook-form'

import { RadioGroup } from '../../components/ui/radio-group'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextField } from '../../components/ui/text-field'
import { Link } from '../../lib/navigation'
import { connectionLabel, credentialStatusLabels } from '../connections/connection-format'
import { isPlatformProvider, usePlatformConnections, usePlatformResources, type PlatformResourceType } from '../connections/platform-connections-api'

export type HostSetupField = 'connectionId' | 'mode' | 'name' | 'region' | 'plan' | 'resourceId'

interface TextFieldCopy {
  label?: string
  hint: string
}

/**
 * FR-144, FR-152, §7: the setup controls of a deployment target or database, shared by the Settings forms and the wizard's
 * Deployment step: one of the caller's own connections, create or link, the create fields and the link picker. It holds no
 * form of its own: the caller registers the fields.
 */
export function HostSetupFields({
  ids,
  provider,
  platformName,
  noun,
  connectionsEnabled,
  connectionHint,
  register,
  errors,
  mode,
  connectionId,
  modeLegend,
  modeOptions,
  pickerType,
  linkIdLabel,
  nameField,
  regionField,
  planField,
  modeFirst = false,
  showDetails = true,
  linkPicker = true,
  onAddConnection,
}: {
  ids: Record<HostSetupField, string>
  provider: string
  platformName: string
  noun: string
  connectionsEnabled: boolean
  connectionHint: string
  register: (field: HostSetupField) => UseFormRegisterReturn
  errors: Partial<Record<HostSetupField, string | undefined>>
  mode: string
  connectionId: string
  modeLegend: string
  modeOptions: { value: string; label: string; description?: string }[]
  pickerType: PlatformResourceType | null
  linkIdLabel: string
  nameField: TextFieldCopy & { label: string; maxLength: number }
  regionField: TextFieldCopy | null
  planField: TextFieldCopy | null
  /** The wizard asks create, link or later first, and shows the rest only when not later. */
  modeFirst?: boolean
  showDetails?: boolean
  /** False when Link has no resource of its own (a Vercel group links per part, FR-192). */
  linkPicker?: boolean
  /** "Add a token in Connections": the wizard saves its draft before Connections opens. */
  onAddConnection?: () => void
}) {
  const platform = isPlatformProvider(provider) ? provider : undefined
  const connections = usePlatformConnections({ enabled: connectionsEnabled && showDetails && platform !== undefined, ...(platform ? { provider: platform } : {}) })
  const own = connections.data ?? []
  const selected = own.find((c) => c.id === connectionId)
  const resources = usePlatformResources(showDetails && linkPicker && mode === 'link' && connectionId && pickerType ? connectionId : null, pickerType)

  const connection = (
    <div className="flex flex-col gap-2">
      {connections.isPending && <LoadingState label={`Loading your ${platformName} connections…`} />}
      {connections.isError && (
        <ErrorState error={connections.error} title={`Your ${platformName} connections couldn't load`} onRetry={() => void connections.refetch()} />
      )}
      {connections.isSuccess && (
        <SelectField id={ids.connectionId} label={`${platformName} connection`} hint={connectionHint} error={errors.connectionId} {...register('connectionId')}>
          <option value="">Pick a connection</option>
          {own.map((c) => (
            <option key={c.id} value={c.id}>
              {connectionLabel(c)}
              {c.accountName ? ` · ${c.accountName}` : ''}
              {c.status === 'valid' ? '' : ` (${credentialStatusLabels[c.status].toLowerCase()})`}
            </option>
          ))}
        </SelectField>
      )}
      {connections.isSuccess && own.length === 0 && <p className="text-sm text-muted-foreground">You have no {platformName} connections yet.</p>}
      {selected?.status === 'invalid' && <p className="text-sm text-destructive">{platformName} rejected this token; setting up with it will fail.</p>}
      <p>
        <Link to="/connections" className="text-sm font-medium text-primary underline underline-offset-2" onClick={onAddConnection}>
          Add a token in Connections
        </Link>
      </p>
    </div>
  )
  const modeGroup = <RadioGroup id={ids.mode} legend={modeLegend} inputProps={register('mode')} error={errors.mode} options={modeOptions} />

  return (
    <>
      {modeFirst && modeGroup}
      {showDetails && connection}
      {!modeFirst && modeGroup}
      {showDetails && mode === 'create' && (
        <div className="grid gap-4 sm:grid-cols-3">
          <TextField
            id={ids.name}
            label={nameField.label}
            hint={nameField.hint || undefined}
            autoComplete="off"
            spellCheck={false}
            maxLength={nameField.maxLength}
            className="sm:col-span-3"
            error={errors.name}
            {...register('name')}
          />
          {regionField && (
            <TextField
              id={ids.region}
              label={regionField.label ?? 'Region (optional)'}
              hint={regionField.hint}
              autoComplete="off"
              spellCheck={false}
              maxLength={64}
              error={errors.region}
              {...register('region')}
            />
          )}
          {planField && (
            <TextField
              id={ids.plan}
              label={planField.label ?? 'Plan (optional)'}
              hint={planField.hint}
              autoComplete="off"
              spellCheck={false}
              maxLength={64}
              error={errors.plan}
              {...register('plan')}
            />
          )}
        </div>
      )}
      {showDetails &&
        linkPicker &&
        mode === 'link' &&
        (pickerType === null ? (
          <TextField
            id={ids.resourceId}
            label={linkIdLabel}
            autoComplete="off"
            spellCheck={false}
            maxLength={200}
            error={errors.resourceId}
            {...register('resourceId')}
          />
        ) : !connectionId ? (
          <p className="text-sm text-muted-foreground">Pick a connection first: the list shows what it can reach.</p>
        ) : resources.isPending ? (
          <LoadingState label={`Loading your ${platformName} ${noun}s…`} />
        ) : resources.isError ? (
          <ErrorState error={resources.error} title={`Your ${platformName} ${noun}s couldn't load`} onRetry={() => void resources.refetch()} />
        ) : (
          <SelectField id={ids.resourceId} label={`Existing ${noun}`} error={errors.resourceId} {...register('resourceId')}>
            <option value="">{resources.data.length === 0 ? `No ${noun}s found` : `Pick a ${noun}`}</option>
            {resources.data.map((r) => (
              <option key={r.id} value={r.id}>
                {r.name}
                {r.detail ? ` · ${r.detail}` : ''}
              </option>
            ))}
          </SelectField>
        ))}
    </>
  )
}
