import { useEffect, useRef, type ReactNode } from 'react'
import { useWatch, type Control, type FieldErrors, type UseFormRegister } from 'react-hook-form'

import { panelClass } from '../../components/shared/panel-class'
import { Alert } from '../../components/ui/alert'
import { RadioGroup } from '../../components/ui/radio-group'
import { ErrorState, LoadingState } from '../../components/ui/states'
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
import { deploymentPartLabels, SHARED_SETUP_NOTE } from '../deployments/deployment-group-format'
import { DeploymentProviderLogo } from '../deployments/deployment-provider-logo'
import { GroupSetupFields, type GroupSetupField } from '../deployments/group-setup-fields'
import { HostSetupFields, type HostSetupField } from '../deployments/host-setup-fields'
import type { StackOptions } from '../stack/stack-api'
import type { WizardValues } from './schemas'
import { HOST_SKIP, hostField, hostFieldIds, hostGroupLinkFieldOf } from './wizard-host-fields'
import { partsOfHost, type WizardHost } from './wizard-hosts'

const skipOption = { value: HOST_SKIP, label: 'Set up later', description: 'Do it any time in Settings → Deployment.' }

const propOf: Record<HostSetupField, 'ConnectionId' | 'Mode' | 'Name' | 'Region' | 'Plan' | 'ResourceId'> = {
  connectionId: 'ConnectionId',
  mode: 'Mode',
  name: 'Name',
  region: 'Region',
  plan: 'Plan',
  resourceId: 'ResourceId',
}

type Caveat = StackOptions['caveats'][number]
type Part = ReturnType<typeof partsOfHost>[number]

/** The part a choice's caveat is about, shown on that part's window (FR-189); the rest show above the windows. */
const partOfCaveatLayer: Readonly<Record<string, Part>> = { frontendHost: 'frontend', backendHost: 'backend', databaseHost: 'database' }

const groupFieldNames: Record<GroupSetupField, keyof WizardValues> = {
  connectionId: 'hostGroupConnectionId',
  mode: 'hostGroupMode',
  name: 'hostGroupName',
  region: 'hostGroupRegion',
  plan: 'hostGroupPlan',
  resourceId: 'hostGroupResourceId',
  linkFrontend: hostGroupLinkFieldOf.frontend,
  linkBackend: hostGroupLinkFieldOf.backend,
  linkDatabase: hostGroupLinkFieldOf.database,
}

const fieldOf = (id: string) => hostFieldIds[id as keyof typeof hostFieldIds]

/** FR-188: one group's body: one connection and create or link for all its parts, or set up later. */
function GroupBody({
  host,
  control,
  register,
  errors,
  onAddConnection,
}: {
  host: Extract<WizardHost, { kind: 'group' }>
  control: Control<WizardValues>
  register: UseFormRegister<WizardValues>
  errors: FieldErrors<WizardValues>
  onAddConnection: () => void
}) {
  const mode = useWatch({ control, name: 'hostGroupMode' })
  const connectionId = useWatch({ control, name: 'hostGroupConnectionId' })
  const resourceId = useWatch({ control, name: 'hostGroupResourceId' })
  const entries = Object.entries(groupFieldNames) as [GroupSetupField, keyof WizardValues][]
  return (
    <GroupSetupFields
      group={host}
      ids={Object.fromEntries(entries.map(([field, name]) => [field, fieldOf(name)])) as Record<GroupSetupField, string>}
      register={(field) => register(groupFieldNames[field])}
      errors={Object.fromEntries(entries.map(([field, name]) => [field, errors[name]?.message]))}
      mode={mode}
      connectionId={connectionId}
      resourceId={resourceId}
      extraModeOptions={[skipOption]}
      modeFirst
      showDetails={mode !== HOST_SKIP}
      onAddConnection={onAddConnection}
    />
  )
}

/** FR-189: which of a window's parts deploy to their own project. A tick asks the server again for the windows and caveats. */
function SeparateParts({ parts, separate, onToggle }: { parts: readonly Part[]; separate: readonly string[]; onToggle: (part: Part, on: boolean) => void }) {
  return (
    <fieldset className="flex flex-col gap-1">
      <legend className="mb-1 text-sm font-medium">Deploy parts to separate projects</legend>
      {parts.map((part) => (
        <label key={part} className="flex min-h-10 items-center gap-3 text-sm">
          <input
            type="checkbox"
            id={`host-separate-${part}`}
            className="size-4 accent-primary"
            checked={separate.includes(part)}
            onChange={(e) => {
              onToggle(part, e.target.checked)
            }}
          />
          {deploymentPartLabels[part]}
        </label>
      ))}
    </fieldset>
  )
}

function CaveatList({ caveats }: { caveats: readonly string[] }) {
  if (caveats.length === 0) {
    return null
  }
  return (
    <Alert tone="warning" title="Before you continue">
      <ul aria-label="Consequences" className="list-disc pl-5">
        {caveats.map((text) => (
          <li key={text} data-testid="window-caveat">
            {text}
          </li>
        ))}
      </ul>
    </Alert>
  )
}

/** One window of the step: a group, or a target or database: set up later, create or link with an own connection, or an External string. */
function HostCard({
  host,
  control,
  register,
  errors,
  onAddConnection,
  caveats,
  canSeparate,
  separate,
  onToggleSeparate,
}: {
  host: WizardHost
  control: Control<WizardValues>
  register: UseFormRegister<WizardValues>
  errors: FieldErrors<WizardValues>
  onAddConnection: () => void
  caveats: readonly string[]
  canSeparate: boolean
  separate: readonly string[]
  onToggleSeparate: (part: Part, on: boolean) => void
}) {
  if (host.kind === 'group') {
    return (
      <WindowShell host={host} headingId={`${hostFieldIds.hostGroupMode}-title`} testId="wizard-host-group">
        {host.capability === 'shared_setup' && <p className="text-sm">{SHARED_SETUP_NOTE}</p>}
        {host.parts.includes('database') && (
          <p className="text-sm text-muted-foreground">This covers both the production and the preview database.</p>
        )}
        <GroupBody host={host} control={control} register={register} errors={errors} onAddConnection={onAddConnection} />
        <SeparateParts parts={host.parts} separate={separate} onToggle={onToggleSeparate} />
        <CaveatList caveats={caveats} />
      </WindowShell>
    )
  }
  return (
    <SingleHostCard
      host={host}
      control={control}
      register={register}
      errors={errors}
      onAddConnection={onAddConnection}
      caveats={caveats}
      canSeparate={canSeparate}
      separate={separate}
      onToggleSeparate={onToggleSeparate}
    />
  )
}

function WindowShell({ host, headingId, testId, children }: { host: WizardHost; headingId: string; testId: string; children: ReactNode }) {
  return (
    <section aria-labelledby={headingId} data-testid={testId} className={cn(panelClass, 'flex min-w-0 flex-col gap-4 p-4')}>
      <h3 id={headingId} className="flex items-center gap-2 text-lg">
        {host.kind !== 'database' && <DeploymentProviderLogo provider={host.provider} className="size-5" />}
        {host.title}
      </h3>
      {children}
    </section>
  )
}

function SingleHostCard({
  host,
  control,
  register,
  errors,
  onAddConnection,
  caveats,
  canSeparate,
  separate,
  onToggleSeparate,
}: {
  host: Exclude<WizardHost, { kind: 'group' }>
  control: Control<WizardValues>
  register: UseFormRegister<WizardValues>
  errors: FieldErrors<WizardValues>
  onAddConnection: () => void
  caveats: readonly string[]
  canSeparate: boolean
  separate: readonly string[]
  onToggleSeparate: (part: Part, on: boolean) => void
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
            hint="A postgres:// or postgresql:// URI, or an ADO.NET string for SQL Server. Tevrin stores it encrypted and never shows it again."
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
          {platformName} needs no extra connection: Tevrin uses the project&apos;s GitHub App installation (or its access token).
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
        connectionHint={database ? 'One of your own tokens. Tevrin uses it to provision, check and restore this database.' : 'One of your own tokens. Every deployment of this target uses it, whoever triggers it.'}
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
            description: database ? 'Tevrin provisions it and stores its connection details encrypted.' : "Linked to the project's repository, built as the stack says.",
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
    <WindowShell host={host} headingId={headingId} testId={`wizard-host-${database ? `database-${host.environment}` : host.component}`}>
      {database && host.previewInSettings && (
        <p className="text-sm text-muted-foreground">This is the production database. Add the preview database in Settings → Deployment once this exists.</p>
      )}
      {body}
      {canSeparate && <SeparateParts parts={partsOfHost(host)} separate={separate} onToggle={onToggleSeparate} />}
      <CaveatList caveats={caveats} />
    </WindowShell>
  )
}

/**
 * §7, FR-188 wizard step "Deployment" for a composed stack: the windows `POST /api/stack/options` lists, groups first, each
 * set up later (the default) or bound to one of the caller's own connections, with create or link. The setup runs right
 * after the project is created (`applyHostSetups`), through the same endpoints as Settings → Deployment.
 * FR-189: a part can leave its group; each caveat shows as text before the user moves on. The UI keeps no rule of its own.
 */
export function WizardHostsStep({
  hosts,
  control,
  register,
  errors,
  gitMode,
  onAddConnection,
  caveats,
  originNote,
  separate,
  onToggleSeparate,
  loading,
  loadError,
  onRetry,
}: {
  hosts: readonly WizardHost[]
  control: Control<WizardValues>
  register: UseFormRegister<WizardValues>
  errors: FieldErrors<WizardValues>
  gitMode: WizardValues['gitMode']
  onAddConnection: () => void
  caveats: readonly Caveat[]
  /** The origin model the last tick invalidated (R10), with the server's reason. */
  originNote: string | null
  separate: readonly string[]
  onToggleSeparate: (part: Part, on: boolean) => void
  loading: boolean
  loadError: unknown
  onRetry: () => void
}) {
  // A tick can replace the window it was in: focus follows the part to its new window, not back to the page.
  const toggled = useRef<string | null>(null)
  useEffect(() => {
    if (toggled.current && document.activeElement === document.body) {
      document.getElementById(`host-separate-${toggled.current}`)?.focus()
    }
  }, [hosts])
  const toggle = (part: Part, on: boolean) => {
    toggled.current = part
    onToggleSeparate(part, on)
  }
  if (loadError !== null) {
    return <ErrorState error={loadError} title="The deployment windows couldn't load" onRetry={onRetry} />
  }
  if (loading && hosts.length === 0) {
    return <LoadingState label="Loading the deployment windows…" />
  }
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
            With a Local repository, targets work only where Tevrin runs its test deployment provider. Elsewhere the project is still created, and Tevrin reports
            which setup it refused. Choose a hosted repository in the Repository step to deploy.
          </p>
        </Alert>
      )}
      <p role="status" className="sr-only">
        {`Deployment windows: ${hosts.map((h) => h.title).join('; ')}.`}
        {caveats.length > 0 ? ` ${caveats.map((c) => c.text).join(' ')}` : ''}
      </p>
      <CaveatList caveats={[...(originNote ? [originNote] : []), ...caveats.filter((c) => !(c.layer in partOfCaveatLayer)).map((c) => c.text)]} />
      {hosts.map((host) => {
        const parts = partsOfHost(host)
        const onPlatform = new Set(hosts.filter((h) => h.provider === host.provider).flatMap(partsOfHost))
        return (
          <HostCard
            key={host.slot}
            host={host}
            control={control}
            register={register}
            errors={errors}
            onAddConnection={onAddConnection}
            caveats={host.slot === 'DbPreview' ? [] : caveats.filter((c) => parts.some((part) => part === partOfCaveatLayer[c.layer])).map((c) => c.text)}
            canSeparate={onPlatform.size >= 2 && host.slot !== 'DbPreview'}
            separate={separate}
            onToggleSeparate={toggle}
          />
        )
      })}
      <section aria-labelledby="wizard-readiness-title" className="flex flex-col gap-2 text-sm">
        <h3 id="wizard-readiness-title" className="text-lg">
          Readiness preview
        </h3>
        <p className="text-muted-foreground">Once a target is set up, Tevrin checks these and shows the results, with fix links, in Settings → Deployment:</p>
        <ul aria-label="Readiness checks" className="list-disc pl-5">
          {Object.values(readinessItemLabels).map((label) => (
            <li key={label}>{label}</li>
          ))}
        </ul>
      </section>
    </>
  )
}
