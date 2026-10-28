import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'

import { contextualErrorMessages } from '../../lib/errors'
import { signInAs } from '../../mocks/handlers/auth'
import { problem } from '../../mocks/handlers/problem'
import { projectsDb } from '../../mocks/handlers/projects'
import { stackDb } from '../../mocks/handlers/stack'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'

type User = ReturnType<typeof userEvent.setup>

const next = (user: User) => user.click(screen.getByRole('button', { name: 'Next' }))
const back = (user: User) => user.click(screen.getByRole('button', { name: 'Back' }))
const stepHeading = (name: RegExp) => screen.findByRole('heading', { level: 2, name })
const layerHeading = (name: string) => screen.findByRole('heading', { level: 3, name })
const bodiesOf = (method: string, suffix: string) =>
  projectsDb.requests.filter((r) => r.method === method && r.path.endsWith(suffix)).map((r) => r.body)
const pick = async (user: User, name: string) => {
  await user.click(await screen.findByRole('radio', { name }))
}

/** Source (scratch) → idea → repository (`local` or a GitHub URL + token) → Version → the Stack step, composing. */
async function wizardToStack({ repo = 'local' }: { repo?: 'local' | 'github_pat' } = {}) {
  signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  await renderRoute('/projects/new')
  const user = userEvent.setup()
  await stepHeading(/Step 1 of 8: Source/)
  await next(user)
  await user.type(screen.getByLabelText('Project name'), 'Storefront')
  await user.type(screen.getByLabelText('Your idea'), 'Sell posters online.')
  await next(user)
  await stepHeading(/Step 3 of 8: Repository/)
  if (repo === 'local') {
    await user.click(screen.getByRole('radio', { name: 'Local repository' }))
  } else {
    await user.type(screen.getByLabelText('Repository URL'), 'https://github.com/ada/storefront')
  }
  await next(user)
  await stepHeading(/Step 4 of 8: Version/)
  await next(user)
  await stepHeading(/Step 5 of 8: Stack/)
  // FR-114: "No stack (general)" stays the top choice and the default.
  expect(screen.getByRole('radio', { name: 'No stack (general)' })).toBeChecked()
  await user.click(screen.getByRole('radio', { name: 'Choose the stack' }))
  await layerHeading('Frontend')
  return user
}

describe('Wizard → Stack sub-steps (FR-114, FR-115, FR-119)', () => {
  it('FR114_stack_substeps_are_frontend_backend_data_vcs_deployment_in_order', async () => {
    const user = await wizardToStack()

    const layers = screen.getByRole('list', { name: 'Stack layers' })
    expect(within(layers).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      '1. Frontend',
      '2. Backend',
      '3. Data',
      '4. VCS',
      '5. Deployment',
    ])
    // A sub-step can't be skipped: the frontend (or None) must be chosen first.
    await next(user)
    expect(await screen.findAllByText('Choose the frontend, or None.')).not.toHaveLength(0)
    expect(screen.getByRole('heading', { level: 3, name: 'Frontend' })).toBeInTheDocument()

    await pick(user, 'React + Vite')
    await next(user)
    await layerHeading('Backend')
    // FR-114 example: Frontend = React + Vite → every backend is allowed.
    expect(screen.queryByText(/Not available with your choices/)).not.toBeInTheDocument()
    for (const name of ['ASP.NET Core', 'FastAPI', 'Express', 'None']) {
      expect(screen.getByRole('radio', { name })).not.toHaveAttribute('aria-disabled')
    }
    await pick(user, 'ASP.NET Core')
    await next(user)
    await layerHeading('Data')
    await pick(user, 'EF Core + PostgreSQL')
    await next(user)
    await layerHeading('VCS')
    await pick(user, 'GitHub')
    await next(user)
    await layerHeading('Deployment')
    expect(screen.getByRole('heading', { level: 2, name: /Step 5 of 8: Stack/ })).toBeInTheDocument()

    // Back walks the sub-steps before leaving the step.
    await back(user)
    await layerHeading('VCS')
  })

  it('FR114_forbidden_options_are_listed_with_their_reasons_and_never_selected', async () => {
    const user = await wizardToStack()
    await pick(user, 'React + Vite')
    await next(user)
    await pick(user, 'ASP.NET Core')
    await next(user)
    await layerHeading('Data')

    // FR-114 example: Backend = ASP.NET Core → EF Core + PostgreSQL, EF Core + SQL Server (caveat) and None.
    const group = screen.getByRole('group', { name: 'Data' })
    expect(within(group).getByText(/Not available with your choices \(3\)/)).toBeInTheDocument()
    const prisma = within(group).getByRole('radio', { name: 'Prisma' })
    expect(prisma).toHaveAttribute('aria-disabled', 'true')
    expect(prisma).not.toBeDisabled()
    expect(prisma).toHaveAccessibleDescription(/Prisma needs Express, NestJS or Next\.js full-stack/)
    // The reason is always visible text, not a tooltip.
    expect(within(group).getByText(/Prisma needs Express, NestJS or Next\.js full-stack/)).toBeInTheDocument()
    expect(within(group).getByRole('radio', { name: 'EF Core + SQL Server' })).toHaveAccessibleDescription(
      /No managed SQL Server on the catalog's platforms/,
    )
    expect(within(group).getByRole('radio', { name: 'EF Core + PostgreSQL' })).toHaveAccessibleDescription(
      /EF Core migrations on PostgreSQL/,
    )

    // Keyboard: the forbidden card stays focusable, and choosing it does nothing.
    prisma.focus()
    expect(prisma).toHaveFocus()
    await user.click(prisma)
    expect(prisma).not.toBeChecked()
    await user.keyboard(' ')
    expect(prisma).not.toBeChecked()
  })

  it('FR114_changing_an_earlier_layer_clears_the_invalidated_choice_with_a_toast', async () => {
    const user = await wizardToStack()
    await pick(user, 'React + Vite')
    await next(user)
    await pick(user, 'FastAPI')
    await next(user)
    await pick(user, 'SQLAlchemy')
    await next(user)
    await layerHeading('VCS')

    // Back to Backend through the sub-step list, then FastAPI → ASP.NET Core.
    await user.click(screen.getByRole('button', { name: '2. Backend' }))
    await layerHeading('Backend')
    await pick(user, 'ASP.NET Core')

    expect(await screen.findByText('SQLAlchemy was cleared: it needs FastAPI')).toBeInTheDocument()
    await next(user)
    await layerHeading('Data')
    await waitFor(() => {
      expect(screen.getByRole('radio', { name: 'EF Core + PostgreSQL' })).not.toBeChecked()
    })
    expect(screen.getByRole('radio', { name: 'SQLAlchemy' })).toHaveAttribute('aria-disabled', 'true')
    await next(user)
    expect(await screen.findAllByText('Choose the data layer, or None.')).not.toHaveLength(0)
  })

  it('FR114_FR115_summary_shows_the_composition_specialists_and_origin_model_and_create_sends_stack', async () => {
    const user = await wizardToStack()
    await pick(user, 'React + Vite')
    await next(user)
    await pick(user, 'ASP.NET Core')
    await next(user)
    await pick(user, 'EF Core + SQL Server')
    await next(user)
    await pick(user, 'GitHub')
    await next(user)
    await layerHeading('Deployment')

    // FR-115: the Deployment choices, filtered by the layers before (Vercel can't run ASP.NET Core).
    await user.click(within(screen.getByRole('group', { name: 'Frontend host' })).getByRole('radio', { name: 'Render' }))
    const backendHost = screen.getByRole('group', { name: 'Backend host' })
    expect(within(backendHost).getByRole('radio', { name: 'Vercel' })).toHaveAccessibleDescription(/Vercel can't run ASP\.NET Core/)
    // "Same as frontend" is preselected for the backend host; Compose is the default dev loop.
    await waitFor(() => {
      expect(within(backendHost).getByRole('radio', { name: 'Render' })).toBeChecked()
    })
    expect(within(screen.getByRole('group', { name: 'Local dev loop' })).getByRole('radio', { name: 'Docker Compose' })).toBeChecked()
    // Origin model: proxy (default) or cross-origin, switchable.
    const origin = screen.getByRole('group', { name: 'Origin model' })
    await waitFor(() => {
      expect(within(origin).getByRole('radio', { name: 'Proxy' })).toBeChecked()
    })

    const summary = screen.getByRole('region', { name: 'Stack summary' })
    await waitFor(() => {
      expect(summary).toHaveTextContent('Backend engineer')
    })
    expect(summary).toHaveTextContent('ASP.NET Core (backend)')
    expect(summary).toHaveTextContent('Origin modelProxy')
    expect(summary).toHaveTextContent(/No managed SQL Server on the catalog's platforms/)

    await next(user)
    await stepHeading(/Step 6 of 8: Configuration/)
    await next(user)
    await stepHeading(/Step 7 of 8: Deployment/)
    await next(user)
    await stepHeading(/Step 8 of 8: Review & create/)
    expect(screen.getByRole('region', { name: 'Stack' })).toHaveTextContent('EF Core + SQL Server')
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Storefront' })

    const body = bodiesOf('POST', '/api/projects')[0]
    expect(body).not.toHaveProperty('presetId')
    // A Local repository has no host, so the VCS layer is sent (R5).
    expect(body).toMatchObject({
      stack: {
        frontend: { pack: 'fe-react-vite', path: 'frontend' },
        backend: { pack: 'be-aspnet', path: 'backend' },
        data: { pack: 'data-efcore-sqlserver' },
        vcs: { pack: 'vcs-github' },
        deploy: { frontendHost: 'deploy-render', backendHost: 'deploy-render', local: 'compose' },
        originModel: 'proxy',
      },
    })
  })

  it('FR117_FR119_the_VCS_is_read_only_for_a_GitHub_repository_and_path_errors_land_on_the_fields', async () => {
    const user = await wizardToStack({ repo: 'github_pat' })
    await pick(user, 'React + Vite')
    await next(user)
    await pick(user, 'ASP.NET Core')
    // FR-119: the backend's folder may not be the frontend's.
    const backendPath = screen.getByLabelText('Backend folder')
    await user.clear(backendPath)
    await user.type(backendPath, 'frontend')
    await next(user)
    await pick(user, 'None')
    await next(user)
    await layerHeading('VCS')

    // R5: fixed to the repository's host, nothing to choose.
    expect(screen.queryByRole('radio', { name: 'GitLab' })).not.toBeInTheDocument()
    expect(screen.getByText(/Set from the repository you chose \(GitHub\)/)).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '2. Backend' }))
    expect(await screen.findByText("must differ from the frontend path ('frontend').")).toBeInTheDocument()
    expect(screen.getByLabelText('Backend folder')).toHaveAttribute('aria-invalid', 'true')

    await user.clear(screen.getByLabelText('Backend folder'))
    await user.type(screen.getByLabelText('Backend folder'), 'api')
    await next(user)
    await next(user)
    await next(user)
    await layerHeading('Deployment')
    await next(user)
    await stepHeading(/Step 6 of 8: Configuration/)
    await next(user)
    await next(user)
    await stepHeading(/Step 8 of 8: Review & create/)
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Storefront' })
    const body = bodiesOf('POST', '/api/projects')[0] as { stack: Record<string, unknown> }
    // The server sets the VCS from the GitHub repository.
    expect(body.stack).not.toHaveProperty('vcs')
    expect(body.stack).toMatchObject({ backend: { pack: 'be-aspnet', path: 'api' } })
  })

  it('FR116_a_STACK_INCOMPATIBLE_create_returns_to_the_stack_step_with_the_reasons', async () => {
    const user = await wizardToStack()
    await pick(user, 'Vue')
    await next(user)
    await pick(user, 'ASP.NET Core')
    await next(user)
    await pick(user, 'None')
    await next(user)
    await pick(user, 'GitHub')
    await next(user)
    await layerHeading('Deployment')
    server.use(
      http.post('*/api/projects', () =>
        problem(400, 'STACK_INCOMPATIBLE', 'Incompatible.', {
          violations: [{ layer: 'data', option: 'data-prisma', rule: 'R3', reason: 'Prisma needs Express, NestJS or Next.js full-stack' }],
        }),
      ),
    )
    await next(user)
    await next(user)
    await next(user)
    await stepHeading(/Step 8 of 8: Review & create/)
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await stepHeading(/Step 5 of 8: Stack/)
    expect(
      await screen.findByText("These stack choices don't work together: Prisma needs Express, NestJS or Next.js full-stack."),
    ).toBeInTheDocument()
  })
})

describe('Wizard → import detection (FR-118)', () => {
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

  it('FR118_detected_options_are_pre_filled_and_marked_with_their_evidence_and_conflicts_show_every_candidate', async () => {
    stackDb.detection = {
      ref: 'main',
      commit: 'c0ffee1',
      suggestions: [
        { choice: 'frontend', option: 'fe-vue', path: 'web', mode: null, evidence: ['`vue` 3.5 in `web/package.json`'] },
        { choice: 'backend', option: 'none', path: null, mode: null, evidence: ['no server project'] },
        { choice: 'data', option: 'data-supabase', path: null, mode: 'baas', evidence: ['`supabase/config.toml`'] },
        { choice: 'vcs', option: 'vcs-github', path: null, mode: null, evidence: [] },
      ],
      conflicts: [
        {
          choice: 'frontendHost',
          candidates: [
            { option: 'deploy-vercel', path: null, evidence: ['`vercel.json`'] },
            { option: 'deploy-netlify', path: null, evidence: ['`netlify.toml`'] },
          ],
        },
      ],
      truncated: false,
    }
    const user = await importToStack()

    // Pre-filled: the step switches to composing, and says so.
    expect(await screen.findByText('Stack detected')).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Choose the stack' })).toBeChecked()
    expect(bodiesOf('POST', '/api/stack/detect')[0]).toEqual({ git: { provider: 'local', repo: '/data/import/shop', ref: 'main' } })
    const vue = await screen.findByRole('radio', { name: 'Vue' })
    expect(vue).toBeChecked()
    expect(vue).toHaveAccessibleDescription(/`vue` 3\.5 in `web\/package\.json`/)
    expect(within(screen.getByRole('group', { name: 'Frontend' })).getByText('Detected')).toBeInTheDocument()
    expect(screen.getByLabelText('Frontend folder')).toHaveValue('web')

    // Everything stays editable.
    await pick(user, 'React + Vite')
    expect(screen.getByRole('radio', { name: 'React + Vite' })).toBeChecked()
    await next(user)
    await layerHeading('Backend')
    expect(screen.getByRole('radio', { name: 'None' })).toBeChecked()
    await next(user)
    await layerHeading('Data')
    expect(screen.getByRole('radio', { name: 'Supabase (BaaS)' })).toBeChecked()
    await next(user)
    await next(user)
    await layerHeading('Deployment')
    expect(screen.getByText("Couldn't decide: Vercel or Netlify")).toBeInTheDocument()
    expect(screen.getByText(/`vercel\.json`/)).toBeInTheDocument()
    expect(screen.getByText(/`netlify\.toml`/)).toBeInTheDocument()
  })

  it('FR118_a_detection_failure_is_a_non_blocking_notice', async () => {
    stackDb.detectError = () => problem(409, 'GIT_NOT_CONNECTED', 'No GitHub connection.')
    const user = await importToStack()

    expect(await screen.findByText(contextualErrorMessages.detectStack.GIT_NOT_CONNECTED)).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'No stack (general)' })).toBeChecked()
    await next(user)
    await stepHeading(/Step 6 of 8: Configuration/)
  })

  it('FR118_a_detection_that_answers_after_the_user_left_the_Stack_step_changes_nothing', async () => {
    let release: () => void = () => undefined
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })
    server.use(
      http.post('*/api/stack/detect', async () => {
        await gate
        return HttpResponse.json({
          ref: 'main',
          commit: 'c0ffee1',
          suggestions: [{ choice: 'frontend', option: 'fe-vue', path: 'web', mode: null, evidence: ['`vue` in package.json'] }],
          conflicts: [],
          truncated: false,
        })
      }),
    )
    const user = await importToStack()
    expect(await screen.findByText(/Detecting the repository/)).toBeInTheDocument()
    await next(user)
    await stepHeading(/Step 6 of 8: Configuration/)
    release()
    await new Promise((resolve) => setTimeout(resolve, 50))

    await next(user)
    await stepHeading(/Step 7 of 8: Deployment/)
    await next(user)
    await stepHeading(/Step 8 of 8: Review & create/)
    expect(screen.getByRole('region', { name: 'Stack' })).toHaveTextContent('No stack (general)')
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Shop' })
    expect(bodiesOf('POST', '/api/projects')[0]).not.toHaveProperty('stack')
  })

  it('FR118_a_new_repository_re_applies_detection_while_the_stack_is_untouched_and_badges_follow_it', async () => {
    server.use(
      http.post('*/api/stack/detect', async ({ request }) => {
        const body = (await request.json()) as { git: { repo: string } }
        const api = body.git.repo.endsWith('/api')
        return HttpResponse.json({
          ref: 'main',
          commit: api ? 'b2' : 'a1',
          suggestions: api
            ? [
                { choice: 'frontend', option: 'none', path: null, mode: null, evidence: ['no web app'] },
                { choice: 'backend', option: 'be-fastapi', path: 'api', mode: null, evidence: ['`fastapi` in `api/pyproject.toml`'] },
              ]
            : [{ choice: 'frontend', option: 'fe-vue', path: 'web', mode: null, evidence: ['`vue` in `web/package.json`'] }],
          conflicts: [],
          truncated: false,
        })
      }),
    )
    const user = await importToStack()
    expect(await screen.findByRole('radio', { name: 'Vue' })).toBeChecked()

    // Back to the repository (Stack → Version → Repository), point at another one, and return.
    await back(user)
    await stepHeading(/Step 4 of 8: Version/)
    await back(user)
    await stepHeading(/Step 3 of 8: Repository/)
    const path = screen.getByLabelText(/Path of an existing repository on the server/)
    await user.clear(path)
    await user.type(path, '/data/import/api')
    await next(user)
    await next(user)
    await stepHeading(/Step 5 of 8: Stack/)

    const frontend = await screen.findByRole('group', { name: 'Frontend' })
    await waitFor(() => {
      expect(within(frontend).getByRole('radio', { name: 'None' })).toBeChecked()
    })
    expect(within(frontend).getByRole('radio', { name: 'Vue' })).not.toBeChecked()
    expect(within(frontend).getByRole('radio', { name: 'None' })).toHaveAccessibleDescription(/no web app/)
    expect(within(frontend).getByRole('radio', { name: 'Vue' })).not.toHaveAccessibleDescription(/package\.json/)

    // Touched: a later detection no longer overwrites the user's choice.
    await user.click(within(frontend).getByRole('radio', { name: 'React + Vite' }))
    await back(user)
    await back(user)
    await stepHeading(/Step 3 of 8: Repository/)
    await user.clear(screen.getByLabelText(/Path of an existing repository on the server/))
    await user.type(screen.getByLabelText(/Path of an existing repository on the server/), '/data/import/shop')
    await next(user)
    await next(user)
    await stepHeading(/Step 5 of 8: Stack/)
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(await screen.findByRole('radio', { name: 'React + Vite' })).toBeChecked()
    expect(screen.getByRole('radio', { name: 'Vue' })).not.toBeChecked()
  })
})
