import { useQueryClient } from '@tanstack/react-query'

import { buttonClasses } from '../../components/ui/button-classes'
import { PageHeader } from '../../components/ui/states'
import type { GateInboxItem } from '../../engine/api/types'
import { GateInbox, type GateDecisionCopy, type GateInboxLinks } from '../../engine/components/gate-inbox'
import { Link } from '../../lib/navigation'
import { invalidateProject } from '../lifecycle/lifecycle-api'
import { artifactTypeLabels, gateTypeLabels } from '../projects/labels'
import { approveEffect, rejectEffect } from './gate-effects'

/** The SDLC pipeline's id (its artifacts have pages under /projects/:id/artifacts/:type). */
const SDLC_PIPELINE_ID = 'sdlc'

/** Today's copy for members who may not decide (Overview → Pending gates). */
export const CANNOT_DECIDE_NOTE = 'Only project Owners can decide gates.'

type SdlcStageGate = keyof typeof approveEffect

const isSdlcArtifact = (key: string): key is keyof typeof artifactTypeLabels => Object.hasOwn(artifactTypeLabels, key)
const isSdlcStageGate = (type: string): type is SdlcStageGate => Object.hasOwn(approveEffect, type)

/**
 * FR-307 routing for the engine's GateInbox (src/engine knows no routes): the project's Overview, and for an SDLC
 * artifact its artifact page at the gated version. Artifacts of other pipelines have no page yet, so they show as text.
 */
export const gateInboxLinks: GateInboxLinks = {
  project: (item, label) => (
    <Link to="/projects/$projectId" params={{ projectId: item.gate.projectId }} className={buttonClasses('secondary')}>
      {label}
      <span className="sr-only">: {item.projectName}</span>
    </Link>
  ),
  artifact: (item, label) => {
    const key = item.gate.artifactKey
    if (item.pipelineId !== SDLC_PIPELINE_ID || !key || !isSdlcArtifact(key)) {
      return null
    }
    return (
      <Link
        to="/projects/$projectId/artifacts/$type"
        params={{ projectId: item.gate.projectId, type: key }}
        search={item.gate.artifactVersion === null ? {} : { version: item.gate.artifactVersion }}
        className="font-medium text-primary underline"
      >
        {label}
      </Link>
    )
  },
}

/** SDLC stage gates get the project gate dialogs' copy (features/gates/gate-dialogs); other pipelines the engine's. */
export function sdlcDecisionCopy(item: GateInboxItem): GateDecisionCopy | undefined {
  const type = item.gate.type
  if (item.pipelineId !== SDLC_PIPELINE_ID || !isSdlcStageGate(type)) {
    return undefined
  }
  return { label: gateTypeLabels[type], approveEffect: approveEffect[type], rejectEffect: rejectEffect[type] }
}

/**
 * /gates (FR-307, SPEC §7): the Gate inbox, beside Home's pending-gates card (Q3 default). A decision refreshes the
 * project's own queries, so its Overview and Home's card agree with the inbox.
 */
export function GateInboxPage() {
  const queryClient = useQueryClient()
  return (
    <div className="flex flex-col gap-4">
      <PageHeader title="Gates" description="Gates waiting for a decision on the projects you can see, newest first." />
      <GateInbox
        links={gateInboxLinks}
        decisionCopy={sdlcDecisionCopy}
        cannotDecideNote={CANNOT_DECIDE_NOTE}
        onDecided={(item) => {
          // The project's detail queries (its gates, state and Overview) sit under the project key.
          void invalidateProject(queryClient, item.gate.projectId)
        }}
      />
    </div>
  )
}
