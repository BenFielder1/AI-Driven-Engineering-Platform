import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { errorMessages } from '../../lib/errors'
import { signInAs } from '../../mocks/handlers/auth'
import { projectsDb } from '../../mocks/handlers/projects'
import { renderRoute } from '../../test/render'

type User = ReturnType<typeof userEvent.setup>

const next = (user: User) => user.click(screen.getByRole('button', { name: 'Next' }))
const stepHeading = (name: RegExp) => screen.findByRole('heading', { level: 2, name })

const createBody = () =>
  projectsDb.requests.find((r) => r.method === 'POST' && r.path === '/api/projects')?.body as Schemas['CreateProjectRequest'] | undefined

/** A Local import (a repository with code) up to the Stack step. */
async function importToStack() {
  signInAs({ systemRole: 'user' })
  await renderRoute('/projects/new')
  const user = userEvent.setup()
  await stepHeading(/Step 1 of 8: Source/)
  await user.click(screen.getByRole('radio', { name: 'Import an existing repository' }))
  await next(user)
  await user.type(screen.getByLabelText('Project name'), 'Shop')
  await user.type(screen.getByLabelText('What should this version deliver?'), 'Add CSV export')
  await next(user)
  await stepHeading(/Step 3 of 8: Repository/)
  await user.click(screen.getByRole('radio', { name: 'Local repository' }))
  await user.type(screen.getByLabelText(/Path of an existing repository on the server/), '/data/import/shop')
  await next(user)
  await stepHeading(/Step 4 of 8: Version/)
  await next(user)
  await stepHeading(/Step 5 of 8: Stack/)
  return user
}

describe('Wizard → Infer the stack (FR-178)', () => {
  it('FR178_import_with_code_preselects_infer_and_skips_the_layers_and_the_deployment_step', async () => {
    const user = await importToStack()

    const group = screen.getByRole('group', { name: 'How should the agents build it?' })
    expect(within(group).getAllByRole('radio').map((r) => r.getAttribute('value'))).toEqual(['infer', 'compose', 'general'])
    expect(within(group).getByRole('radio', { name: 'Infer from the repository (recommended)' })).toBeChecked()
    expect(screen.getByText('The stack is inferred after the project is created')).toBeInTheDocument()
    expect(screen.getByText(/On the Analysis page you review its findings/)).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Stack layers' })).not.toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Wizard steps' })).getByText(/7\. Deployment \(on the Analysis page\)/)).toBeInTheDocument()

    await next(user)
    await stepHeading(/Step 6 of 8: Configuration/)
    await next(user)
    // The Deployment step is skipped.
    await stepHeading(/Step 8 of 8: Review & create/)
    expect(screen.getByRole('region', { name: 'Stack' })).toHaveTextContent('Infer from the repository (reviewed on the Analysis page)')
    expect(screen.getByRole('region', { name: 'Deployment' })).toHaveTextContent('Set up on the Analysis page, once the stack is approved')

    // Back from the review returns to Configuration, not the skipped Deployment step.
    await user.click(screen.getByRole('button', { name: 'Back' }))
    await stepHeading(/Step 6 of 8: Configuration/)
    await next(user)
    await stepHeading(/Step 8 of 8: Review & create/)

    await user.click(screen.getByRole('button', { name: 'Create project' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Shop' })).toBeInTheDocument()
    expect(createBody()).toMatchObject({ source: 'import', stackSource: 'infer' })
    expect(createBody()).not.toHaveProperty('stack')
    expect(createBody()).not.toHaveProperty('deployment')
    // Given Infer, the version starts in analysis.
    expect(projectsDb.projects.at(-1)?.currentPhase).toBe('analysis')
  })

  it('FR178_choose_manually_keeps_the_v4_layer_sub_steps', async () => {
    const user = await importToStack()
    await user.click(screen.getByRole('radio', { name: 'Choose manually' }))
    expect(await screen.findByRole('list', { name: 'Stack layers' })).toBeInTheDocument()
    expect(screen.queryByText('The stack is inferred after the project is created')).not.toBeInTheDocument()
  })

  it('FR178_a_repository_with_no_code_returns_to_the_stack_step_with_the_copy', async () => {
    projectsDb.inferHasCode = false
    const user = await importToStack()
    await next(user)
    await stepHeading(/Step 6 of 8: Configuration/)
    await next(user)
    await stepHeading(/Step 8 of 8: Review & create/)
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await stepHeading(/Step 5 of 8: Stack/)
    expect(await screen.findByText(errorMessages.STACK_INFER_NO_CODE)).toBeInTheDocument()
  })

  it('FR178_a_scratch_project_does_not_see_infer', async () => {
    signInAs({ systemRole: 'user' })
    await renderRoute('/projects/new')
    const user = userEvent.setup()
    await stepHeading(/Step 1 of 8: Source/)
    await next(user)
    await user.type(await screen.findByLabelText('Project name'), 'Shop')
    await user.type(screen.getByLabelText('Your idea'), 'A shop')
    await next(user)
    await stepHeading(/Step 3 of 8: Repository/)
    await user.click(screen.getByRole('radio', { name: 'Local repository' }))
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await next(user)
    await stepHeading(/Step 5 of 8: Stack/)

    expect(screen.queryByRole('radio', { name: /Infer from the repository/ })).not.toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'No stack (general)' })).toBeChecked()
    expect(screen.getByRole('radio', { name: 'Choose the stack' })).toBeInTheDocument()
  })
})
