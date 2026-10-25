import type { UseQueryResult } from '@tanstack/react-query'
import { useState } from 'react'
import { Controller, type Control, type FieldErrors, type UseFormRegister } from 'react-hook-form'

import { Alert } from '../../components/ui/alert'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { Link } from '../../lib/navigation'
import { cn } from '../../lib/utils'
import { connectionLabel } from '../connections/connection-format'
import type { LlmProviderOption } from '../connections/connections-api'
import { agentKeys, agentLabels, llmProviderLabels } from './labels'
import { ModelPicker } from './model-picker'
import { wizardAgentModelFields, type WizardValues } from './schemas'
import { llmFieldIds, providerReasonText, showsConnectionPicker } from './wizard-llm'

/**
 * FR-159 wizard Configuration step, "AI model": the providers enabled on this server (ones the user can't use yet are
 * disabled, with the reason and "Add a connection"), the user's connection when one is needed, the default model from
 * the catalog (or any id), and per-agent overrides under a collapsed "Advanced".
 */
export function WizardLlmSection({
  providers,
  control,
  register,
  errors,
  provider,
  onChooseProvider,
  onAddConnection,
}: {
  providers: UseQueryResult<LlmProviderOption[]>
  control: Control<WizardValues>
  register: UseFormRegister<WizardValues>
  errors: FieldErrors<WizardValues>
  /** The chosen provider ('' = none yet). */
  provider: WizardValues['llmProvider']
  onChooseProvider: (option: LlmProviderOption) => void
  /** Saves the wizard before Connections opens (same tab). */
  onAddConnection: (provider: LlmProviderOption['provider']) => void
}) {
  const agentErrors = agentKeys.some((a) => errors[wizardAgentModelFields[a]])
  const [advancedOpen, setAdvancedOpen] = useState(false)

  return (
    <section aria-labelledby="project-llm-title" className="flex flex-col gap-4 rounded-lg border border-border/60 p-4">
      <h3 id="project-llm-title" className="font-semibold">
        AI model
      </h3>
      {providers.isPending && <LoadingState label="Loading the AI providers…" />}
      {providers.isError && (
        <ErrorState error={providers.error} title="The AI providers couldn't load" onRetry={() => void providers.refetch()} />
      )}
      {providers.isSuccess && (
        <LlmChoice
          options={providers.data}
          control={control}
          register={register}
          errors={errors}
          provider={provider}
          onChooseProvider={onChooseProvider}
          onAddConnection={onAddConnection}
        />
      )}
      {providers.isSuccess && providers.data.some((p) => p.provider === provider) && (
        <details
          open={advancedOpen || agentErrors}
          onToggle={(e) => {
            setAdvancedOpen(e.currentTarget.open)
          }}
          className="rounded-lg border border-border/60 p-3"
        >
          <summary className="cursor-pointer text-sm font-medium">Advanced: model per agent (optional)</summary>
          <div className="mt-3 flex flex-col gap-3">
            <p className="text-sm text-muted-foreground">Each agent uses the default model unless you pick another here.</p>
            {agentKeys.map((agent) => {
              const field = wizardAgentModelFields[agent]
              return (
                <Controller
                  key={`${provider}-${agent}`}
                  control={control}
                  name={field}
                  render={({ field: f }) => (
                    <ModelPicker
                      ref={f.ref}
                      id={llmFieldIds[field]}
                      label={agentLabels[agent]}
                      catalog={providers.data.find((p) => p.provider === provider)?.catalog ?? []}
                      value={f.value}
                      onChange={f.onChange}
                      onBlur={f.onBlur}
                      error={errors[field]?.message}
                      emptyLabel="Use the default model"
                    />
                  )}
                />
              )
            })}
          </div>
        </details>
      )}
    </section>
  )
}

function LlmChoice({
  options,
  control,
  register,
  errors,
  provider,
  onChooseProvider,
  onAddConnection,
}: {
  options: LlmProviderOption[]
  control: Control<WizardValues>
  register: UseFormRegister<WizardValues>
  errors: FieldErrors<WizardValues>
  provider: WizardValues['llmProvider']
  onChooseProvider: (option: LlmProviderOption) => void
  onAddConnection: (provider: LlmProviderOption['provider']) => void
}) {
  const chosen = options.find((p) => p.provider === provider)
  const firstUsable = options.find((p) => p.usable)
  const providerError = errors.llmProvider?.message
  const providerField = register('llmProvider', {
    onChange: (e: { target: { value: string } }) => {
      const option = options.find((p) => p.provider === e.target.value)
      if (option) {
        onChooseProvider(option)
      }
    },
  })

  if (options.length === 0) {
    return (
      <Alert tone="warning" title="No AI provider is turned on">
        <p>This server has no AI provider turned on. Ask an administrator to turn one on.</p>
      </Alert>
    )
  }

  return (
    <>
      <fieldset className="flex flex-col gap-2" aria-describedby={providerError ? `${llmFieldIds.llmProvider}-error` : undefined}>
        {/* The error-summary and focus target: the first usable radio, or the legend when none can be chosen. */}
        <legend
          id={firstUsable ? undefined : llmFieldIds.llmProvider}
          tabIndex={firstUsable ? undefined : -1}
          className="mb-1 text-sm font-medium focus:outline-none"
        >
          Provider
        </legend>
        {options.map((option) => {
          const inputId =
            option === firstUsable ? llmFieldIds.llmProvider : `${llmFieldIds.llmProvider}-${option.provider}`
          const reasonId = `${llmFieldIds.llmProvider}-${option.provider}-reason`
          const name = llmProviderLabels[option.provider]
          return (
            <div
              key={option.provider}
              className={cn(
                'flex items-start gap-3 rounded-lg border border-border/60 bg-background/40 p-3 transition-colors',
                option.usable ? 'has-checked:border-primary/60 has-checked:bg-primary/10' : 'opacity-90',
              )}
            >
              <input
                type="radio"
                id={inputId}
                value={option.provider}
                disabled={!option.usable}
                aria-describedby={reasonId}
                className="mt-1 size-4 accent-primary"
                {...providerField}
              />
              <div className="flex flex-col gap-1">
                <label htmlFor={inputId} className={cn('font-medium', !option.usable && 'text-muted-foreground')}>
                  {name}
                </label>
                <p id={reasonId} className="text-sm text-muted-foreground">
                  {providerReasonText(option)}
                </p>
                {!option.usable && (
                  <p>
                    <Link
                      to="/connections"
                      aria-describedby={reasonId}
                      className="text-sm text-primary underline"
                      onClick={() => {
                        onAddConnection(option.provider)
                      }}
                    >
                      Add a connection
                    </Link>
                  </p>
                )}
              </div>
            </div>
          )
        })}
        {providerError && (
          <p id={`${llmFieldIds.llmProvider}-error`} className="text-sm text-destructive">
            {providerError}
          </p>
        )}
      </fieldset>
      {!options.some((p) => p.usable) && (
        <Alert tone="info" title="Add a connection to choose a provider">
          <p>None of the AI providers on this server can be used with your account yet. Add an API key in Connections.</p>
        </Alert>
      )}

      {chosen && showsConnectionPicker(chosen) && (
        <SelectField
          id={llmFieldIds.llmCredentialId}
          label={`${llmProviderLabels[chosen.provider]} connection`}
          hint="Every run of this project uses this key, whoever starts the run."
          error={errors.llmCredentialId?.message}
          {...register('llmCredentialId')}
        >
          {chosen.connections.map((c) => (
            <option key={c.id} value={c.id}>
              {connectionLabel(c)}
            </option>
          ))}
        </SelectField>
      )}

      {chosen && (
        <Controller
          key={chosen.provider}
          control={control}
          name="llmDefaultModel"
          render={({ field }) => (
            <ModelPicker
              ref={field.ref}
              id={llmFieldIds.llmDefaultModel}
              label="Default model"
              hint={`Every agent uses it unless you pick another under Advanced. ${llmProviderLabels[chosen.provider]}'s system default is ${chosen.defaultModel}.`}
              catalog={chosen.catalog}
              value={field.value}
              onChange={field.onChange}
              onBlur={field.onBlur}
              error={errors.llmDefaultModel?.message}
            />
          )}
        />
      )}
    </>
  )
}
