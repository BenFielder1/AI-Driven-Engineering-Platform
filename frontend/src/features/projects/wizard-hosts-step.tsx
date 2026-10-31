import { useWatch, type Control, type FieldErrors, type UseFormRegister } from 'react-hook-form'

import { panelClass } from '../../components/shared/panel-class'
import { Alert } from '../../components/ui/alert'
import { RadioGroup } from '../../components/ui/radio-group'
import { TextField } from '../../components/ui/text-field'
import { cn } from '../../lib/utils'
import {
  databaseLinkIdLabels,
  databasePickerTypes,
  databasePlatformNames,
  databaseProviderLabel,
  databaseResourceNouns,
} from '../deployments/database-format'
import { deploymentProviderLabels, readinessItemLabels, resourceNouns, resourcePickerTypes } from '../deployments/deployment-format'
import { DeploymentProviderLogo } from '../deployments/deployment-provider-logo'
import { HostSetupFields, type HostSetupField } from '../deployments/host-setup-fields'
import type { WizardValues } from './schemas'
import { HOST_SKIP, hostField, hostFieldIds } from './wizard-host-fields'
import type { WizardHost } from './wizard-hosts'

const skipOption = { value: HOST_SKIP, label: 'Set up later', description: 'Do it any time in Settings → Deployment.' }

const propOf: Record<HostSetupField, 'ConnectionId' | 'Mode' | 'Name' | 'Region' | 'Plan' | 'ResourceId'> = {
  connectionId: 'ConnectionId',
  mode: 'Mode',
  name: 'Name',
  region: 'Region',
  plan: 'Plan',
  resourceId: 'ResourceId',
}

/** One target or database of the step: set up later, create or link with an own connection, or an External string. */
function HostCard({
  host,
  control,
  register,
  errors,
  onAddConnection,
}: {
  host: WizardHost
  control: Control<WizardValues>
  register: UseFormRegister<WizardValues>
  errors: FieldErrors<WizardValues>
  onAddConnection: () => void
}) {
  const slot = host.slot
  const mode = useWatch({ control, name: hostField(slot, 'Mode') })
  const connectionId = useWatch({ control, name: hostField(slot, 'ConnectionId') })
  const headingId = `${hostFieldIds[hostField(slot, 'Mode')]}-title`
  const database = host.kind === 'database'
  const platformName = database ? (databasePlatformNames[host.provider] ?? databaseProviderLabel(host.provider)) : deploymentProviderLabels[host.provider]
  const noun = database ? (databaseResourceNouns[host.provider] ?? 'database') : resourceNouns[host.provider]
  const subject = database ? `${host.environment} database` : host.component

  let body
  if (host.provider === 'external') {
    const field = hostField(slot, 'ConnectionString')
    body = (
      <>
        <RadioGroup
          id={hostFieldIds[hostField(slot, 'Mode')]}
          legend={`The ${subject}`}
          inputProps={register(hostField(slot, 'Mode'))}
          error={errors[hostField(slot, 'Mode')]?.message}
          options={[skipOption, { value: 'external', label: 'Enter a connection string' }]}
        />
        {mode === 'external' && (
          <TextField
            id={hostFieldIds[field]}
            label={`${host.environment === 'production' ? 'Production' : 'Preview'} connection string`}
            hint="A postgres:// or postgresql:// URI, or an ADO.NET string for SQL Server. AIDE stores it encrypted and never shows it again."
            type="password"
            autoComplete="off"
            spellCheck={false}
            error={errors[field]?.message}
            {...register(field)}
          />
        )}
      </>
    )
  } else if (!host.needsConnection) {
    body = (
      <>
        <RadioGroup
          id={hostFieldIds[hostField(slot, 'Mode')]}
          legend={`The ${subject}`}
          inputProps={register(hostField(slot, 'Mode'))}
          error={errors[hostField(slot, 'Mode')]?.message}
          options={[skipOption, { value: 'create', label: `Turn on ${platformName}` }]}
        />
        <p className="text-sm text-muted-foreground">
          {platformName} needs no extra connection: AIDE uses the project&apos;s GitHub App installation (or its access token).
        </p>
      </>
    )
  } else {
    const name = (field: HostSetupField) => hostField(slot, propOf[field])
    body = (
      <HostSetupFields
        ids={{
          connectionId: hostFieldIds[name('connectionId')],
          mode: hostFieldIds[name('mode')],
          name: hostFieldIds[name('name')],
          region: hostFieldIds[name('region')],
          plan: hostFieldIds[name('plan')],
          resourceId: hostFieldIds[name('resourceId')],
        }}
        provider={host.provider}
        platformName={platformName}
        noun={noun}
        connectionsEnabled
        connectionHint={database ? 'One of your own tokens. AIDE uses it to provision, check and restore this database.' : 'One of your own tokens. Every deployment of this target uses it, whoever triggers it.'}
        register={(field) => register(name(field))}
        errors={{
          connectionId: errors[name('connectionId')]?.message,
          mode: errors[name('mode')]?.message,
          name: errors[name('name')]?.message,
          region: errors[name('region')]?.message,
          plan: errors[name('plan')]?.message,
          resourceId: errors[name('resourceId')]?.message,
        }}
        mode={mode}
        connectionId={connectionId}
        modeLegend={`The ${subject}`}
        modeOptions={[
          skipOption,
          {
            value: 'create',
            label: `Create a new ${platformName} ${noun}`,
            description: database ? 'AIDE provisions it and stores its connection details encrypted.' : "Linked to the project's repository, built as the stack says.",
          },
          { value: 'link', label: `Link an existing ${platformName} ${noun}`, description: 'One your connection can reach.' },
        ]}
        pickerType={database ? (databasePickerTypes[host.provider] ?? null) : resourcePickerTypes[host.provider]}
        linkIdLabel={database ? (databaseLinkIdLabels[host.provider] ?? `${platformName} ${noun} id`) : `${platformName} ${noun} id`}
        nameField={database ? { label: 'Name (optional)', hint: "The project's name when empty.", maxLength: 60 } : { label: 'Name', hint: '', maxLength: 100 }}
        regionField={
          database
            ? host.provider === 'railway'
              ? null
              : host.provider === 'supabase'
                ? { label: 'Region', hint: 'A region code (e.g. eu-west-2), or americas, emea or apac.' }
                : { hint: "The backend's region when empty." }
            : { hint: `${platformName}'s default when empty.` }
        }
        planField={database ? (host.provider === 'render' ? { hint: "Render's default (free) when empty." } : null) : { hint: `${platformName}'s default when empty.` }}
        modeFirst
        showDetails={mode !== HOST_SKIP}
        onAddConnection={onAddConnection}
      />
    )
  }

  return (
    <section aria-labelledby={headingId} data-testid={`wizard-host-${database ? `database-${host.environment}` : host.component}`} className={cn(panelClass, 'flex min-w-0 flex-col gap-4 p-4')}>
      <h3 id={headingId} className="flex items-center gap-2 text-lg">
        {!database && <DeploymentProviderLogo provider={host.provider} className="size-5" />}
        {host.title}
      </h3>
      {body}
    </section>
  )
}

/**
 * §7 wizard step "Deployment" for a composed stack: one card per host the Stack step chose (per component and database
 * environment), each set up later (the default) or bound to one of the caller's own connections, with create or link. The
 * setup runs right after the project is created (`applyHostSetups`), through the same endpoints as Settings → Deployment.
 */
export function WizardHostsStep({
  hosts,
  control,
  register,
  errors,
  gitMode,
  onAddConnection,
}: {
  hosts: readonly WizardHost[]
  control: Control<WizardValues>
  register: UseFormRegister<WizardValues>
  errors: FieldErrors<WizardValues>
  gitMode: WizardValues['gitMode']
  onAddConnection: () => void
}) {
  if (hosts.length === 0) {
    return (
      <Alert tone="info" title="Nothing to set up">
        <p>The stack has no deployment hosts. To deploy, choose a host in the Stack step&apos;s Deployment layer, or later in Settings → Stack.</p>
      </Alert>
    )
  }
  return (
    <>
      <p className="text-sm text-muted-foreground">
        This step is optional. What you choose is set up right after the project is created; anything else waits in Settings → Deployment.
      </p>
      {gitMode === 'local' && hosts.some((h) => h.kind === 'target') && (
        <Alert tone="info" title="Hosted deployment needs a hosted repository">
          <p>
            With a Local repository, targets work only where AIDE runs its test deployment provider. Elsewhere the project is still created, and AIDE reports
            which setup it refused. Choose a hosted repository in the Repository step to deploy.
          </p>
        </Alert>
      )}
      {hosts.map((host) => (
        <HostCard key={host.slot} host={host} control={control} register={register} errors={errors} onAddConnection={onAddConnection} />
      ))}
      <section aria-labelledby="wizard-readiness-title" className="flex flex-col gap-2 text-sm">
        <h3 id="wizard-readiness-title" className="text-lg">
          Readiness preview
        </h3>
        <p className="text-muted-foreground">Once a target is set up, AIDE checks these and shows the results, with fix links, in Settings → Deployment:</p>
        <ul aria-label="Readiness checks" className="list-disc pl-5">
          {Object.values(readinessItemLabels).map((label) => (
            <li key={label}>{label}</li>
          ))}
        </ul>
      </section>
    </>
  )
}
