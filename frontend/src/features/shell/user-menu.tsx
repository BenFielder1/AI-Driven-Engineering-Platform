import { Link, useRouter } from '@tanstack/react-router'
import { useEffect, useId, useRef, useState } from 'react'

import { errorMessage } from '../../lib/errors'
import { useLogout, type User } from '../auth/auth-api'

const roleLabel = { admin: 'Admin', user: 'User' } as const

/** User menu (disclosure): who is signed in, their system role (FR-3), Account and Sign out (FR-1). */
export function UserMenu({ user }: { user: User }) {
  const [open, setOpen] = useState(false)
  const panelId = useId()
  const containerRef = useRef<HTMLDivElement>(null)
  const buttonRef = useRef<HTMLButtonElement>(null)
  const router = useRouter()
  const logout = useLogout()

  useEffect(() => {
    if (!open) {
      return undefined
    }
    function onPointerDown(event: PointerEvent) {
      if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
        setOpen(false)
      }
    }
    document.addEventListener('pointerdown', onPointerDown)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown)
    }
  }, [open])

  function signOut() {
    logout.mutate(undefined, {
      onSuccess: () => {
        void router.navigate({ to: '/login' })
      },
    })
  }

  const initial = user.name.trim().charAt(0).toUpperCase() || '?'

  return (
    <div
      ref={containerRef}
      className="relative"
      onKeyDown={(e) => {
        if (e.key === 'Escape' && open) {
          setOpen(false)
          buttonRef.current?.focus()
        }
      }}
    >
      <button
        ref={buttonRef}
        type="button"
        aria-expanded={open}
        aria-controls={panelId}
        onClick={() => {
          setOpen((v) => !v)
        }}
        className="flex min-h-9 items-center gap-2 rounded-md px-2 text-sm hover:bg-surface"
      >
        {user.avatarUrl ? (
          <img src={user.avatarUrl} alt="" className="h-7 w-7 rounded-full" />
        ) : (
          <span
            aria-hidden="true"
            className="flex h-7 w-7 items-center justify-center rounded-full bg-primary text-xs font-semibold text-on-primary"
          >
            {initial}
          </span>
        )}
        <span className="sr-only">Account menu for </span>
        <span className="hidden max-w-40 truncate sm:inline">{user.name}</span>
        <span className="sr-only sm:hidden">{user.name}</span>
      </button>

      {open && (
        <div
          id={panelId}
          className="absolute right-0 z-40 mt-2 w-64 max-w-[calc(100vw-2rem)] rounded-lg border border-border bg-surface-raised p-2 shadow-lg"
        >
          <div className="border-b border-border px-2 pb-2">
            <p className="truncate font-medium">{user.name}</p>
            <p className="truncate text-sm text-muted">{user.email}</p>
            <p className="mt-1 text-xs text-muted">Role: {roleLabel[user.systemRole]}</p>
          </div>
          <ul className="mt-2 flex flex-col">
            <li>
              <Link
                to="/account"
                onClick={() => {
                  setOpen(false)
                }}
                className="block rounded-md px-2 py-2 text-sm hover:bg-surface"
              >
                Account
              </Link>
            </li>
            <li>
              <button
                type="button"
                onClick={signOut}
                disabled={logout.isPending}
                className="block w-full rounded-md px-2 py-2 text-left text-sm hover:bg-surface"
              >
                {logout.isPending ? 'Signing out…' : 'Sign out'}
              </button>
            </li>
          </ul>
          {logout.isError && (
            <p role="alert" className="px-2 pt-1 text-sm text-danger">
              {errorMessage(logout.error)}
            </p>
          )}
        </div>
      )}
    </div>
  )
}
