import { useCallback, useRef, useState, type ReactNode } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Badge } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage, errorMessages, errorReasons, isApiError, type ErrorCode } from '../../lib/errors'
import { Link, useNavigate } from '../../lib/navigation'
import { useDocumentTitle } from '../../lib/use-document-title'
import { useAdvancePhase } from '../lifecycle/lifecycle-api'
import { phaseLabels } from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { RelativeTime } from '../projects/relative-time'
import { RunStatusBadge } from '../runs/run-badges'
import { describeRunError, runWarningText } from '../runs/run-format'
import { useStackCatalog, type StackCatalog } from '../stack/stack-api'
import { optionName } from '../stack/stack-model'
import { StageArtifact } from '../stages/stage-artifact'
import { StageHeader } from '../stages/stage-header'
import { StageRuns } from '../stages/stage-runs'
import { StageSection } from '../stages/stage-section'
import { AnalysisDecisionDialog, type ProfileDecision } from './analysis-decision-dialog'
import { AnalysisDeploymentSetup } from './analysis-deployment'
import { isAnalysisRunPending, useAnalysis, useRunStackAnalysis, useSaveStackProfile, type Analysis } from './analysis-api'
import { CodebaseSummarySection } from './codebase-summary-section'
import { NeedsInputSection } from './needs-input'
import { HOSTS_WAIT_REASON, ProfileEditor } from './profile-editor'
import { HostPickers } from './profile-pickers'
import {
  confidenceLabels,
  hasUnsupported,
  hostsReady,
  layerStatusLabels,
  profileLayerLabels,
  profileLayers,
  structureRoleLabels,
  withHost,
  type StackProfileDocument,
} from './stack-profile-format'
import { useProfileOptions } from './use-profile-options'

const triggerLabels: Readonly<Record<string, string>> = {
  phase_entered: 'Started after the Repository Analyst',
  user_requested: 'Started from this page',
  artifact_rejected: 'Started after the profile was rejected',
  artifact_corrupted: 'Started after the profile failed its integrity check',
}

const runLink = (projectId: string, runId: string | null, label: string) =>
  runId ? (
    <Link to="/projects/$projectId/runs" params={{ projectId }} search={{ run: runId }} className="text-sm text-primary underline">
      {label}
    </Link>
  ) : undefined

/** FR-180, FR-182: the Stack Analyst's latest run, live (queued, running), its warnings, or why it failed with Retry and Fill in manually. */
function RunSection({
  analysis,
  canRun,
  onFillIn,
}: {
  analysis: Analysis
  canRun: boolean
  onFillIn: (() => void) | null
}) {
  const { project } = useProjectAccess()
  const run = analysis.run
  const retry = useRunStackAnalysis(project.id)
  let body: ReactNode
  if (!run) {
    body = (
      <p className="text-sm text-muted-foreground">
        The Stack Analyst hasn&apos;t started yet. It starts when the Repository Analyst has summarised the code.
      </p>
    )
  } else {
    const failure = run.status === 'failed' ? (run.error ? describeRunError(run.error) : null) : null
    const code = run.errorCode && Object.hasOwn(errorMessages, run.errorCode) ? errorMessages[run.errorCode as ErrorCode] : null
    body = (
      <>
        <dl className="flex flex-wrap gap-x-6 gap-y-2 text-sm">
          <div className="flex items-center gap-2">
            <dt className="text-muted-foreground">Status</dt>
            <dd>
              <RunStatusBadge status={run.status} />
            </dd>
          </div>
          <div className="flex items-center gap-2">
            <dt className="text-muted-foreground">Started</dt>
            <dd>
              <RelativeTime iso={run.createdAt} />
            </dd>
          </div>
          <div className="flex items-center gap-2">
            <dt className="text-muted-foreground">Trigger</dt>
            <dd>{triggerLabels[run.trigger] ?? run.trigger}</dd>
          </div>
          {run.artifactVersion !== null && (
            <div className="flex items-center gap-2">
              <dt className="text-muted-foreground">Draft</dt>
              <dd>v{run.artifactVersion}</dd>
            </div>
          )}
        </dl>
        {isAnalysisRunPending(run) && (
          <p role="status" aria-busy="true" className="rounded-lg border border-border/60 bg-muted/60 p-3 text-sm">
            <span aria-hidden="true" className="mr-2 inline-block size-3 animate-pulse rounded-full bg-primary align-middle" />
            The Stack Analyst is {run.status === 'queued' ? 'about to start' : 'reading the repository'}. Its findings appear
            here when it&apos;s done.
          </p>
        )}
        {run.warnings.length > 0 && (
          <ul aria-label="Run warnings" className="list-disc pl-5 text-sm">
            {run.warnings.map((w) => (
              <li key={w}>{runWarningText(w)}</li>
            ))}
          </ul>
        )}
        {run.status === 'failed' && (
          <Alert tone="error" role="alert" title="The Stack Analyst failed">
            <p className="break-words">{code ?? failure?.title ?? failure?.message ?? 'It ended without a stack profile.'}</p>
            {failure?.message && code !== failure.message && <p className="mt-1 break-words text-muted-foreground">{failure.message}</p>}
            {canRun ? (
              <div className="mt-2 flex flex-wrap gap-2">
                <Button
                  pending={retry.isPending}
                  pendingLabel="Starting…"
                  onClick={() => {
                    retry.mutate(undefined, {
                      onSuccess: () => {
                        toast.success('The Stack Analyst is queued.')
                      },
                    })
                  }}
                >
                  Retry
                </Button>
                {onFillIn && (
                  <Button variant="secondary" onClick={onFillIn}>
                    Fill in manually
                  </Button>
                )}
              </div>
            ) : (
              <p className="mt-1">Only Contributors and Owners can retry it or fill the profile in.</p>
            )}
          </Alert>
        )}
        {retry.isError && (
          <Alert tone="error" role="alert" title="We couldn't start the Stack Analyst">
            <p>{errorMessage(retry.error, 'runStackAnalysis')}</p>
          </Alert>
        )}
      </>
    )
  }
  return (
    <StageSection id="stack-analyst" title="Stack Analyst" actions={runLink(project.id, run?.runId ?? null, 'View the Stack Analyst run')}>
      <div aria-live="polite" className="flex flex-col gap-3">
        {body}
      </div>
    </StageSection>
  )
}

/** FR-182: what the agent found per layer: option, path, status, confidence and the cited evidence. */
function FindingsSection({ catalog, doc, narrative }: { catalog: StackCatalog | undefined; doc: StackProfileDocument; narrative: string }) {
  return (
    <StageSection id="profile-findings" title="Findings">
      {narrative && <p className="text-sm whitespace-pre-wrap break-words">{narrative}</p>}
      {doc.generalist && (
        <Alert tone="info" title="No stack (general)">
          <p>This profile continues with No stack: the generalist agent writes every plan step.</p>
        </Alert>
      )}
      <ul aria-label="Findings per layer" className="grid gap-3 md:grid-cols-2">
        {profileLayers.map((layer) => {
          const value = doc.layers[layer]
          const option = value.option ? optionName(catalog, layer, value.option) : value.status === 'none' ? 'None' : value.technology ?? 'Not decided'
          return (
            <li key={layer} data-testid={`finding-${layer}`} className="flex min-w-0 flex-col gap-2 rounded-lg border border-border/60 p-3">
              <h4 className="font-medium">{profileLayerLabels[layer]}</h4>
              <dl className="grid grid-cols-[max-content_1fr] gap-x-3 gap-y-1 text-sm">
                <dt className="text-muted-foreground">Option</dt>
                <dd className="break-words">{option}</dd>
                {value.path && (
                  <>
                    <dt className="text-muted-foreground">Path</dt>
                    <dd className="font-mono break-all">{value.path}</dd>
                  </>
                )}
                {value.mode && (
                  <>
                    <dt className="text-muted-foreground">Mode</dt>
                    <dd>{value.mode === 'baas' ? 'Backend-as-a-service' : value.mode}</dd>
                  </>
                )}
                <dt className="text-muted-foreground">Status</dt>
                <dd>{layerStatusLabels[value.status]}</dd>
                {value.confidence && (
                  <>
                    <dt className="text-muted-foreground">Confidence</dt>
                    <dd>{confidenceLabels[value.confidence]}</dd>
                  </>
                )}
              </dl>
              {value.evidence.length > 0 && (
                <div className="text-sm">
                  <p className="font-medium">Evidence</p>
                  <ul aria-label={`${profileLayerLabels[layer]} evidence`} className="list-disc pl-5">
                    {value.evidence.map((e) => (
                      <li key={`${e.file}:${e.text}`} className="break-words">
                        <code className="font-mono text-xs break-all">{e.file}</code>: {e.text}
                      </li>
                    ))}
                  </ul>
                </div>
              )}
            </li>
          )
        })}
      </ul>
    </StageSection>
  )
}

/** FR-182: the repository structure table (§4.27 roles). */
function StructureSection({ doc }: { doc: StackProfileDocument }) {
  return (
    <StageSection id="profile-structure" title="Repository structure">
      {doc.structure.length === 0 ? (
        <p className="text-sm text-muted-foreground">The Stack Analyst listed no folders.</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full border-collapse text-sm">
            <caption className="sr-only">Repository structure: each folder and its role</caption>
            <thead>
              <tr className="border-b border-border/60 text-left text-xs text-muted-foreground">
                <th scope="col" className="px-2 py-1 font-medium">Path</th>
                <th scope="col" className="px-2 py-1 font-medium">Role</th>
                <th scope="col" className="px-2 py-1 font-medium">Note</th>
              </tr>
            </thead>
            <tbody>
              {doc.structure.map((row) => (
                <tr key={row.path} className="border-b border-border/40 last:border-b-0">
                  <th scope="row" className="px-2 py-1 text-left font-mono font-normal break-all">
                    {row.path}
                  </th>
                  <td className="px-2 py-1">{structureRoleLabels[row.role] ?? row.role}</td>
                  <td className="px-2 py-1 break-words">{row.note || '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </StageSection>
  )
}

/**
 * FR-184 before approval: the hosts, local dev loop and origin model from `/api/stack/options` for the profile's layers.
 * Disabled (with the reason as text) until the layers are resolved; detected hosts are marked. Saving makes a new draft.
 */
function HostsSection({
  catalog,
  analysis,
  doc: initial,
  editable,
  lockedReason,
}: {
  catalog: StackCatalog
  analysis: Analysis
  doc: StackProfileDocument
  editable: boolean
  lockedReason: string | null
}) {
  const { project } = useProjectAccess()
  const [doc, setDoc] = useState(initial)
  const save = useSaveStackProfile(project.id)
  const onHostsCleared = useCallback((next: StackProfileDocument) => {
    setDoc(next)
  }, [])
  const ready = hostsReady(doc)
  const options = useProfileOptions(catalog, doc, editable && ready ? onHostsCleared : null)
  const reason = !ready ? HOSTS_WAIT_REASON : lockedReason
  const disabled = reason !== null || !editable
  const changed = JSON.stringify(doc.hosts) !== JSON.stringify(initial.hosts)
  return (
    <StageSection id="profile-hosts" title="Hosts">
      <form
        noValidate
        aria-label="Hosts"
        className="flex flex-col gap-3"
        onSubmit={(e) => {
          e.preventDefault()
          save.mutate(
            { narrative: analysis.profile?.narrative ?? '', document: doc, baseVersion: analysis.profile?.version ?? null },
            {
              onSuccess: (created) => {
                toast.success(`Hosts saved as draft v${String(created.version)}.`)
              },
            },
          )
        }}
      >
        <fieldset disabled={disabled} aria-describedby={reason ? 'profile-hosts-reason' : undefined} className="flex flex-col gap-3">
          <legend className="sr-only">Hosts, local dev loop and origin model</legend>
          {reason && (
            <p id="profile-hosts-reason" className="text-sm text-muted-foreground">
              {reason}
            </p>
          )}
          {!reason && !editable && <p className="text-sm text-muted-foreground">Only Contributors and Owners can change the hosts.</p>}
          <HostPickers
            catalog={catalog}
            options={options.data}
            doc={doc}
            idPrefix="profile-hosts"
            disabled={disabled}
            onChange={(choice, option) => {
              setDoc(withHost(doc, choice, option))
            }}
          />
        </fieldset>
        {save.isError && (
          <Alert tone="error" role="alert" title="We couldn't save the hosts">
            <p>{isApiError(save.error, 'VALIDATION') ? Object.values(save.error.fieldErrors).flat().join(' ') || errorMessage(save.error) : errorMessage(save.error, 'createArtifactDraft')}</p>
          </Alert>
        )}
        {editable && !disabled && (
          <div>
            <Button type="submit" disabled={!changed} pending={save.isPending} pendingLabel="Saving…">
              Save hosts as a new draft
            </Button>
          </div>
        )}
      </form>
    </StageSection>
  )
}

/**
 * /projects/:id/analysis (FR-182). Every version with existing code: the Repository Analyst's summary and run. A version
 * that infers its stack (FR-178), in order: the Stack Analyst's run (live), Needs your input (FR-181), the findings per
 * layer, the structure table, the hosts (FR-184), the actions (Edit, Approve, Reject, FR-183), deployment setup after
 * approval (FR-184) and Continue to Refinement (FR-185); then the profile's versions (history and compare).
 */
export function AnalysisPage() {
  const { project, can, readOnly } = useProjectAccess()
  useDocumentTitle(`Analysis · ${project.name}`)
  const analysis = useAnalysis(project.id, !readOnly)
  const catalog = useStackCatalog()
  const advance = useAdvancePhase(project.id)
  const navigate = useNavigate()
  const [editor, setEditor] = useState<'edit' | 'fill' | null>(null)
  const [decision, setDecision] = useState<ProfileDecision | null>(null)
  const actionsRef = useRef<HTMLHeadingElement>(null)

  if (readOnly) {
    return (
      <section aria-labelledby="analysis-title">
        <StageHeader stage="analysis" />
        <div className="flex flex-col gap-5">
          <StageArtifact stage="analysis" type="stack_profile" actions={false} notStarted="This release version didn't infer its stack." />
        </div>
      </section>
    )
  }

  let body: ReactNode
  if (analysis.isPending) {
    body = <LoadingState label="Loading the analysis…" />
  } else if (analysis.isError) {
    body = <ErrorState error={analysis.error} title="The analysis couldn't load" onRetry={() => void analysis.refetch()} />
  } else {
    const a = analysis.data
    const inference = a.stackSource === 'infer'
    const profile = a.profile
    const doc = profile?.document ?? null
    const inAnalysis = a.phase === 'analysis'
    const writer = can('createDraft')
    const owner = can('approveArtifacts')
    const draft = profile?.status === 'draft'
    const locked = profile?.status === 'locked'
    const editable = writer && inAnalysis && !locked
    const lockedReason = !inAnalysis
      ? `The project is in ${phaseLabels[a.phase]}, so the profile can't change here. Change the stack in Settings → Stack.`
      : locked
        ? 'The profile is approved. Change the stack in Settings → Stack until the plan is locked.'
        : null

    if (!inference) {
      body = (
        <>
          {a.hasExistingCode && (
            <Alert tone="info">
              This version&apos;s stack was chosen, not inferred, so there is nothing to approve here. The Repository Analyst&apos;s
              summary of the existing code is below.
            </Alert>
          )}
          <CodebaseSummarySection state={a.summary} inference={false} />
        </>
      )
    } else {
      const openEditor = editor === null ? null : editor === 'fill' ? a.suggested : doc
      const blocked = a.canApprove ? null : (a.approveBlockedReason ?? 'The profile can’t be approved yet.')
      const approveReason = !owner ? 'Only project Owners can approve or reject the stack profile.' : blocked
      const rejectReason = !owner ? 'Only project Owners can approve or reject the stack profile.' : !draft || !inAnalysis ? 'Only a draft can be rejected.' : null
      body = (
        <>
          {!inAnalysis && locked && (
            <Alert tone="info" title="The stack profile is locked">
              <p>
                v{profile.version} was approved and the project moved on to {phaseLabels[a.phase]}. This page is read-only; the
                pinned stack is in Settings → Stack.
              </p>
            </Alert>
          )}
          <RunSection
            analysis={a}
            canRun={can('startAgents') && inAnalysis}
            onFillIn={
              editable && a.suggested && catalog.data
                ? () => {
                    setEditor('fill')
                  }
                : null
            }
          />

          {doc && a.needsInput.length > 0 && catalog.data && (
            <NeedsInputSection
              key={`needs-input-${String(profile?.version)}`}
              catalog={catalog.data}
              analysis={a}
              document={doc}
              canEdit={editable && draft}
              canDecide={owner && draft && inAnalysis}
              onGeneralist={() => {
                setDecision('generalist')
              }}
            />
          )}

          {profile && !doc && (
            <Alert tone="error" role="alert" title="This profile can't be read">
              <p>Edit it from the detector&apos;s findings, or run the analysis again.</p>
              {profile.errors.length > 0 && (
                <ul className="mt-1 list-disc pl-5">
                  {profile.errors.map((e) => (
                    <li key={e}>{e}</li>
                  ))}
                </ul>
              )}
            </Alert>
          )}

          {doc && <FindingsSection catalog={catalog.data} doc={doc} narrative={profile?.narrative ?? ''} />}
          {doc && <StructureSection doc={doc} />}
          {doc && catalog.data && !doc.generalist && (
            <HostsSection key={`hosts-${String(profile?.version)}`} catalog={catalog.data} analysis={a} doc={doc} editable={editable && draft} lockedReason={lockedReason} />
          )}

          {(profile ?? openEditor) && (
            <StageSection id="profile-actions" title={<span ref={actionsRef} tabIndex={-1}>Decide</span>}>
              {openEditor && catalog.data ? (
                <ProfileEditor
                  catalog={catalog.data}
                  initial={openEditor}
                  narrative={editor === 'edit' ? (profile?.narrative ?? '') : ''}
                  baseVersion={profile?.version ?? null}
                  title={editor === 'fill' ? 'Fill in the profile yourself' : 'Edit the stack profile'}
                  intro={
                    editor === 'fill'
                      ? 'Starting from what the detector found, with no agent findings. Saving creates a draft an Owner can approve.'
                      : 'Change any layer, folder or host. Saving creates a new draft authored by you.'
                  }
                  onCancel={() => {
                    setEditor(null)
                    actionsRef.current?.focus()
                  }}
                  onSaved={(created) => {
                    setEditor(null)
                    toast.success(`Saved as draft v${String(created.version)}. An Owner can now approve or reject it.`)
                    actionsRef.current?.focus()
                  }}
                />
              ) : (
                profile && (
                  <div className="flex flex-col gap-3">
                    <p className="flex flex-wrap items-center gap-2 text-sm">
                      <span className="font-mono font-medium">v{profile.version}</span>
                      <Badge tone={locked ? 'success' : draft ? 'warning' : 'neutral'}>
                        {locked ? 'Approved and locked' : draft ? 'Draft' : (profile.status ?? 'Unknown')}
                      </Badge>
                      <span className="text-muted-foreground">
                        {profile.authoredByAgent ? 'Written by the Stack Analyst' : 'Edited by a person'} ·{' '}
                        <RelativeTime iso={profile.createdAt} />
                      </span>
                    </p>
                    {!locked && inAnalysis && (
                      <>
                        <div className="flex flex-wrap gap-2">
                          {editable && (
                            <Button
                              variant="secondary"
                              disabled={!catalog.data || !doc}
                              onClick={() => {
                                setEditor('edit')
                              }}
                            >
                              Edit
                            </Button>
                          )}
                          <Button
                            disabled={approveReason !== null}
                            aria-describedby={approveReason ? 'approve-reason' : undefined}
                            onClick={() => {
                              setDecision('approve')
                            }}
                          >
                            Approve
                          </Button>
                          <Button
                            variant="danger"
                            disabled={rejectReason !== null}
                            aria-describedby={rejectReason ? 'reject-reason' : undefined}
                            onClick={() => {
                              setDecision('reject')
                            }}
                          >
                            Reject
                          </Button>
                          {doc && hasUnsupported(doc) && owner && draft && (
                            <Button
                              variant="secondary"
                              onClick={() => {
                                setDecision('generalist')
                              }}
                            >
                              Continue with No stack (general)
                            </Button>
                          )}
                        </div>
                        {approveReason && (
                          <p id="approve-reason" data-testid="approve-reason" className="text-sm">
                            <span className="font-medium">Approve is unavailable: </span>
                            {approveReason}
                          </p>
                        )}
                        {rejectReason && rejectReason !== approveReason && (
                          <p id="reject-reason" className="text-sm text-muted-foreground">
                            {rejectReason}
                          </p>
                        )}
                        {!writer && <p className="text-sm text-muted-foreground">Only Contributors and Owners can edit the profile.</p>}
                      </>
                    )}
                    {a.previousComposition && (
                      <p className="text-sm text-muted-foreground">
                        Previous version&apos;s stack:{' '}
                        {[a.previousComposition.frontend?.pack, a.previousComposition.backend?.pack, a.previousComposition.data?.pack]
                          .flatMap((p, i) => (p ? [optionName(catalog.data, ['frontend', 'backend', 'data'][i] ?? '', p)] : []))
                          .join(' · ') || 'no frontend, backend or data layer'}
                        . Approving different layers is a stack change.
                      </p>
                    )}
                  </div>
                )
              )}
            </StageSection>
          )}

          {locked && inAnalysis && (a.deployment.available ? (
            <AnalysisDeploymentSetup />
          ) : (
            a.deployment.reason && (
              <StageSection id="analysis-deployment" title="Deployment setup">
                <p className="text-sm text-muted-foreground">{a.deployment.reason}</p>
              </StageSection>
            )
          ))}

          {locked && inAnalysis && (
            <StageSection id="analysis-continue" title="Continue">
              <p className="text-sm text-muted-foreground">
                The stack is pinned. Deployment setup that is still running or skipped doesn&apos;t block this.
              </p>
              {advance.isError && (
                <Alert tone="error" role="alert" title="We couldn't continue to Refinement">
                  <p>{[errorMessage(advance.error), ...errorReasons(advance.error)].join(' ')}</p>
                </Alert>
              )}
              <div>
                <Button
                  disabled={!a.canContinue || !can('advancePhase')}
                  aria-describedby={!can('advancePhase') ? 'continue-reason' : undefined}
                  pending={advance.isPending}
                  pendingLabel="Continuing…"
                  onClick={() => {
                    advance.mutate(
                      {},
                      {
                        onSuccess: (result) => {
                          if (result.gate) {
                            toast.info('An approval gate must be decided first.')
                            return
                          }
                          toast.success('The project moved on to Refinement.')
                          void navigate({ to: '/projects/$projectId/refinement', params: { projectId: project.id } })
                        },
                      },
                    )
                  }}
                >
                  Continue to Refinement
                </Button>
              </div>
              {!can('advancePhase') && (
                <p id="continue-reason" className="text-sm text-muted-foreground">
                  Only Contributors and Owners can move the project on.
                </p>
              )}
            </StageSection>
          )}

          {profile && (
            <StageArtifact
              stage="analysis"
              type="stack_profile"
              actions={false}
              expectedLatest={profile.version}
              awaitingDraft={isAnalysisRunPending(a.run)}
              notStarted="The Stack Analyst writes the stack profile after the Repository Analyst."
            />
          )}
          <CodebaseSummarySection state={a.summary} inference />
          <StageRuns agent="stack_analyst" />

          {decision && profile && (
            <AnalysisDecisionDialog
              artifactId={profile.artifactId}
              version={profile.version}
              action={decision}
              fallbackFocusRef={actionsRef}
              onClose={() => {
                setDecision(null)
              }}
              onDecided={(action, decided) => {
                setDecision(null)
                toast.success(
                  action === 'reject'
                    ? `v${String(decided.version)} rejected. The Stack Analyst runs again with your feedback.`
                    : action === 'generalist'
                      ? `v${String(decided.version)} approved with No stack (general).`
                      : `v${String(decided.version)} approved and locked. The stack is pinned.`,
                )
              }}
            />
          )}
        </>
      )
    }
  }

  const awaiting = analysis.data?.profile?.status === 'draft'
  return (
    <section aria-labelledby="analysis-title">
      <StageHeader
        stage="analysis"
        awaitingApproval={awaiting}
        description="What the agents found in the existing code: the codebase summary and, when the version infers its stack, the stack profile."
      />
      <div className="flex flex-col gap-5">{body}</div>
    </section>
  )
}
