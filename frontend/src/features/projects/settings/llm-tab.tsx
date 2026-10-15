import { useState } from 'react'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm, useWatch } from 'react-hook-form'
import { z } from 'zod'
import { toast } from 'sonner'

import type { Schemas } from '../../../api/client'
import { Alert, ErrorSummary } from '../../../components/ui/alert'
import { Button } from '../../../components/ui/button'
import { fieldErrorSummary } from '../../../components/ui/form-errors'
import { SelectField } from '../../../components/ui/select-field'
import { TextField } from '../../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError } from '../../../lib/errors'
import { agentKeys, agentLabels, llmProviderLabels } from '../labels'
import { useUpdateLlmSettings, type LlmSettings } from '../projects-api'
import { limits } from '../schemas'
import { SecretField, type SecretAction } from './secret-field'

type LlmProvider = Schemas['LlmProvider']

const model = z.string().max(limits.modelMax, { error: `Model ids are at most ${String(limits.modelMax)} characters.` })

const llmSchema = z.object({
  provider: z.enum(['anthropic', 'openai', 'ollama', 'fake']),
  defaultModel: model.refine((v) => v.trim() !== '', { error: 'Enter the default model id.' }),
  ideaRefinement: model,
  specGenerator: model,
  planGenerator: model,
  codeGenerator: model,
  codeReview: model,
  apiKey: z.string().max(limits.secretMax, { error: `The API key is at most ${String(limits.secretMax)} characters.` }),
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
  apiKey: 'llm-api-key',
} as const

const toValues = (s: LlmSettings): LlmValues => ({
  provider: s.provider,
  defaultModel: s.defaultModel,
  ideaRefinement: s.modelsByAgent.ideaRefinement ?? '',
  specGenerator: s.modelsByAgent.specGenerator ?? '',
  planGenerator: s.modelsByAgent.planGenerator ?? '',
  codeGenerator: s.modelsByAgent.codeGenerator ?? '',
  codeReview: s.modelsByAgent.codeReview ?? '',
  apiKey: '',
})

/** Settings → LLM (FR-9, FR-22). Providers without a system or project key show "not configured". */
export function LlmTab({ projectId, llm }: { projectId: string; llm: LlmSettings }) {
  const update = useUpdateLlmSettings(projectId)
  const [keyAction, setKeyAction] = useState<SecretAction>('keep')
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
  const apiKey = useWatch({ control, name: 'apiKey' })
  const keyEntered = apiKey.trim() !== '' && keyAction !== 'clear'
  const statusOf = (p: LlmProvider) => llm.providers.find((s) => s.provider === p)
  const isUsable = (p: LlmProvider) => statusOf(p)?.configured === true || keyEntered
  const providerField = register('provider')

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    if (!isUsable(v.provider) && !(llm.hasApiKey && keyAction !== 'clear')) {
      setError('provider', {
        type: 'manual',
        message: `${llmProviderLabels[v.provider]} isn't configured. Enter a project API key or choose another provider.`,
      })
      return
    }
    const modelsByAgent: Schemas['AgentModelsDto'] = {}
    for (const agent of agentKeys) {
      modelsByAgent[agent] = v[agent].trim() || null
    }
    const body: Schemas['UpdateLlmSettingsRequest'] = {
      provider: v.provider,
      defaultModel: v.defaultModel.trim(),
      modelsByAgent,
    }
    if (keyAction === 'clear') {
      body.clearApiKey = true
    } else if (v.apiKey && (keyAction === 'replace' || !llm.hasApiKey)) {
      body.apiKey = v.apiKey
    }
    update.mutate(body, {
      onSuccess: (next) => {
        reset(toValues(next))
        setKeyAction('keep')
        toast.success('LLM settings saved.')
      },
      onError: (error) => {
        const unmatched = applyFieldErrors(error, setError, ['provider', 'defaultModel', 'apiKey'])
        setServerErrors(unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error)])
      },
    })
  })

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
          hint="Providers marked “not configured” need a project API key (below) or a system key set by an admin."
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
            <option key={p.provider} value={p.provider} disabled={!isUsable(p.provider) && p.provider !== llm.provider}>
              {llmProviderLabels[p.provider]}
              {p.configured ? '' : ' (not configured)'}
            </option>
          ))}
        </SelectField>
        {current && !current.configured && (
          <Alert tone="warning">
            <p>
              {llmProviderLabels[current.provider]} isn&apos;t configured on this server.{' '}
              {keyEntered || (llm.hasApiKey && keyAction !== 'clear')
                ? 'The project API key will be used.'
                : 'Enter a project API key to use it.'}
            </p>
          </Alert>
        )}
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
        <SecretField
          id={fieldIds.apiKey}
          label="Project API key"
          isSet={llm.hasApiKey}
          action={keyAction}
          onActionChange={(a) => {
            setKeyAction(a)
            setValue('apiKey', '')
          }}
          hint="Used instead of the system key for this project. Stored encrypted; it's never shown again."
          maxLength={limits.secretMax}
          error={errors.apiKey?.message}
          inputProps={register('apiKey')}
        />
      </div>
      <div className="mt-6">
        <Button type="submit" pending={update.isPending} pendingLabel="Saving…">
          Save LLM settings
        </Button>
      </div>
    </form>
  )
}
