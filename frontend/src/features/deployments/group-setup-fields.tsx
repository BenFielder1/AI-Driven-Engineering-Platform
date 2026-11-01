import type { UseFormRegisterReturn } from 'react-hook-form'

import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { usePlatformResources } from '../connections/platform-connections-api'
import { deploymentProviderLabels } from './deployment-format'
import { deploymentPartLabels, groupModeOptions, groupPartNoun, groupPartPicker, groupProjectPicker } from './deployment-group-format'
import { GROUP_NAME_MAX_LENGTH, groupLinkFields, type GroupLinkField, type GroupShape } from './host-setup-body'
import { HostSetupFields, type HostSetupField } from './host-setup-fields'

export type GroupSetupField = HostSetupField | GroupLinkField

/** FR-190 Link: one part's existing resource, or Create in this project. A Railway service belongs to the chosen project. */
function GroupPartPicker({
  group,
  part,
  id,
  connectionId,
  projectId,
  inputProps,
  error,
}: {
  group: GroupShape
  part: GroupShape['parts'][number]
  id: string
  connectionId: string
  projectId: string
  inputProps: UseFormRegisterReturn
  error: string | undefined
}) {
  const type = groupPartPicker(group.provider, part)
  const resources = usePlatformResources(connectionId, type)
  const noun = groupPartNoun(group.provider, part)
  const label = deploymentPartLabels[part]
  if (resources.isPending) {
    return <LoadingState label={`Loading the ${noun}s for the ${part}…`} />
  }
  if (resources.isError) {
    return <ErrorState error={resources.error} title={`The ${noun}s for the ${part} couldn't load`} onRetry={() => void resources.refetch()} />
  }
  const list = group.provider === 'railway' ? resources.data.filter((r) => r.id.startsWith(`${projectId}:`)) : resources.data
  return (
    <SelectField id={id} label={`${label} ${noun}`} error={error} {...inputProps}>
      <option value="">{group.capability === 'shared_setup' ? `Pick a ${noun}` : 'Create in this project'}</option>
      {list.map((r) => (
        <option key={r.id} value={r.id}>
          {r.name}
          {r.detail ? ` · ${r.detail}` : ''}
        </option>
      ))}
    </SelectField>
  )
}

/**
 * FR-188, FR-190: a group's setup controls, shared by the wizard's window and the Settings card: one connection, then
 * create (name, and region and plan where the platform takes them) or link (the project, then each part). It holds no form
 * of its own: the caller registers the fields.
 */
export function GroupSetupFields({
  group,
  ids,
  register,
  errors,
  mode,
  connectionId,
  resourceId,
  extraModeOptions = [],
  modeFirst = false,
  showDetails = true,
  onAddConnection,
}: {
  group: GroupShape
  ids: Record<GroupSetupField, string>
  register: (field: GroupSetupField) => UseFormRegisterReturn
  errors: Partial<Record<GroupSetupField, string | undefined>>
  mode: string
  connectionId: string
  resourceId: string
  extraModeOptions?: { value: string; label: string; description?: string }[]
  modeFirst?: boolean
  showDetails?: boolean
  onAddConnection?: () => void
}) {
  const platformName = deploymentProviderLabels[group.provider]
  const shared = group.capability === 'shared_setup'
  const placed = group.provider === 'render'
  const linking = showDetails && mode === 'link'
  return (
    <>
      <HostSetupFields
        ids={ids}
        provider={group.provider}
        platformName={platformName}
        noun="project"
        connectionsEnabled
        connectionHint="One of your own tokens. Every part uses it, whoever triggers a deployment."
        register={register}
        errors={errors}
        mode={mode}
        connectionId={connectionId}
        modeLegend={shared ? `The ${platformName} projects` : `The ${platformName} project`}
        modeOptions={[...extraModeOptions, ...groupModeOptions(group.capability)]}
        pickerType={groupProjectPicker(group.provider)}
        linkIdLabel={`${platformName} project id`}
        nameField={{
          label: 'Name',
          hint: shared ? 'The projects are named <name>-web and <name>-api.' : "The project's name. The services are named after it.",
          maxLength: GROUP_NAME_MAX_LENGTH,
        }}
        regionField={placed ? { hint: `${platformName}'s default when empty.` } : null}
        planField={placed ? { hint: `For the services, ${platformName}'s default when empty. The database uses the default plan.` } : null}
        modeFirst={modeFirst}
        showDetails={showDetails}
        linkPicker={!shared}
        {...(onAddConnection ? { onAddConnection } : {})}
      />
      {linking && connectionId && (shared || resourceId) && (
        <fieldset className="flex flex-col gap-3">
          <legend className="mb-1 text-sm font-medium">{shared ? 'The project of each part' : 'Each part in this project'}</legend>
          {group.parts.map((part) => {
            const field = groupLinkFields[part]
            return (
              <GroupPartPicker
                key={part}
                group={group}
                part={part}
                id={ids[field]}
                connectionId={connectionId}
                projectId={resourceId}
                inputProps={register(field)}
                error={errors[field]}
              />
            )
          })}
        </fieldset>
      )}
    </>
  )
}

/** FR-191: a part outside its group is set up in the group only when the Owner says so (`groupId`). */
export function JoinGroupField({ platform, checked, onChange }: { platform: string; checked: boolean; onChange: (checked: boolean) => void }) {
  return (
    <label className="flex min-h-10 items-center gap-3 text-sm">
      <input
        type="checkbox"
        className="size-4 accent-primary"
        checked={checked}
        onChange={(e) => {
          onChange(e.target.checked)
        }}
      />
      Set this up in the {platform} group
    </label>
  )
}
