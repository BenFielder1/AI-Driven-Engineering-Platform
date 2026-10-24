import { zodResolver } from '@hookform/resolvers/zod'
import { Link } from '../../../lib/navigation'
import { useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'

import type { Schemas } from '../../../api/client'
import { Alert, ErrorSummary } from '../../../components/ui/alert'
import { Button } from '../../../components/ui/button'
import { buttonClasses } from '../../../components/ui/button-classes'
import { fieldErrorSummary } from '../../../components/ui/form-errors'
import { SelectField } from '../../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../../components/ui/states'
import { TextField } from '../../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError } from '../../../lib/errors'
import { bindingSummary, connectionLabel, credentialStatusLabels } from '../../connections/connection-format'
import { keyedProviders, useLlmConnections, type LlmCredential } from '../../connections/connections-api'
import { CredentialStatusBadge } from '../../connections/credential-status-badge'
import { agentKeys, agentLabels, llmProviderLabels } from '../labels'
import { useLlmSettings, useUpdateLlmSettings, type LlmSettings } from '../projects-api'
import { limits } from '../schemas'

type LlmProvider = Schemas['LlmProvider']
type Binding = Schemas['LlmBindingDto']

const isKeyed = (p: LlmProvider) => keyedProviders.some((k) => k === p)

const model = z.string().max(limits.modelMax, { error: `Model ids are at most ${String(limits.modelMax)} characters.` })

const llmSchema = z.object({
  provider: z.enum(['anthropic', 'openai', 'ollama', 'fake']),
  defaultModel: model.refine((v) => v.trim() !== '', { error: 'Enter the default model id.' }),
  ideaRefinement: model,
  specGenerator: model,
  planGenerator: model,
  codeGenerator: model,
  codeReview: model,
  /** The bound connection's id, or '' for none. */
  credentialId: z.string(),
})

type LlmValues = z.infer<typeof llmSchema>

const fieldIds = {
  provider: 'llm-provider',
  defaultModel: 'llm-default-model',
  ideaRefinement: 'llm-model-ideaRefinement',
  specGenerator: 'llm-model-specGenerator',
  planGenerator: 'llm-model-planGenerator',
  codeGenerator: 'llm-model-codeGenerator',
  codeReview: 'llm-model-codeReview',
  credentialId: 'llm-connection',
} as const

const toValues = (s: LlmSettings): LlmValues => ({
  provider: s.provider,
  defaultModel: s.defaultModel,
  ideaRefinement: s.modelsByAgent.ideaRefinement ?? '',
  specGenerator: s.modelsByAgent.specGenerator ?? '',
  planGenerator: s.modelsByAgent.planGenerator ?? '',
  codeGenerator: s.modelsByAgent.codeGenerator ?? '',
  codeReview: s.modelsByAgent.codeReview ?? '',
  credentialId: s.binding?.credentialId ?? '',
})

/** The bound connection as every member sees it: "Anthropic · connected by Ana (••••a1b2)", its label and status. */
function BindingSummary({ binding }: { binding: Binding }) {
  return (
    <div className="flex flex-col gap-1 text-sm" data-testid="llm-binding">
      <span className="flex flex-wrap items-center gap-2">
        <span className="font-medium">{bindingSummary(binding)}</span>
        <CredentialStatusBadge status={binding.status} />
      </span>
      <span className="text-muted-foreground">Connection “{binding.label}”</span>
    </div>
  )
}

/** Warnings about the key runs would use for `provider` (FR-79): an invalid bound key, or none at all. */
function KeyWarning({ llm, provider, binding }: { llm: LlmSettings; provider: LlmProvider; binding: Binding | null }) {
  if (!isKeyed(provider)) {
    return null
  }
  const name = llmProviderLabels[provider]
  if (binding?.provider === provider) {
    return binding.status === 'invalid' ? (
      <Alert tone="warning" title="The bound key is invalid">
        <p>
          {name} rejected the bound connection&apos;s key, so runs fail. Its owner can add a new key under Connections and an
          Owner can bind it here.
        </p>
      </Alert>
    ) : null
  }
  const systemKey = llm.providers.find((p) => p.provider === provider)?.configured === true
  return systemKey ? (
    <Alert tone="info">
      <p>No connection is bound, so runs use the server&apos;s {name} system key.</p>
    </Alert>
  ) : (
    <Alert tone="warning" title="No LLM key for this project">
      <p>
        No {name} connection is bound, so agent runs fail (PROVIDER_NOT_CONFIGURED) unless an admin allows system keys. A
        project Owner can bind one of their own connections here.
      </p>
    </Alert>
  )
}

/** Read-only LLM settings for members who can't change them (FR-79: they can't bind or unbind). */
function LlmReadOnly({ llm }: { llm: LlmSettings }) {
  return (
    <section aria-labelledby="llm-title" className="flex max-w-2xl flex-col gap-4">
      <h2 id="llm-title" className="text-xl">
        LLM
      </h2>
      <p className="text-sm text-muted-foreground">Only project Owners can change the LLM settings or the bound connection.</p>
      <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 text-sm">
        <dt className="font-medium">Provider</dt>
        <dd>{llmProviderLabels[llm.provider]}</dd>
        <dt className="font-medium">Default model</dt>
        <dd className="font-mono">{llm.defaultModel}</dd>
        {agentKeys.map((agent) => {
          const m = llm.modelsByAgent[agent]
          return m ? (
            <div key={agent} className="contents">
              <dt className="font-medium">{agentLabels[agent]}</dt>
              <dd className="font-mono">{m}</dd>
            </div>
          ) : null
        })}
      </dl>
      <div className="flex flex-col gap-2">
        <h3 className="text-sm font-semibold">Connection</h3>
        {llm.binding ? <BindingSummary binding={llm.binding} /> : !isKeyed(llm.provider) && <p className="text-sm">{llmProviderLabels[llm.provider]} needs no key.</p>}
        <KeyWarning llm={llm} provider={llm.provider} binding={llm.binding} />
      </div>
    </section>
  )
}

function LlmForm({ projectId, llm, canBind }: { projectId: string; llm: LlmSettings; canBind: boolean }) {
  const update = useUpdateLlmSettings(projectId)
  const unbind = useUpdateLlmSettings(projectId)
  const connections = useLlmConnections(canBind)
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    getValues,
    reset,
    control,
    formState: { errors, isSubmitted },
  } = useForm<LlmValues>({ resolver: zodResolver(llmSchema), defaultValues: toValues(llm) })

  const provider = useWatch({ control, name: 'provider' })
  const credentialId = useWatch({ control, name: 'credentialId' })
  const statusOf = (p: LlmProvider) => llm.providers.find((s) => s.provider === p)
  const providerField = register('provider')
  const binding = llm.binding

  // The picker offers the caller's own connections for the chosen provider, plus the current binding (possibly
  // another Owner's) so keeping it doesn't need a change.
  const own: LlmCredential[] = (connections.data?.credentials ?? []).filter((c) => c.provider === provider)
  const showCurrent = binding !== null && binding.provider === provider && !own.some((c) => c.id === binding.credentialId)
  const optionIds = new Set([...own.map((c) => c.id), ...(showCurrent ? [binding.credentialId] : [])])
  const effectiveCredential = optionIds.has(credentialId) ? credentialId : ''
  const selected = own.find((c) => c.id === effectiveCredential)

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    const modelsByAgent: Schemas['AgentModelsDto'] = {}
    for (const agent of agentKeys) {
      modelsByAgent[agent] = v[agent].trim() || null
    }
    const body: Schemas['UpdateLlmSettingsRequest'] = {
      provider: v.provider,
      defaultModel: v.defaultModel.trim(),
      modelsByAgent,
    }
    const initial = binding?.credentialId ?? ''
    const next = isKeyed(v.provider) && optionIds.has(v.credentialId) ? v.credentialId : ''
    if (canBind && next !== initial) {
      body.credentialId = next === '' ? null : next
    }
    update.mutate(body, {
      onSuccess: (saved) => {
        reset(toValues(saved))
        toast.success('LLM settings saved.')
      },
      onError: (error) => {
        if (isApiError(error, 'CREDENTIAL_NOT_OWNER') || isApiError(error, 'NOT_FOUND')) {
          setError('credentialId', { type: 'server', message: errorMessage(error, 'llmBinding') })
          return
        }
        const unmatched = applyFieldErrors(error, setError, ['provider', 'defaultModel', 'credentialId'])
        setServerErrors(
          unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error, 'llmBinding')],
        )
      },
    })
  })

  const doUnbind = () => {
    setServerErrors([])
    unbind.mutate(
      { credentialId: null },
      {
        onSuccess: (saved) => {
          reset({ ...getValues(), credentialId: saved.binding?.credentialId ?? '' })
          toast.success('Connection unbound.')
        },
        onError: (error) => {
          setServerErrors([errorMessage(error, 'llmBinding')])
        },
      },
    )
  }

  const current = statusOf(provider)

  return (
    <form noValidate onSubmit={(e) => void onSubmit(e)} aria-labelledby="llm-title" className="max-w-2xl">
      <h2 id="llm-title" className="mb-4 text-xl">
        LLM
      </h2>
      <ErrorSummary
        title="We couldn't save the LLM settings"
        errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, fieldIds) : [])]}
      />
      <div className="mt-2 flex flex-col gap-5">
        <SelectField
          id={fieldIds.provider}
          label="Provider"
          hint="Anthropic and OpenAI use the connection bound below. Ollama and Fake need no key."
          error={errors.provider?.message}
          {...providerField}
          onChange={(e) => {
            const previous = statusOf(getValues('provider'))
            void providerField.onChange(e)
            const next = statusOf(getValues('provider'))
            const modelNow = getValues('defaultModel').trim()
            if (next && (modelNow === '' || modelNow === previous?.defaultModel)) {
              setValue('defaultModel', next.defaultModel)
            }
          }}
        >
          {llm.providers.map((p) => (
            <option key={p.provider} value={p.provider}>
              {llmProviderLabels[p.provider]}
            </option>
          ))}
        </SelectField>

        <fieldset className="flex flex-col gap-3 rounded-lg border border-border/60 p-4">
          <legend className="px-1 text-sm font-medium">Connection</legend>
          {binding && <BindingSummary binding={binding} />}
          {!isKeyed(provider) ? (
            <p className="text-sm text-muted-foreground">{llmProviderLabels[provider]} needs no key.</p>
          ) : canBind ? (
            <>
              {connections.isPending && <LoadingState label="Loading your connections…" />}
              {connections.isError && (
                <ErrorState error={connections.error} title="Your connections couldn't load" onRetry={() => void connections.refetch()} />
              )}
              {connections.isSuccess && (
                <SelectField
                  id={fieldIds.credentialId}
                  label={`${llmProviderLabels[provider]} connection`}
                  hint="Bind one of your own keys. Every run of this project uses it, whoever starts the run."
                  error={errors.credentialId?.message}
                  value={effectiveCredential}
                  {...register('credentialId')}
                >
                  <option value="">No connection</option>
                  {showCurrent && (
                    <option value={binding.credentialId}>
                      {connectionLabel(binding)} · connected by {binding.boundBy.name}
                    </option>
                  )}
                  {own.map((c) => (
                    <option key={c.id} value={c.id}>
                      {connectionLabel(c)}
                      {c.status === 'valid' ? '' : ` (${credentialStatusLabels[c.status].toLowerCase()})`}
                    </option>
                  ))}
                </SelectField>
              )}
              {connections.isSuccess && own.length === 0 && (
                <p className="text-sm text-muted-foreground">You have no {llmProviderLabels[provider]} keys yet.</p>
              )}
              {selected?.status === 'invalid' && (
                <p className="text-sm text-destructive">The provider rejected this key; runs with it will fail.</p>
              )}
              <div className="flex flex-wrap items-center gap-2">
                <Link to="/connections" className={buttonClasses('secondary')}>
                  Add a key in Connections
                </Link>
                {binding && (
                  <Button variant="ghost" pending={unbind.isPending} pendingLabel="Unbinding…" onClick={doUnbind}>
                    Unbind
                  </Button>
                )}
              </div>
            </>
          ) : (
            <p className="text-sm text-muted-foreground">Only project Owners can bind or unbind a connection.</p>
          )}
          {isKeyed(provider) && <KeyWarning llm={llm} provider={provider} binding={binding} />}
        </fieldset>

        <TextField
          id={fieldIds.defaultModel}
          label="Default model"
          hint={current ? `The system default for ${llmProviderLabels[current.provider]} is ${current.defaultModel}.` : undefined}
          maxLength={limits.modelMax}
          autoComplete="off"
          error={errors.defaultModel?.message}
          {...register('defaultModel')}
        />
        <fieldset className="flex flex-col gap-3 rounded-lg border border-border/60 p-4">
          <legend className="px-1 text-sm font-medium">Model per agent (optional)</legend>
          <p className="text-sm text-muted-foreground">Leave a field empty to use the default model.</p>
          {agentKeys.map((agent) => (
            <TextField
              key={agent}
              id={fieldIds[agent]}
              label={agentLabels[agent]}
              maxLength={limits.modelMax}
              autoComplete="off"
              error={errors[agent]?.message}
              {...register(agent)}
            />
          ))}
        </fieldset>
      </div>
      <div className="mt-6">
        <Button type="submit" pending={update.isPending} pendingLabel="Saving…">
          Save LLM settings
        </Button>
      </div>
    </form>
  )
}

/**
 * Settings → LLM (FR-9, FR-22, FR-79). Every member can read it (GET /settings/llm). Owners pick the provider and
 * models and bind one of their own connections; everyone else sees "Anthropic · connected by Ana (••••a1b2)".
 * `canEdit` is the changeSettings capability (Owners and Admins); binding additionally needs the API's `canBind`.
 */
export function LlmTab({ projectId, canEdit }: { projectId: string; canEdit: boolean }) {
  const settings = useLlmSettings(projectId)
  if (settings.isPending) {
    return <LoadingState label="Loading the LLM settings…" />
  }
  if (settings.isError) {
    return <ErrorState error={settings.error} onRetry={() => void settings.refetch()} title="The LLM settings couldn't load" />
  }
  const llm = settings.data
  return canEdit || llm.canBind ? (
    <LlmForm projectId={projectId} llm={llm} canBind={llm.canBind} />
  ) : (
    <LlmReadOnly llm={llm} />
  )
}
