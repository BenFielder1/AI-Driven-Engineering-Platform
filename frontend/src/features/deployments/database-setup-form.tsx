import { zodResolver } from '@hookform/resolvers/zod'
import { useMemo, useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'

import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { TextField } from '../../components/ui/text-field'
import { errorMessage, isApiError, stackIncompatibleMessage } from '../../lib/errors'
import { useCurrentUser } from '../auth/auth-api'
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
import { databaseRequestBody } from './host-setup-body'
import { HostSetupFields } from './host-setup-fields'

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

  const onSubmit = handleSubmit((v) => {
    setFailure(null)
    put.mutate(databaseRequestBody(db.provider as ManagedDatabaseProvider, db.needsConnection, v), {
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
        <HostSetupFields
          ids={ids}
          provider={db.provider}
          platformName={platformName}
          noun={noun}
          connectionsEnabled={db.needsConnection}
          connectionHint="Bind one of your own tokens. AIDE uses it to provision, check and restore this database."
          register={(field) => register(field)}
          errors={{
            connectionId: errors.connectionId?.message,
            mode: errors.mode?.message,
            name: errors.name?.message,
            region: errors.region?.message,
            plan: errors.plan?.message,
            resourceId: errors.resourceId?.message,
          }}
          mode={mode}
          connectionId={connectionId}
          modeLegend={`Which ${platformName} ${noun} is the ${db.environment} database?`}
          modeOptions={modeOptions}
          pickerType={pickerType}
          linkIdLabel={databaseLinkIdLabels[db.provider] ?? `${platformName} ${noun} id`}
          nameField={{ label: 'Name (optional)', hint: "The project's name when empty.", maxLength: 100 }}
          regionField={
            db.provider === 'railway'
              ? null
              : { hint: db.provider === 'supabase' ? 'A region code (e.g. eu-west-2), or americas, emea or apac.' : `The backend's region when empty.` }
          }
          planField={db.provider === 'render' ? { hint: "Render's default (free) when empty." } : null}
        />
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
