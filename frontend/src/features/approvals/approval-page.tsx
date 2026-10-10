import { zodResolver } from '@hookform/resolvers/zod'
import { useQueryClient } from '@tanstack/react-query'
import { Link, useRouter } from '@tanstack/react-router'
import { useEffect, useRef, useState } from 'react'
import { useForm } from 'react-hook-form'
import type { z } from 'zod'

import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { LoadingState, PageHeader } from '../../components/ui/states'
import { TextAreaField } from '../../components/ui/textarea-field'
import { applyFieldErrors, errorMessage, isApiError, messageFor, type ErrorCode } from '../../lib/errors'
import { useDocumentTitle } from '../../lib/use-document-title'
import { authKeys } from '../auth/auth-api'
import { approveEffect, rejectEffect } from '../gates/gate-effects'
import { commentSchema, GATE_COMMENT_MAX } from '../gates/gate-schemas'
import type { Gate } from '../gates/gates-api'
import { approvalDecisionLabels, artifactTypeLabels, formatDateTime, gateTypeLabels } from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import { useApprovalLink, useConfirmApprovalLink, type ApprovalLinkView } from './approvals-api'

type CommentValues = z.infer<typeof commentSchema>

/** Outcomes that end the page: the link can't (or can no longer) be confirmed. */
type Terminal = 'LINK_USED' | 'LINK_EXPIRED' | 'FORBIDDEN' | 'NOT_FOUND' | 'GATE_NOT_PENDING'
const terminalCodes: readonly ErrorCode[] = ['LINK_USED', 'LINK_EXPIRED', 'FORBIDDEN', 'NOT_FOUND', 'GATE_NOT_PENDING']

const terminalTitles: Record<Terminal, string> = {
  LINK_USED: 'This link has already been used',
  LINK_EXPIRED: 'This link has expired',
  FORBIDDEN: "You can't use this link",
  NOT_FOUND: 'Approval link not found',
  GATE_NOT_PENDING: 'This gate is already closed',
}

/** A 200 normally says `valid` or `gate_closed`; `used`/`expired` are mapped too, should the API send them. */
const statusTerminal: Record<Exclude<ApprovalLinkView['status'], 'valid'>, Terminal> = {
  gate_closed: 'GATE_NOT_PENDING',
  used: 'LINK_USED',
  expired: 'LINK_EXPIRED',
}

function terminalOf(error: unknown): Terminal | null {
  return isApiError(error) && error.errorCode !== null && terminalCodes.includes(error.errorCode)
    ? (error.errorCode as Terminal)
    : null
}

/**
 * FR-48: the token is a credential in the URL. `no-referrer` keeps it out of the Referer header of anything this
 * page loads or links to (React hoists the tag into <head> while the page is shown).
 */
function NoReferrer() {
  return <meta name="referrer" content="no-referrer" />
}

/** A heading that takes focus when it appears, so the new state is announced (NFR-A11Y). */
function FocusHeading({ children }: { children: string }) {
  const ref = useRef<HTMLHeadingElement>(null)
  useDocumentTitle(children)
  useEffect(() => {
    ref.current?.focus()
  }, [])
  return (
    <h1 ref={ref} tabIndex={-1} className="mb-4 text-2xl font-semibold tracking-tight focus:outline-none">
      {children}
    </h1>
  )
}

function TerminalState({ code, view }: { code: Terminal; view?: ApprovalLinkView }) {
  return (
    <>
      <FocusHeading>{terminalTitles[code]}</FocusHeading>
      <Alert tone={code === 'FORBIDDEN' || code === 'NOT_FOUND' ? 'error' : 'warning'}>
        <p>{messageFor(code, 'approvalLink')}</p>
      </Alert>
      <div className="mt-4 flex flex-wrap gap-2">
        {view && code !== 'FORBIDDEN' ? (
          <Link to="/projects/$projectId" params={{ projectId: view.projectId }} className={buttonClasses('secondary')}>
            Open {view.projectName}
          </Link>
        ) : (
          <Link to="/projects" className={buttonClasses('secondary')}>
            Go to projects
          </Link>
        )}
      </div>
    </>
  )
}

function Summary({ view }: { view: ApprovalLinkView }) {
  const a = view.artifactSummary
  return (
    <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
      <dt className="text-muted">Project</dt>
      <dd className="break-words">
        <Link to="/projects/$projectId" params={{ projectId: view.projectId }} className="font-medium text-link underline">
          {view.projectName}
        </Link>
      </dd>
      <dt className="text-muted">Gate</dt>
      <dd>{gateTypeLabels[view.gateType]}</dd>
      <dt className="text-muted">Decision</dt>
      <dd className="font-semibold">{approvalDecisionLabels[view.decision]}</dd>
      {a && (
        <>
          <dt className="text-muted">Artifact</dt>
          <dd className="break-words">
            {artifactTypeLabels[a.type]} v{a.version}
            {a.title && <> — {a.title}</>}
          </dd>
        </>
      )}
      <dt className="text-muted">Link expires</dt>
      <dd>
        {formatDateTime(view.expiresAt)} (<RelativeTime iso={view.expiresAt} />)
      </dd>
    </dl>
  )
}

const effectOf = (view: ApprovalLinkView) => {
  if (view.gateType === 'escalation') {
    return null
  }
  return view.decision === 'approve' ? approveEffect[view.gateType] : rejectEffect[view.gateType]
}

function ConfirmForm({
  token,
  view,
  onDone,
}: {
  token: string
  view: ApprovalLinkView
  onDone: (result: { gate: Gate } | { terminal: Terminal }) => void
}) {
  const confirm = useConfirmApprovalLink(token, view.projectId)
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const reject = view.decision === 'reject'
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitted },
  } = useForm<CommentValues>({ resolver: zodResolver(commentSchema), defaultValues: { comment: '' } })

  const onSubmit = handleSubmit(({ comment }) => {
    setServerErrors([])
    confirm.mutate(comment.trim() ? { comment } : {}, {
      onSuccess: (gate) => {
        onDone({ gate })
      },
      onError: (error) => {
        const terminal = terminalOf(error)
        if (terminal) {
          onDone({ terminal })
          return
        }
        const unmatched = applyFieldErrors(error, setError, ['comment'])
        setServerErrors(
          unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error, 'approvalLink')],
        )
      },
    })
  })

  const effect = effectOf(view)
  return (
    <form noValidate onSubmit={(e) => void onSubmit(e)} aria-labelledby="confirm-title" className="mt-6 flex flex-col gap-4">
      <h2 id="confirm-title" className="text-lg font-semibold">
        {reject ? 'Confirm the rejection' : 'Confirm the approval'}
      </h2>
      {effect && <p className="text-sm">{effect}</p>}
      <ErrorSummary
        title="We couldn't record your decision"
        errors={[
          ...serverErrors.map((message) => ({ message })),
          ...(isSubmitted ? fieldErrorSummary(errors, { comment: 'approval-comment' }) : []),
        ]}
      />
      <TextAreaField
        id="approval-comment"
        label={reject ? 'Feedback (optional)' : 'Comment (optional)'}
        rows={4}
        maxLength={GATE_COMMENT_MAX}
        error={errors.comment?.message}
        {...register('comment')}
      />
      <div className="flex flex-wrap gap-2">
        <Button
          type="submit"
          variant={reject ? 'danger' : 'primary'}
          pending={confirm.isPending}
          pendingLabel={reject ? 'Rejecting…' : 'Approving…'}
        >
          {reject ? 'Confirm rejection' : 'Confirm approval'}
        </Button>
        <Link to="/projects/$projectId" params={{ projectId: view.projectId }} className={buttonClasses('secondary')}>
          Cancel
        </Link>
      </div>
    </form>
  )
}

/**
 * /approvals/:token (FR-48, SPEC §7): what a one-click link decides, and a confirmation (with an optional comment)
 * that records it. Expired, used, forbidden, not found and gate-closed links say that nothing changed. A signed-out
 * user is sent to sign in and comes back here.
 */
export function ApprovalPage({ token }: { token: string }) {
  const router = useRouter()
  const queryClient = useQueryClient()
  const link = useApprovalLink(token)
  const [result, setResult] = useState<{ gate: Gate } | { terminal: Terminal } | null>(null)
  const unauthenticated = link.isError && isApiError(link.error, 'UNAUTHENTICATED')

  // The app's global 401 handler (main.tsx) does the same; this keeps the page correct on its own. Signing in returns
  // here through the login page's same-origin `returnUrl`.
  useEffect(() => {
    if (unauthenticated) {
      queryClient.setQueryData(authKeys.me(), null)
      const returnUrl = router.state.location.href
      void router.navigate({ to: '/login', search: { returnUrl }, replace: true })
    }
  }, [unauthenticated, router, queryClient])

  let content
  if (link.isPending || unauthenticated) {
    content = <LoadingState label="Loading the approval…" />
  } else if (link.isError) {
    const terminal = terminalOf(link.error)
    content = terminal ? (
      <TerminalState code={terminal} />
    ) : (
      <>
        <FocusHeading>Approval</FocusHeading>
        <Alert tone="error" role="alert" title="The approval couldn't load">
          <p>{errorMessage(link.error, 'approvalLink')}</p>
          <Button variant="secondary" className="mt-2" onClick={() => void link.refetch()}>
            Try again
          </Button>
        </Alert>
      </>
    )
  } else if (result && 'terminal' in result) {
    content = <TerminalState code={result.terminal} view={link.data} />
  } else if (result) {
    const label = gateTypeLabels[result.gate.type]
    content = (
      <>
        <FocusHeading>{result.gate.status === 'rejected' ? `${label} rejected` : `${label} approved`}</FocusHeading>
        <Alert tone="success" role="status">
          <p>
            Your decision on {link.data.projectName} was recorded. This link can&apos;t be used again.
          </p>
        </Alert>
        <Link
          to="/projects/$projectId"
          params={{ projectId: link.data.projectId }}
          className={buttonClasses('primary', 'mt-4')}
        >
          Open {link.data.projectName}
        </Link>
      </>
    )
  } else if (link.data.status !== 'valid') {
    content = <TerminalState code={statusTerminal[link.data.status]} view={link.data} />
  } else {
    const view = link.data
    content = (
      <>
        <PageHeader title={`${approvalDecisionLabels[view.decision]} ${gateTypeLabels[view.gateType].toLowerCase()}?`} />
        <section aria-label="Approval summary" className="rounded-lg border border-border bg-surface-raised p-5">
          <Summary view={view} />
        </section>
        <ConfirmForm token={token} view={view} onDone={setResult} />
      </>
    )
  }

  return (
    <div className="mx-auto max-w-2xl">
      <NoReferrer />
      {content}
    </div>
  )
}
