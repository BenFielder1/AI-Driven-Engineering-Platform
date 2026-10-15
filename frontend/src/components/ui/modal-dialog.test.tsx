import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useRef, useState } from 'react'
import { describe, expect, it } from 'vitest'

import { ModalDialog } from './modal-dialog'

function Harness({ removeOpener }: { removeOpener: boolean }) {
  const [open, setOpen] = useState(false)
  const [openerShown, setOpenerShown] = useState(true)
  const fallback = useRef<HTMLHeadingElement>(null)
  return (
    <>
      <h2 ref={fallback} tabIndex={-1}>
        Fallback
      </h2>
      {openerShown && (
        <button
          type="button"
          onClick={() => {
            setOpen(true)
          }}
        >
          Open
        </button>
      )}
      <ModalDialog
        open={open}
        title="Test dialog"
        fallbackFocusRef={fallback}
        onClose={() => {
          setOpen(false)
          if (removeOpener) {
            setOpenerShown(false)
          }
        }}
      >
        <button type="button">Inside</button>
      </ModalDialog>
    </>
  )
}

describe('ModalDialog focus (NFR-A11Y, FR53_shadcn_dialog)', () => {
  it('returns focus to the opener when it still exists', async () => {
    const user = userEvent.setup()
    render(<Harness removeOpener={false} />)
    await user.click(screen.getByRole('button', { name: 'Open' }))
    expect(screen.getByRole('button', { name: 'Inside' })).toHaveFocus()
    await user.keyboard('{Escape}')
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Open' })).toHaveFocus()
    })
  })

  it('falls back to the stable target when the opener has left the DOM', async () => {
    const user = userEvent.setup()
    render(<Harness removeOpener />)
    await user.click(screen.getByRole('button', { name: 'Open' }))
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('button', { name: 'Open' })).not.toBeInTheDocument()
    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Fallback' })).toHaveFocus()
    })
  })

  it('ignores Escape while busy and never closes on an outside click', async () => {
    const user = userEvent.setup()
    let closed = 0
    render(
      <ModalDialog
        open
        busy
        title="Busy dialog"
        onClose={() => {
          closed += 1
        }}
      >
        <button type="button">Inside</button>
      </ModalDialog>,
    )
    expect(screen.getByRole('dialog', { name: 'Busy dialog' })).toBeInTheDocument()
    await user.keyboard('{Escape}')
    expect(closed).toBe(0)
  })
})
