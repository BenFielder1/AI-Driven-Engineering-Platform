import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'

import type { Schemas } from '../../../api/client'
import { ErrorSummary } from '../../../components/ui/alert'
import { Button } from '../../../components/ui/button'
import { fieldErrorSummary } from '../../../components/ui/form-errors'
import { RadioGroup } from '../../../components/ui/radio-group'
import { TextField } from '../../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError } from '../../../lib/errors'
import { gitHostTerms } from '../../../lib/git-host'
import { mergeStrategiesFor, mergeStrategyDescription, mergeStrategyLabel } from '../labels'
import { useProjectState, useUpdateGitSettings, type GitSettings } from '../projects-api'
import { checkTagPrefix, TAG_PREFIX_MAX, tagPrefixLockReason } from './release-settings-rules'

const releaseSchema = z
  .object({ mergeStrategy: z.enum(['aide_merges', 'manual', 'fast_forward', 'squash']), tagPrefix: z.string().optional() })
  .superRefine((v, ctx) => {
    // A disabled (locked) field may read as undefined.
    const problem = checkTagPrefix(v.tagPrefix ?? '')
    if (problem) {
      ctx.addIssue({ code: 'custom', path: ['tagPrefix'], message: problem })
    }
  })

type ReleaseValues = z.infer<typeof releaseSchema>

const fieldIds = { mergeStrategy: 'release-merge-strategy', tagPrefix: 'release-tag-prefix' } as const

/** FR-65, FR-140: the strategies the project's Git host offers (Bitbucket adds fast-forward and squash), worded for it. */
const strategyOptions = (git: GitSettings) => {
  const host = gitHostTerms(git.mode)
  return mergeStrategiesFor(git.mode).map((value) => ({
    value,
    label: mergeStrategyLabel(value, host),
    description: mergeStrategyDescription(value, host),
  }))
}

/** M28: a strategy this host doesn't offer (Bitbucket's squash or fast-forward held after a host change) can't be sent back. */
const heldUnavailable = (g: GitSettings) => !mergeStrategiesFor(g.mode).includes(g.mergeStrategy)

const toValues = (g: GitSettings): ReleaseValues => ({
  mergeStrategy: heldUnavailable(g) ? 'aide_merges' : g.mergeStrategy,
  tagPrefix: g.tagPrefix,
})

/**
 * Settings → Git → Release (FR-64, FR-65, §4.10; Owner): the merge strategy and the tag prefix, with a live preview of
 * the tag. The prefix is locked (with the reason) while a release is in progress or awaiting its merge.
 */
export function ReleaseSettingsForm({ projectId, git }: { projectId: string; git: GitSettings }) {
  const update = useUpdateGitSettings(projectId)
  const state = useProjectState(projectId)
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    reset,
    control,
    formState: { errors, isSubmitted },
  } = useForm<ReleaseValues>({ resolver: zodResolver(releaseSchema), defaultValues: toValues(git), mode: 'onChange' })

  const tagPrefix = useWatch({ control, name: 'tagPrefix' })
  const lockReason = tagPrefixLockReason(state.data, gitHostTerms(git.mode))
  const sampleVersion = state.data?.developmentVersion ?? state.data?.suggestedNextVersion ?? '1.2.0'

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    const body: Schemas['UpdateGitSettingsRequest'] = { mergeStrategy: v.mergeStrategy }
    if (!lockReason && v.tagPrefix !== undefined && v.tagPrefix !== git.tagPrefix) {
      body.tagPrefix = v.tagPrefix
    }
    update.mutate(body, {
      onSuccess: (next) => {
        reset(toValues(next))
        toast.success('Release settings saved.')
      },
      onError: (error) => {
        const unmatched = applyFieldErrors(error, setError, ['mergeStrategy', 'tagPrefix'])
        setServerErrors(
          unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error, 'releaseSettings')],
        )
        if (isApiError(error, 'CONFLICT')) {
          void state.refetch()
        }
      },
    })
  })

  return (
    <form noValidate onSubmit={(e) => void onSubmit(e)} aria-labelledby="release-settings-title">
      <h2 id="release-settings-title" className="mb-1 text-xl">
        Release
      </h2>
      <p className="mb-4 text-sm text-muted-foreground">How the version&apos;s pull request is merged, and how its tag is named.</p>
      <ErrorSummary
        title="We couldn't save the release settings"
        errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, fieldIds) : [])]}
      />
      <div className="mt-2 flex flex-col gap-5">
        {heldUnavailable(git) && (
          <p data-testid="merge-strategy-unavailable" className="text-sm text-muted-foreground">
            This project is set to &ldquo;{mergeStrategyLabel(git.mergeStrategy, gitHostTerms(git.mode))}&rdquo;, which{' '}
            {gitHostTerms(git.mode).host} doesn&apos;t offer. Pick a strategy below and save to replace it.
          </p>
        )}
        <RadioGroup
          legend="Merge strategy"
          id={fieldIds.mergeStrategy}
          options={strategyOptions(git)}
          inputProps={register('mergeStrategy')}
          error={errors.mergeStrategy?.message}
        />
        <div className="flex flex-col gap-1">
          <TextField
            id={fieldIds.tagPrefix}
            label="Tag prefix"
            hint={lockReason ?? 'Up to 20 letters, digits, ".", "_", "/" or "-". Leave it empty for bare version tags.'}
            autoComplete="off"
            spellCheck={false}
            maxLength={TAG_PREFIX_MAX + 5}
            disabled={lockReason !== null}
            error={errors.tagPrefix?.message}
            {...register('tagPrefix')}
          />
          <p aria-live="polite" data-testid="tag-preview" className="text-sm text-muted-foreground">
            {errors.tagPrefix ? null : (
              <>
                Tags will look like <code className="font-mono text-foreground">{`${tagPrefix ?? git.tagPrefix}${sampleVersion}`}</code>
              </>
            )}
          </p>
        </div>
      </div>
      <div className="mt-6">
        <Button type="submit" pending={update.isPending} pendingLabel="Saving…">
          Save release settings
        </Button>
      </div>
    </form>
  )
}
