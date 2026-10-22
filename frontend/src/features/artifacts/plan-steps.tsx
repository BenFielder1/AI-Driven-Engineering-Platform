import { Badge, type BadgeTone } from '../../components/ui/badge'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { useExecutionSteps, type StepStatus } from '../execution/execution-api'
import { stepStatusLabels } from '../projects/labels'
import type { Artifact } from './artifacts-api'
import { PLAN_BLOCK_LANGUAGE, readPlanBlock, type PlanPreviewStep } from './plan-block'

const stepTones: Record<StepStatus, BadgeTone> = {
  pending: 'neutral',
  in_progress: 'info',
  completed: 'success',
  rejected: 'danger',
  blocked: 'warning',
  invalidated: 'warning',
}

interface Row extends Omit<PlanPreviewStep, 'files' | 'agent' | 'covers'> {
  /** `protected` comes from the server (FR-30, §4.6); a draft preview can't tell, so it leaves it unset. */
  files: { path: string; action: string; protected?: boolean }[]
  status?: StepStatus
}

function StepsTable({ caption, rows, withStatus }: { caption: string; rows: Row[]; withStatus: boolean }) {
  const th = 'px-3 py-2'
  return (
    <div className="overflow-x-auto rounded-xl border border-border/60 bg-card/70 shadow-sm backdrop-blur-md">
      <table className="w-full border-collapse text-sm">
        <caption className="mb-2 text-left text-sm text-muted-foreground">{caption}</caption>
        <thead>
          <tr className="border-b border-border/60 text-left text-xs text-muted-foreground">
            <th scope="col" className={th}>
              Step
            </th>
            <th scope="col" className={th}>
              Title
            </th>
            <th scope="col" className={th}>
              Files
            </th>
            <th scope="col" className={th}>
              Depends on
            </th>
            {withStatus && (
              <th scope="col" className="py-2">
                Status
              </th>
            )}
          </tr>
        </thead>
        <tbody>
          {rows.map((step) => (
            <tr key={step.number} className="border-b border-border align-top last:border-b-0">
              <th scope="row" className="px-3 py-2 text-left font-medium">
                {step.number}
              </th>
              <td className="px-3 py-2 break-words">{step.title}</td>
              <td className="px-3 py-2">
                <ul className="flex flex-col gap-0.5">
                  {step.files.map((f) => (
                    <li key={`${f.action}:${f.path}`} className="break-all">
                      <code className="text-xs">{f.path}</code> <span className="text-xs text-muted-foreground">({f.action})</span>
                      {f.protected === true && (
                        <>
                          {' '}
                          <Badge tone="warning">Protected</Badge>
                        </>
                      )}
                    </li>
                  ))}
                </ul>
              </td>
              <td className="px-3 py-2">{step.dependencies.length === 0 ? 'None' : step.dependencies.join(', ')}</td>
              {withStatus && (
                <td className="py-2">{step.status && <Badge tone={stepTones[step.status]}>{stepStatusLabels[step.status]}</Badge>}</td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
      {rows.some((r) => r.files.some((f) => f.protected === true)) && (
        <p className="mt-2 text-xs text-muted-foreground">
          Protected paths (.git, .aide, CI workflows, lockfiles and env files) may be changed only by the step that declares
          them; a change to one from any other step is a scope-creep violation.
        </p>
      )}
    </div>
  )
}

/** A draft has no steps yet (FR-30 creates them on approval): preview them from its aide-plan block. */
function DraftPreview({ artifact }: { artifact: Artifact }) {
  const steps = readPlanBlock(artifact.content)
  if (!steps) {
    return (
      <p className="text-sm text-muted-foreground">
        The ```{PLAN_BLOCK_LANGUAGE} block of this draft can&apos;t be read, so there is no step preview. Approving it will
        report what&apos;s wrong.
      </p>
    )
  }
  return (
    <StepsTable
      caption={`Preview of the ${String(steps.length)} steps in draft v${String(artifact.version)}, read from its ${PLAN_BLOCK_LANGUAGE} block. Execution steps are created when it's approved.`}
      rows={steps}
      withStatus={false}
    />
  )
}

/** FR-30: the execution steps created for an approved plan version, with their status. */
function VersionSteps({ projectId, artifact }: { projectId: string; artifact: Artifact }) {
  const query = useExecutionSteps(projectId, artifact.type, artifact.version)
  if (query.isPending) {
    return <LoadingState label="Loading the plan's steps…" />
  }
  if (query.isError) {
    return <ErrorState error={query.error} title="The plan's steps couldn't load" onRetry={() => void query.refetch()} />
  }
  const { steps } = query.data
  if (steps.length === 0) {
    return <p className="text-sm text-muted-foreground">v{artifact.version} has no execution steps: steps are created only when a plan version is approved.</p>
  }
  return (
    <StepsTable
      caption={`${String(steps.length)} execution steps of v${String(artifact.version)}`}
      rows={steps.map((s) => ({
        number: s.number,
        title: s.title,
        files: s.declaredFiles,
        dependencies: s.dependencies,
        status: s.status,
      }))}
      withStatus
    />
  )
}

/** The steps of the shown plan version (FR-29, FR-30): live execution steps once approved, a preview for drafts. */
export function PlanSteps({ projectId, artifact }: { projectId: string; artifact: Artifact }) {
  return artifact.status === 'draft' ? (
    <DraftPreview artifact={artifact} />
  ) : (
    <VersionSteps projectId={projectId} artifact={artifact} />
  )
}
