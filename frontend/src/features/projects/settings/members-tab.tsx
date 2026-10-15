import { zodResolver } from '@hookform/resolvers/zod'
import { useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'

import { Alert, ErrorSummary } from '../../../components/ui/alert'
import { Button } from '../../../components/ui/button'
import { ConfirmDialog } from '../../../components/ui/confirm-dialog'
import { fieldErrorSummary } from '../../../components/ui/form-errors'
import { SelectField } from '../../../components/ui/select-field'
import { EmptyState, ErrorState, LoadingState } from '../../../components/ui/states'
import { TextField } from '../../../components/ui/text-field'
import { errorMessage, isApiError } from '../../../lib/errors'
import { canInProject, type ProjectRole } from '../../../lib/permissions'
import { projectRoles, roleLabels } from '../labels'
import { useProjectAccess } from '../project-context'
import {
  projectKeys,
  useAddMember,
  useMembers,
  useRemoveMember,
  useUpdateMemberRole,
  type Member,
} from '../projects-api'
import { limits } from '../schemas'

const addMemberSchema = z.object({
  email: z
    .string()
    .trim()
    .min(1, { error: 'Enter the email address of an existing AIDE user.' })
    .max(limits.emailMax, { error: `Email addresses are at most ${String(limits.emailMax)} characters.` })
    .pipe(z.email({ error: 'Enter a valid email address.' })),
  role: z.enum(projectRoles),
})

type AddMemberValues = z.infer<typeof addMemberSchema>

const addFieldIds = { email: 'member-email', role: 'member-role' } as const

function AddMemberForm({ projectId }: { projectId: string }) {
  const add = useAddMember(projectId)
  const [serverError, setServerError] = useState<string | null>(null)
  const [added, setAdded] = useState<string | null>(null)
  const {
    register,
    handleSubmit,
    setError,
    reset,
    formState: { errors, isSubmitted },
  } = useForm<AddMemberValues>({
    resolver: zodResolver(addMemberSchema),
    defaultValues: { email: '', role: 'contributor' },
  })

  const onSubmit = handleSubmit((values) => {
    setServerError(null)
    setAdded(null)
    add.mutate(values, {
      onSuccess: (member) => {
        reset()
        setAdded(`${member.name} was added as ${roleLabels[member.role ?? 'viewer']}.`)
      },
      onError: (error) => {
        if (isApiError(error, 'USER_NOT_FOUND') || isApiError(error, 'CONFLICT')) {
          setError('email', { type: 'server', message: errorMessage(error, 'addMember') })
        } else {
          setServerError(errorMessage(error))
        }
      },
    })
  })

  return (
    <form noValidate onSubmit={(e) => void onSubmit(e)} aria-labelledby="add-member-title" className="mb-6">
      <h3 id="add-member-title" className="mb-3 font-semibold">
        Add a member
      </h3>
      <ErrorSummary
        title="We couldn't add the member"
        errors={[...(serverError ? [{ message: serverError }] : []), ...(isSubmitted ? fieldErrorSummary(errors, addFieldIds) : [])]}
      />
      <div role="status" aria-live="polite">
        {added && (
          <Alert tone="success" className="mb-3">
            {added}
          </Alert>
        )}
      </div>
      <div className="mt-2 flex flex-col gap-4 sm:flex-row sm:items-start">
        <TextField
          id={addFieldIds.email}
          label="Email"
          type="email"
          autoComplete="off"
          className="flex-1"
          error={errors.email?.message}
          {...register('email')}
        />
        <SelectField id={addFieldIds.role} label="Role" error={errors.role?.message} {...register('role')}>
          {projectRoles.map((r) => (
            <option key={r} value={r}>
              {roleLabels[r]}
            </option>
          ))}
        </SelectField>
        <Button type="submit" pending={add.isPending} pendingLabel="Adding…" className="sm:mt-6">
          Add member
        </Button>
      </div>
    </form>
  )
}

/** Settings → Members (FR-8): any member sees the list and can leave; Owners add, change roles and remove. */
export function MembersTab() {
  const { project, user, role } = useProjectAccess()
  const members = useMembers(project.id)
  const updateRole = useUpdateMemberRole(project.id)
  const remove = useRemoveMember(project.id)
  const leave = useRemoveMember(project.id)
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const canManage = canInProject(user, role, 'manageMembers')
  const [actionError, setActionError] = useState<string | null>(null)
  const [toRemove, setToRemove] = useState<Member | null>(null)
  const [confirmLeave, setConfirmLeave] = useState(false)

  const isMember = project.myRole !== null

  function changeRole(member: Member, next: ProjectRole) {
    setActionError(null)
    updateRole.mutate(
      { userId: member.userId, role: next },
      {
        onError: (error) => {
          setActionError(errorMessage(error))
        },
      },
    )
  }

  function confirmRemove() {
    if (!toRemove) {
      return
    }
    setActionError(null)
    remove.mutate(toRemove.userId, {
      onSettled: () => {
        setToRemove(null)
      },
      onError: (error) => {
        setActionError(errorMessage(error))
      },
    })
  }

  function confirmLeaveProject() {
    if (!user) {
      return
    }
    setActionError(null)
    leave.mutate(user.id, {
      onSuccess: () => {
        setConfirmLeave(false)
        void navigate({ to: '/projects' }).then(() => {
          queryClient.removeQueries({ queryKey: projectKeys.detail(project.id) })
        })
      },
      onError: (error) => {
        setConfirmLeave(false)
        setActionError(errorMessage(error))
      },
    })
  }

  return (
    <section aria-labelledby="members-title">
      <div className="mb-4 flex flex-wrap items-center justify-between gap-2">
        <h2 id="members-title" className="text-xl">
          Members
        </h2>
        {isMember && (
          <Button
            variant="secondary"
            onClick={() => {
              setConfirmLeave(true)
            }}
          >
            Leave project
          </Button>
        )}
      </div>

      {canManage && <AddMemberForm projectId={project.id} />}

      {actionError && (
        <Alert tone="error" role="alert" title="That didn't work" className="mb-4">
          <p>{actionError}</p>
        </Alert>
      )}

      {members.isPending && <LoadingState label="Loading members…" />}
      {members.isError && <ErrorState error={members.error} onRetry={() => void members.refetch()} />}
      {members.isSuccess && members.data.length === 0 && <EmptyState title="No members" />}
      {members.isSuccess && members.data.length > 0 && (
        <ul aria-label="Project members" className="divide-y divide-border/60 rounded-lg border border-border/60">
          {members.data.map((m) => {
            const isMe = m.userId === user?.id
            return (
              <li key={m.userId} className="flex flex-col gap-2 p-3 sm:flex-row sm:items-center sm:justify-between">
                <div className="min-w-0">
                  <p className="break-words font-medium">
                    {m.name}
                    {isMe && <span className="text-muted-foreground"> (you)</span>}
                  </p>
                  <p className="break-words text-sm text-muted-foreground">{m.email}</p>
                </div>
                {canManage ? (
                  <div className="flex flex-wrap items-end gap-2">
                    <SelectField
                      label={`Role for ${m.name}`}
                      className="[&>label]:sr-only"
                      value={m.role ?? 'viewer'}
                      disabled={updateRole.isPending}
                      onChange={(e) => {
                        const next = projectRoles.find((r) => r === e.target.value)
                        if (next && next !== m.role) {
                          changeRole(m, next)
                        }
                      }}
                    >
                      {projectRoles.map((r) => (
                        <option key={r} value={r}>
                          {roleLabels[r]}
                        </option>
                      ))}
                    </SelectField>
                    {!isMe && (
                      <Button
                        variant="secondary"
                        aria-label={`Remove ${m.name}`}
                        onClick={() => {
                          setToRemove(m)
                        }}
                      >
                        Remove
                      </Button>
                    )}
                  </div>
                ) : (
                  <span className="text-sm">{m.role ? roleLabels[m.role] : ''}</span>
                )}
              </li>
            )
          })}
        </ul>
      )}

      <ConfirmDialog
        open={toRemove !== null}
        title={`Remove ${toRemove?.name ?? ''}?`}
        confirmLabel="Remove member"
        pendingLabel="Removing…"
        pending={remove.isPending}
        destructive
        onConfirm={confirmRemove}
        onCancel={() => {
          setToRemove(null)
        }}
      >
        They lose access to this project immediately.
      </ConfirmDialog>

      <ConfirmDialog
        open={confirmLeave}
        title={`Leave ${project.name}?`}
        confirmLabel="Leave project"
        pendingLabel="Leaving…"
        pending={leave.isPending}
        destructive
        onConfirm={confirmLeaveProject}
        onCancel={() => {
          setConfirmLeave(false)
        }}
      >
        You lose access to this project until an Owner adds you again.
      </ConfirmDialog>
    </section>
  )
}
