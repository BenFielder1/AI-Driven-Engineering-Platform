import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'

import { Alert } from '../../components/ui/alert'
import { Badge } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { TextField } from '../../components/ui/text-field'
import { errorMessage } from '../../lib/errors'
import { canChangeSystemRoles } from '../../lib/permissions'
import { useDocumentTitle } from '../../lib/use-document-title'
import { useCurrentUser } from '../auth/auth-api'
import { RelativeTime } from '../projects/relative-time'
import {
  ADMIN_PAGE_SIZE,
  useAdminUsers,
  useChangeSystemRole,
  type AdminUser,
  type AdminUsersQuery,
  type SystemRole,
} from './admin-api'
import { systemRoleLabels } from './admin-labels'
import type { AdminUsersSearch } from './admin-search'
import { AdminTable, bodyCell, FilterForm, PagedList } from './admin-shared'

const filterSchema = z.object({
  search: z.string().trim().max(200, { error: 'Searches are at most 200 characters.' }),
})

type FilterValues = z.infer<typeof filterSchema>

function usersQuery(s: AdminUsersSearch): AdminUsersQuery {
  return { page: s.page ?? 1, limit: ADMIN_PAGE_SIZE, ...(s.search ? { search: s.search } : {}) }
}

const otherRole = (role: SystemRole): SystemRole => (role === 'admin' ? 'user' : 'admin')

function UserRow({ user, isMe, canChange, onChangeRole }: { user: AdminUser; isMe: boolean; canChange: boolean; onChangeRole: () => void }) {
  const target = otherRole(user.systemRole)
  return (
    <tr className="border-b border-border/40 transition-colors last:border-b-0 hover:bg-muted/40">
      <th scope="row" className={`${bodyCell} text-left font-medium`}>
        {user.name}
        {isMe && <span className="ml-1 text-xs font-normal text-muted-foreground">(you)</span>}
      </th>
      <td className={`${bodyCell} break-all`}>{user.email}</td>
      <td className={bodyCell}>
        <Badge tone={user.systemRole === 'admin' ? 'info' : 'neutral'}>{systemRoleLabels[user.systemRole]}</Badge>
      </td>
      <td className={bodyCell}>{user.githubLinked ? 'Linked' : 'Not linked'}</td>
      <td className={bodyCell}>
        <RelativeTime iso={user.createdAt} />
      </td>
      <td className="py-2 align-top">
        {canChange && (
          <Button variant="secondary" onClick={onChangeRole} aria-label={`Make ${user.name} ${target === 'admin' ? 'an Admin' : 'a User'}`}>
            {target === 'admin' ? 'Make Admin' : 'Make User'}
          </Button>
        )}
      </td>
    </tr>
  )
}

/**
 * /admin/users (FR-3): every user and their system role, searched by email or name and paged. An Admin changes a
 * role after confirming; demoting the last Admin is refused by the API (409 CONFLICT) and the reason is shown.
 */
export function AdminUsersPage({
  search,
  onSearchChange,
}: {
  search: AdminUsersSearch
  onSearchChange: (next: AdminUsersSearch) => void
}) {
  useDocumentTitle('Users · Admin')
  const me = useCurrentUser()
  const users = useAdminUsers(usersQuery(search))
  const change = useChangeSystemRole()
  const [confirming, setConfirming] = useState<AdminUser | null>(null)
  const [outcome, setOutcome] = useState<{ tone: 'success' | 'error'; text: string } | null>(null)
  const { register, handleSubmit, reset, formState } = useForm<FilterValues>({
    resolver: zodResolver(filterSchema),
    values: { search: search.search ?? '' },
  })

  const clear = () => {
    reset({ search: '' })
    onSearchChange({})
  }

  const onSubmit = handleSubmit((v) => {
    onSearchChange({ search: v.search || undefined })
  })

  const confirmChange = () => {
    if (!confirming) {
      return
    }
    const target = otherRole(confirming.systemRole)
    const name = confirming.name
    change.mutate(
      { id: confirming.id, systemRole: target },
      {
        onSuccess: () => {
          setOutcome({ tone: 'success', text: `${name} is now ${target === 'admin' ? 'an Admin' : 'a User'}.` })
        },
        onError: (error) => {
          setOutcome({ tone: 'error', text: errorMessage(error, 'changeSystemRole') })
        },
        onSettled: () => {
          setConfirming(null)
        },
      },
    )
  }

  const target = confirming ? otherRole(confirming.systemRole) : 'user'
  const demotingMyself = confirming !== null && confirming.id === me?.id && target === 'user'

  return (
    <section aria-labelledby="admin-users-title">
      <h2 id="admin-users-title" className="mb-4 text-xl">
        Users
      </h2>
      <FilterForm label="Search users" onSubmit={(e) => void onSubmit(e)} onClear={clear}>
        <TextField
          id="users-search"
          type="search"
          label="Name or email"
          autoComplete="off"
          error={formState.errors.search?.message}
          {...register('search')}
        />
      </FilterForm>
      <div role={outcome?.tone === 'error' ? 'alert' : 'status'} aria-live={outcome?.tone === 'error' ? 'assertive' : 'polite'}>
        {outcome && (
          <Alert tone={outcome.tone} title={outcome.tone === 'success' ? 'Role changed' : 'The role wasn’t changed'} className="mb-4">
            <p>{outcome.text}</p>
          </Alert>
        )}
      </div>
      <PagedList
        query={users}
        noun="user"
        loadingLabel="Loading users…"
        errorTitle="The users couldn't load"
        filtered={Boolean(search.search)}
        emptyTitle="No users yet"
        emptyText="Users appear here when they register."
        onClearFilters={clear}
        onPage={(page) => {
          onSearchChange({ ...search, page: page > 1 ? page : undefined })
        }}
      >
        {(items) => (
          <AdminTable
            caption="Users, newest first, with their system role."
            headers={['Name', 'Email', 'System role', 'GitHub', 'Joined', 'Action']}
          >
            {items.map((u) => (
              <UserRow
                key={u.id}
                user={u}
                isMe={u.id === me?.id}
                canChange={canChangeSystemRoles(me)}
                onChangeRole={() => {
                  setOutcome(null)
                  setConfirming(u)
                }}
              />
            ))}
          </AdminTable>
        )}
      </PagedList>
      <ConfirmDialog
        open={confirming !== null}
        title={confirming ? `Make ${confirming.name} ${target === 'admin' ? 'an Admin' : 'a User'}?` : ''}
        confirmLabel={target === 'admin' ? 'Make Admin' : 'Make User'}
        pendingLabel="Changing…"
        pending={change.isPending}
        destructive={target === 'user'}
        onConfirm={confirmChange}
        onCancel={() => {
          setConfirming(null)
        }}
      >
        {target === 'admin' ? (
          <p>Admins can do everything on every project, see the admin pages, retry dead letters and change anyone’s role.</p>
        ) : demotingMyself ? (
          <p>You will lose access to the admin area and to projects you aren’t a member of. Another Admin would have to make you an Admin again.</p>
        ) : (
          <p>They will lose access to the admin area and to projects they aren’t a member of.</p>
        )}
      </ConfirmDialog>
    </section>
  )
}
