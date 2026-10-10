import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { ErrorSummary } from './alert'

describe('ErrorSummary (NFR-A11Y)', () => {
  it('NFR_A11Y_error_summary_links_meet_the_24px_target_size', () => {
    render(<ErrorSummary title="Fix these" errors={[{ fieldId: 'project-name', message: 'Enter a name' }]} />)
    const link = screen.getByRole('link', { name: 'Enter a name' })
    expect(link).toHaveAttribute('href', '#project-name')
    // WCAG 2.2 SC 2.5.8: an inline link is only as tall as its line box; inline-block + min-height 1.5rem (24 px).
    expect(link).toHaveClass('inline-block', 'min-h-6', 'min-w-6')
  })
})
