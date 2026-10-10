import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useRef, useState } from 'react'
import { describe, expect, it } from 'vitest'

import { Dialog } from './dialog'

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
      <Dialog
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
      </Dialog>
    </>
  )
}

describe('useModalFocus (NFR-A11Y)', () => {
  it('returns focus to the opener when it still exists', async () => {
    const user = userEvent.setup()
    render(<Harness removeOpener={false} />)
    await user.click(screen.getByRole('button', { name: 'Open' }))
    expect(screen.getByRole('button', { name: 'Inside' })).toHaveFocus()
    await user.keyboard('{Escape}')
    expect(screen.getByRole('button', { name: 'Open' })).toHaveFocus()
  })

  it('falls back to the stable target when the opener has left the DOM', async () => {
    const user = userEvent.setup()
    render(<Harness removeOpener />)
    await user.click(screen.getByRole('button', { name: 'Open' }))
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('button', { name: 'Open' })).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Fallback' })).toHaveFocus()
  })
})
