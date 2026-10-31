import { zodResolver } from '@hookform/resolvers/zod'
import { useMemo, useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'

import type { Schemas } from '../../api/client'
import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { RadioGroup } from '../../components/ui/radio-group'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextField } from '../../components/ui/text-field'
import { errorMessage, isApiError, stackIncompatibleMessage } from '../../lib/errors'
import { Link } from '../../lib/navigation'
import { useCurrentUser } from '../auth/auth-api'
import { connectionLabel, credentialStatusLabels } from '../connections/connection-format'
import { isPlatformProvider, usePlatformConnections, usePlatformResources } from '../connections/platform-connections-api'
import type { Project } from '../projects/projects-api'
import {
  databaseLinkIdLabels,
  databasePickerTypes,
  databasePlatformNames,
  databaseProviderLabel,
  databaseResourceNouns,
  environmentLabels,
} from './database-format'
import { usePutManagedDatabase, type DatabaseCard, type ManagedDatabaseProvider } from './deployments-api'

interface DatabaseValues {
  connectionId: string
  mode: 'keep' | 'create' | 'link'
  name: string
  region: string
  plan: string
  resourceId: string
  connectionString: string
}

/** Server VALIDATION keys → form fields. */
const serverFields: Record<string, keyof DatabaseValues> = {
  connectionid: 'connectionId',
  'create.name': 'name',
  'create.region': 'region',
  'create.plan': 'plan',
  'link.resourceid': 'resourceId',
  'external.connectionstring': 'connectionString',
  external: 'connectionString',
  create: 'mode',
  link: 'mode',
}

/** FR-152: a `postgres://` or `postgresql://` URI, or an ADO.NET string (`Key=value;…`) for SQL Server. */
const CONNECTION_STRING = /^(postgres(ql)?:\/\/\S+|[A-Za-z ]+=[^;]*(;[A-Za-z ]+=[^;]*)*;?)$/

const isExternal = (db: Pick<DatabaseCard, 'provider'>) => db.provider === 'external'

/** The form's rules, matching the API (FR-152): External needs a connection string; the others a connection and a mode. */
function databaseSchema(db: DatabaseCard) {
  const noun = databaseResourceNouns[db.provider] ?? 'database'
  const platform = databasePlatformNames[db.provider] ?? databaseProviderLabel(db.provider)
  return z
    .object({
      connectionId: z.string(),
      mode: z.enum(['keep', 'create', 'link']),
      name: z.string(),
      region: z.string(),
      plan: z.string(),
      resourceId: z.string(),
      connectionString: z.string(),
    })
    .superRefine((v, ctx) => {
      if (isExternal(db)) {
        const cs = v.connectionString.trim()
        if (!cs) {
          ctx.addIssue({ code: 'custom', path: ['connectionString'], message: 'Enter the connection string.' })
        } else if (!CONNECTION_STRING.test(cs)) {
          ctx.addIssue({
            code: 'custom',
            path: ['connectionString'],
            message: 'Enter a postgres:// or postgresql:// URI, or an ADO.NET connection string (Server=…;Database=…).',
          })
        }
        return
      }
      if (db.needsConnection && !v.connectionId) {
        ctx.addIssue({ code: 'custom', path: ['connectionId'], message: `Pick one of your ${platform} connections.` })
      }
      if (v.mode === 'create' && v.name.trim().length > 100) {
        ctx.addIssue({ code: 'custom', path: ['name'], message: 'Names are at most 100 characters.' })
      }
      if (v.mode === 'link' && !v.resourceId.trim()) {
        ctx.addIssue({ code: 'custom', path: ['resourceId'], message: `Pick the ${noun} to link.` })
      }
      if (v.mode === 'keep' && !db.resourceId) {
        ctx.addIssue({ code: 'custom', path: ['mode'], message: `Create or link a ${noun}.` })
      }
    })
}

function requestBody(db: DatabaseCard, v: DatabaseValues): Schemas['PutManagedDatabaseRequest'] {
  const provider = db.provider as ManagedDatabaseProvider
  if (isExternal(db)) {
    return { provider, external: { connectionString: v.connectionString.trim() } }
  }
  const trimmed = (s: string) => (s.trim() ? s.trim() : undefined)
  const create = { name: trimmed(v.name), region: trimmed(v.region), plan: trimmed(v.plan) }
  return {
    provider,
    ...(db.needsConnection ? { connectionId: v.connectionId } : {}),
    ...(v.mode === 'create'
      ? { create: Object.fromEntries(Object.entries(create).filter(([, value]) => value !== undefined)) }
      : v.mode === 'link'
        ? { link: { resourceId: v.resourceId.trim() } }
        : {}),
  }
}

function SetupError({ error }: { error: unknown }) {
  const text =
    error instanceof Error && !isApiError(error)
      ? error.message
      : isApiError(error, 'STACK_INCOMPATIBLE') && error.violations.length > 0
        ? stackIncompatibleMessage(error.violations)
        : errorMessage(error, 'databaseSetup')
  return (
    <Alert tone="error" role="alert" title="We couldn't set up the database">
      <p>{text}</p>
    </Alert>
  )
}

/**
 * FR-152 (Owner): bind one of the caller's connections and provision or link the database, or enter an External
 * connection string (a secret: a password field, sent once and never shown again).
 */
export function DatabaseSetupForm({ project, database: db, onDone }: { project: Project; database: DatabaseCard; onDone: () => void }) {
  const put = usePutManagedDatabase(project.id, db.environment)
  const me = useCurrentUser()
  const env = environmentLabels[db.environment]
  const providerName = databaseProviderLabel(db.provider)
  const platformName = databasePlatformNames[db.provider] ?? providerName
  const noun = databaseResourceNouns[db.provider] ?? 'database'
  const pickerType = databasePickerTypes[db.provider] ?? null
  const external = isExternal(db)
  const myBinding = db.binding !== null && db.binding.boundBy.id === me?.id
  const schema = useMemo(() => databaseSchema(db), [db])
  const [failure, setFailure] = useState<unknown>(null)
  const prefix = `db-${db.environment}`
  const ids = {
    connectionId: `${prefix}-connection`,
    mode: `${prefix}-mode`,
    name: `${prefix}-name`,
    region: `${prefix}-region`,
    plan: `${prefix}-plan`,
    resourceId: `${prefix}-resource`,
    connectionString: `${prefix}-connection-string`,
  } as const
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    control,
    formState: { errors, isSubmitted },
  } = useForm<DatabaseValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      connectionId: myBinding ? (db.binding?.connectionId ?? '') : '',
      mode: db.resourceId ? 'keep' : 'create',
      name: db.name ?? '',
      region: '',
      plan: '',
      resourceId: '',
      connectionString: '',
    },
  })
  const mode = useWatch({ control, name: 'mode' })
  const connectionId = useWatch({ control, name: 'connectionId' })
  const platform = isPlatformProvider(db.provider) ? db.provider : undefined
  const connections = usePlatformConnections({
    enabled: !external && db.needsConnection && platform !== undefined,
    ...(platform ? { provider: platform } : {}),
  })
  const own = connections.data ?? []
  const selected = own.find((c) => c.id === connectionId)
  const resources = usePlatformResources(mode === 'link' && connectionId && pickerType ? connectionId : null, pickerType)

  const onSubmit = handleSubmit((v) => {
    setFailure(null)
    put.mutate(requestBody(db, v), {
      onSuccess: () => {
        setValue('connectionString', '')
        toast.success(external ? `The ${db.environment} database is saved.` : `Setting up the ${db.environment} database on ${providerName}.`)
        onDone()
      },
      onError: (error) => {
        setValue('connectionString', '')
        if (isApiError(error, 'VALIDATION') && Object.keys(error.fieldErrors).length > 0) {
          const unmatched: string[] = []
          for (const [key, messages] of Object.entries(error.fieldErrors)) {
            const field = serverFields[key.toLowerCase()]
            if (field) {
              setError(field, { type: 'server', message: messages.join(' ') })
            } else {
              unmatched.push(messages.join(' '))
            }
          }
          setFailure(unmatched.length > 0 ? new Error(unmatched.join(' ')) : null)
          return
        }
        setFailure(error)
      },
    })
  })

  const modeOptions = [
    ...(db.resourceId
      ? [{ value: 'keep', label: `Keep ${db.name ?? db.resourceId}`, description: 'Only rebind it to the connection above.' }]
      : []),
    { value: 'create', label: `Create a new ${platformName} ${noun}`, description: 'AIDE provisions it and stores its connection details encrypted.' },
    { value: 'link', label: `Link an existing ${platformName} ${noun}`, description: 'One your connection can reach.' },
  ]

  return (
    <form
      noValidate
      aria-label={`Set up the ${db.environment} database on ${providerName}`}
      onSubmit={(e) => void onSubmit(e)}
      className="flex flex-col gap-4 rounded-lg border border-border/60 bg-background/40 p-4"
    >
      <ErrorSummary title="Check the database settings" errors={isSubmitted ? fieldErrorSummary(errors, ids) : []} />
      {failure !== null && <SetupError error={failure} />}
      {external ? (
        <TextField
          id={ids.connectionString}
          label={`${env} connection string`}
          hint="A postgres:// or postgresql:// URI, or an ADO.NET string for SQL Server. AIDE stores it encrypted and never shows it again."
          type="password"
          autoComplete="off"
          spellCheck={false}
          error={errors.connectionString?.message}
          {...register('connectionString')}
        />
      ) : (
        <>
          {db.needsConnection && (
            <div className="flex flex-col gap-2">
              {connections.isPending && <LoadingState label={`Loading your ${platformName} connections…`} />}
              {connections.isError && (
                <ErrorState error={connections.error} title={`Your ${platformName} connections couldn't load`} onRetry={() => void connections.refetch()} />
              )}
              {connections.isSuccess && (
                <SelectField
                  id={ids.connectionId}
                  label={`${platformName} connection`}
                  hint="Bind one of your own tokens. AIDE uses it to provision, check and restore this database."
                  error={errors.connectionId?.message}
                  {...register('connectionId')}
                >
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
              {selected?.status === 'invalid' && (
                <p className="text-sm text-destructive">{platformName} rejected this token; setting up with it will fail.</p>
              )}
              <p>
                <Link to="/connections" className="text-sm font-medium text-primary underline underline-offset-2">
                  Add a token in Connections
                </Link>
              </p>
            </div>
          )}
          <RadioGroup
            id={ids.mode}
            legend={`Which ${platformName} ${noun} is the ${db.environment} database?`}
            inputProps={register('mode')}
            error={errors.mode?.message}
            options={modeOptions}
          />
          {mode === 'create' && (
            <div className="grid gap-4 sm:grid-cols-3">
              <TextField
                id={ids.name}
                label="Name (optional)"
                hint="The project's name when empty."
                autoComplete="off"
                spellCheck={false}
                maxLength={100}
                className="sm:col-span-3"
                error={errors.name?.message}
                {...register('name')}
              />
              {db.provider !== 'railway' && (
                <TextField
                  id={ids.region}
                  label="Region (optional)"
                  hint={
                    db.provider === 'supabase'
                      ? 'A region code (e.g. eu-west-2), or americas, emea or apac.'
                      : `The backend's region when empty.`
                  }
                  autoComplete="off"
                  spellCheck={false}
                  maxLength={64}
                  error={errors.region?.message}
                  {...register('region')}
                />
              )}
              {db.provider === 'render' && (
                <TextField
                  id={ids.plan}
                  label="Plan (optional)"
                  hint="Render's default (free) when empty."
                  autoComplete="off"
                  spellCheck={false}
                  maxLength={64}
                  error={errors.plan?.message}
                  {...register('plan')}
                />
              )}
            </div>
          )}
          {mode === 'link' &&
            (pickerType === null ? (
              <TextField
                id={ids.resourceId}
                label={databaseLinkIdLabels[db.provider] ?? `${platformName} ${noun} id`}
                autoComplete="off"
                spellCheck={false}
                maxLength={200}
                error={errors.resourceId?.message}
                {...register('resourceId')}
              />
            ) : !connectionId ? (
              <p className="text-sm text-muted-foreground">Pick a connection first: the list shows what it can reach.</p>
            ) : resources.isPending ? (
              <LoadingState label={`Loading your ${platformName} ${noun}s…`} />
            ) : resources.isError ? (
              <ErrorState error={resources.error} title={`Your ${platformName} ${noun}s couldn't load`} onRetry={() => void resources.refetch()} />
            ) : (
              <SelectField id={ids.resourceId} label={`Existing ${noun}`} error={errors.resourceId?.message} {...register('resourceId')}>
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
      )}
      <div className="flex flex-wrap gap-2">
        <Button type="submit" pending={put.isPending} pendingLabel="Starting…">
          {external ? 'Save the connection string' : db.resourceId ? 'Save database' : `Set up ${providerName}`}
        </Button>
        {db.status !== 'not_configured' && (
          <Button variant="ghost" onClick={onDone} disabled={put.isPending}>
            Cancel
          </Button>
        )}
      </div>
    </form>
  )
}
