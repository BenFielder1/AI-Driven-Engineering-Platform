import { describe, expect, it } from 'vitest'

import { ApiError, errorMessage, errorMessages, toApiError } from '../../lib/errors'
import { evaluate, stackCatalog } from '../../mocks/handlers/stack-catalog'
import {
  applyOptions,
  clearedMessage,
  emptyStackSelection,
  selectionFromDetection,
  stackFieldOfKey,
  toStackRequest,
  type StackSelection,
} from './stack-model'

const sel = (overrides: Partial<StackSelection>): StackSelection => ({ ...emptyStackSelection, ...overrides })

describe('Stack model (FR-114, FR-116, FR-119)', () => {
  it('FR114_cleared_toast_text_names_the_option_and_the_server_reason', () => {
    expect(clearedMessage('SQLAlchemy', 'SQLAlchemy needs FastAPI')).toBe('SQLAlchemy was cleared: it needs FastAPI')
    expect(clearedMessage('Railway', 'Railway only builds from GitHub.')).toBe('Railway was cleared: it only builds from GitHub')
    expect(clearedMessage('Vercel', "There's no backend to host")).toBe("Vercel was cleared: there's no backend to host")
  })

  it('FR114_applyOptions_clears_invalidated_choices_from_the_server_answer_only', () => {
    const value = sel({ frontend: 'fe-react-vite', backend: 'be-aspnet', data: 'data-sqlalchemy', vcs: 'vcs-github' })
    const { selection, cleared } = applyOptions(value, evaluate({ ...value }), stackCatalog)
    expect(selection.data).toBe('')
    expect(selection.backend).toBe('be-aspnet')
    expect(cleared).toEqual([{ choice: 'data', option: 'data-sqlalchemy', message: 'SQLAlchemy was cleared: it needs FastAPI' }])

    // Nothing to clear: the same object comes back.
    const fine = sel({ frontend: 'fe-react-vite', backend: 'be-aspnet' })
    expect(applyOptions(fine, evaluate({ ...fine }), stackCatalog).selection).toBe(fine)
  })

  it('FR115_applyOptions_preselects_the_deployment_defaults_on_the_deployment_substep', () => {
    const value = sel({ frontend: 'fe-react-vite', backend: 'be-aspnet', data: 'none', vcs: 'vcs-github', frontendHost: 'deploy-railway' })
    const { selection } = applyOptions(value, evaluate({ ...value }), stackCatalog, { fillDefaults: true })
    expect(selection).toMatchObject({ backendHost: 'deploy-railway', local: 'compose', originModel: 'proxy' })
    expect(applyOptions(value, evaluate({ ...value }), stackCatalog).selection.backendHost).toBe('')
  })

  it('FR119_requests_carry_layer_paths_and_omit_unchosen_layers', () => {
    const value = sel({ frontend: 'fe-nextjs', backend: 'none', vcs: 'vcs-github', frontendPath: '.' })
    expect(toStackRequest(value)).toEqual({ frontend: { pack: 'fe-nextjs', path: '.' }, backend: { pack: 'none' }, vcs: { pack: 'vcs-github' } })
    expect(toStackRequest(value, { withPaths: false, omitVcs: true })).toEqual({
      frontend: { pack: 'fe-nextjs' },
      backend: { pack: 'none' },
    })
    expect(stackFieldOfKey('stack.backend.path')).toBe('backendPath')
    expect(stackFieldOfKey('frontend.path')).toBe('frontendPath')
    expect(stackFieldOfKey('stack.deploy.frontendHost')).toBe('frontendHost')
    expect(stackFieldOfKey('stack.data.mode')).toBe('data')
    expect(stackFieldOfKey('git.mode')).toBeUndefined()
  })

  it('FR118_detection_fills_the_suggested_choices_and_paths_only', () => {
    const filled = selectionFromDetection(
      {
        ref: 'main',
        commit: 'c',
        suggestions: [{ choice: 'frontend', option: 'fe-vue', path: 'apps/web', mode: null, evidence: [] }],
        conflicts: [{ choice: 'backend', candidates: [] }],
        truncated: false,
      },
      emptyStackSelection,
    )
    expect(filled).toEqual({ ...emptyStackSelection, frontend: 'fe-vue', frontendPath: 'apps/web' })
  })

  it('FR116_STACK_INCOMPATIBLE_copy_appends_the_violation_reasons', () => {
    const error = toApiError(
      {
        errorCode: 'STACK_INCOMPATIBLE',
        violations: [
          { layer: 'data', option: 'data-prisma', rule: 'R3', reason: 'Prisma needs Express, NestJS or Next.js full-stack' },
          { layer: 'backendHost', option: 'deploy-vercel', rule: 'R7', reason: "Vercel can't run ASP.NET Core." },
          { bogus: true },
        ],
      },
      new Response(null, { status: 400 }),
    )
    expect(error).toBeInstanceOf(ApiError)
    expect(error.violations).toHaveLength(2)
    expect(errorMessage(error)).toBe(
      "These stack choices don't work together: Prisma needs Express, NestJS or Next.js full-stack; Vercel can't run ASP.NET Core.",
    )
    expect(errorMessage(toApiError({ errorCode: 'STACK_INCOMPATIBLE' }, new Response(null, { status: 400 })))).toBe(
      errorMessages.STACK_INCOMPATIBLE,
    )
    expect(errorMessages.VCS_MISMATCH).toBe("The version control choice must match the project's repository host.")
  })
})
