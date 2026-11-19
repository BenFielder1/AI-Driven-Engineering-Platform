import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { MarkdownView } from './markdown-view'

describe('MarkdownView code blocks (NFR-A11Y)', () => {
  it('NFR_A11Y_code_blocks_are_named_keyboard_reachable_groups', async () => {
    render(<MarkdownView label="Plan" content={'Intro\n\n```json\n{"steps": []}\n```\n\n```\nplain\n```\n'} />)
    const json = screen.getByRole('group', { name: 'Code block (json)' })
    const plain = screen.getByRole('group', { name: 'Code block' })
    expect(json.tagName).toBe('PRE')
    expect(json).toHaveAttribute('tabindex', '0')

    await userEvent.tab()
    expect(json).toHaveFocus()
    await userEvent.tab()
    expect(plain).toHaveFocus()
  })
})
