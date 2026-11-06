import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'

import { THEME_STORAGE_KEY } from '../../lib/theme'
import { axeViolations, setTheme, THEMES } from '../../test/axe'
import { ThemeSelector } from './theme-selector'

afterEach(() => {
  document.documentElement.className = ''
})

describe('FR-204: ThemeSelector', () => {
  for (const theme of THEMES) {
    for (const compact of [false, true]) {
      it(`FR204_selector_axe_${theme}${compact ? '_compact' : ''}: a "Theme" radio group with no axe violations`, async () => {
        setTheme(theme)
        const { container } = render(<ThemeSelector compact={compact} />)
        expect(screen.getByRole('group', { name: 'Theme' })).toBeInTheDocument()
        expect(screen.getAllByRole('radio').map((r) => r.getAttribute('value'))).toEqual(['system', 'light', 'dark'])
        expect(await axeViolations(container)).toEqual([])
      })
    }
  }

  it('FR204_selector_default: System is selected when nothing is stored', () => {
    render(<ThemeSelector />)
    expect(screen.getByRole('radio', { name: 'System' })).toBeChecked()
  })

  it('FR204_FR201_selector_stored: a theme saved before 0.6.0 (aide.theme = dark) is shown as the choice', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'dark')
    render(<ThemeSelector />)
    expect(screen.getByRole('radio', { name: 'Dark' })).toBeChecked()
  })

  it('FR204_selector_choose: choosing Light then System applies and stores each choice; arrow keys move between options', async () => {
    const user = userEvent.setup()
    render(<ThemeSelector />)

    await user.click(screen.getByRole('radio', { name: 'Light' }))
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light')
    expect(document.documentElement).toHaveClass('light')
    expect(screen.getByRole('radio', { name: 'Light' })).toBeChecked()

    await user.keyboard('{ArrowRight}')
    expect(screen.getByRole('radio', { name: 'Dark' })).toBeChecked()
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark')
    expect(document.documentElement).toHaveClass('dark')

    await user.click(screen.getByRole('radio', { name: 'System' }))
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('system')
    // The test OS (src/test/setup.ts matchMedia) has no dark preference, so System is light.
    expect(document.documentElement).toHaveClass('light')
  })

  it('FR205_selector_targets: each option is at least 32 x 32 px', () => {
    render(<ThemeSelector compact />)
    for (const radio of screen.getAllByRole('radio')) {
      expect(radio.closest('label')).toHaveClass('min-h-8', 'min-w-8')
    }
  })
})
