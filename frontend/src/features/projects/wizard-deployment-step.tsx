import { Link } from '../../lib/navigation'
import type { FieldErrors, UseFormRegister } from 'react-hook-form'

import { Alert } from '../../components/ui/alert'
import { RadioGroup } from '../../components/ui/radio-group'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextField } from '../../components/ui/text-field'
import { connectionLabel, credentialStatusLabels } from '../connections/connection-format'
import { useVercelConnections } from '../connections/vercel-connections-api'
import type { WizardValues } from './schemas'
import { deploymentFieldIds } from './wizard-deployment-ids'

/**
 * FR-88 wizard step "Deployment" (optional): skip, or Vercel with one of the caller's own connections and a new
 * (name, root directory) or existing Vercel project. Whether a Local repository can deploy is the server's call.
 */
export function WizardDeploymentStep({
  register,
  errors,
  values,
}: {
  register: UseFormRegister<WizardValues>
  errors: FieldErrors<WizardValues>
  values: Pick<WizardValues, 'gitMode' | 'stackKind' | 'stackFrontendHost' | 'deploymentMode' | 'vercelProjectMode' | 'vercelConnectionId'>
}) {
  const vercel = values.deploymentMode === 'vercel'
  const connections = useVercelConnections(vercel)
  // FR-114: the composed stack's frontend host is Vercel.
  const stackDeploysToVercel = values.stackKind === 'compose' && values.stackFrontendHost === 'deploy-vercel'

  const own = connections.data ?? []
  const selected = own.find((c) => c.id === values.vercelConnectionId)
  return (
    <>
      <RadioGroup
        id={deploymentFieldIds.deploymentMode}
        legend="Deploy this project?"
        inputProps={register('deploymentMode')}
        error={errors.deploymentMode?.message}
        options={[
          { value: 'none', label: 'Skip for now', description: 'You can set up deployment later in Settings → Deployment.' },
          {
            value: 'vercel',
            label: 'Vercel',
            description: stackDeploysToVercel
              ? 'The stack you chose deploys the frontend to Vercel: a preview of every run and production on every release.'
              : 'A preview of every run and production on every release (for a Next.js app).',
          },
        ]}
      />
      {vercel && values.gitMode === 'local' && (
        <Alert tone="info" title="Vercel deploys from GitHub">
          <p>
            With a Local repository, deployment works only where AIDE runs its test deployment provider; otherwise creating the
            project reports it. Choose a GitHub repository in the Repository step to deploy to Vercel.
          </p>
        </Alert>
      )}
      {vercel && (
        <>
          {connections.isPending && <LoadingState label="Loading your Vercel connections…" />}
          {connections.isError && (
            <ErrorState error={connections.error} title="Your Vercel connections couldn't load" onRetry={() => void connections.refetch()} />
          )}
          {connections.isSuccess && own.length === 0 ? (
            <Alert tone="info" title="Add a Vercel token first">
              <p>
                You have no Vercel connections yet. Add a token under{' '}
                <Link to="/connections" className="font-medium text-primary underline underline-offset-2">
                  Connections → Vercel
                </Link>{' '}
                (what you entered here isn&apos;t kept), or skip deployment and set it up later in Settings → Deployment.
              </p>
              {errors.vercelConnectionId?.message && <p className="mt-2 text-destructive">{errors.vercelConnectionId.message}</p>}
            </Alert>
          ) : (
            connections.isSuccess && (
              <SelectField
                id={deploymentFieldIds.vercelConnectionId}
                label="Vercel connection"
                hint="One of your own tokens. Every deployment of this project uses it, whoever triggers it."
                error={errors.vercelConnectionId?.message}
                {...register('vercelConnectionId')}
              >
                <option value="">Pick a connection</option>
                {own.map((c) => (
                  <option key={c.id} value={c.id}>
                    {connectionLabel(c)}
                    {c.teamSlug ? ` · team ${c.teamSlug}` : ''}
                    {c.status === 'valid' ? '' : ` (${credentialStatusLabels[c.status].toLowerCase()})`}
                  </option>
                ))}
              </SelectField>
            )
          )}
          {selected?.status === 'invalid' && (
            <p className="text-sm text-destructive">Vercel rejected this token; deployments with it will fail.</p>
          )}
          <RadioGroup
            id={deploymentFieldIds.vercelProjectMode}
            legend="Vercel project"
            inputProps={register('vercelProjectMode')}
            error={errors.vercelProjectMode?.message}
            options={[
              { value: 'create', label: 'Create a new Vercel project', description: 'Linked to the repository, framework Next.js.' },
              { value: 'link', label: 'Link an existing Vercel project', description: 'One the connection can access, by id or name.' },
            ]}
          />
          {values.vercelProjectMode === 'create' ? (
            <div className="grid gap-5 sm:grid-cols-2">
              <TextField
                id={deploymentFieldIds.vercelProjectName}
                label="Vercel project name"
                hint="Lowercase letters, digits, '.', '_' and '-'."
                autoComplete="off"
                spellCheck={false}
                maxLength={100}
                error={errors.vercelProjectName?.message}
                {...register('vercelProjectName')}
              />
              <TextField
                id={deploymentFieldIds.vercelRootDirectory}
                label="Root directory (optional)"
                hint="The app's directory in the repository. Empty: the repository root."
                autoComplete="off"
                spellCheck={false}
                maxLength={255}
                error={errors.vercelRootDirectory?.message}
                {...register('vercelRootDirectory')}
              />
            </div>
          ) : (
            <TextField
              id={deploymentFieldIds.vercelProjectId}
              label="Vercel project id or name"
              hint="For example prj_abc123 or my-app, from the project's settings in Vercel."
              autoComplete="off"
              spellCheck={false}
              maxLength={100}
              error={errors.vercelProjectId?.message}
              {...register('vercelProjectId')}
            />
          )}
        </>
      )}
    </>
  )
}
