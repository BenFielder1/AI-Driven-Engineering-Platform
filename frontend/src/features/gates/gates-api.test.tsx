import { QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { describe, expect, it, vi } from 'vitest'

import { engineKeys } from '../../engine/api/keys'
import { seedGate } from '../../mocks/handlers/lifecycle'
import { seedProjectAs } from '../../test/artifacts'
import { createTestQueryClient } from '../../test/render'
import { useApproveGate, useRejectGate } from './gates-api'

function setup() {
  const queryClient = createTestQueryClient()
  const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  return { invalidate, wrapper }
}

const invalidatedInbox = (invalidate: ReturnType<typeof setup>['invalidate']) =>
  invalidate.mock.calls.some(([filters]) => JSON.stringify(filters?.queryKey) === JSON.stringify(engineKeys.gateInboxAll()))

describe('FR-307: project gate decisions refresh the Gate inbox', () => {
  it.each(['approve', 'reject'] as const)('FR307_project_decision_invalidates_inbox: %s', async (action) => {
    const project = seedProjectAs('owner')
    const gate = seedGate(project, { type: 'spec_approval', artifactType: 'technical_specification', artifactVersion: 1 })
    const { invalidate, wrapper } = setup()
    const { result } = renderHook(() => (action === 'approve' ? useApproveGate(project.id) : useRejectGate(project.id)), { wrapper })

    result.current.mutate({ gateId: gate.id, body: { comment: null } })
    await waitFor(() => {
      expect(result.current.isIdle).toBe(false)
      expect(result.current.isPending).toBe(false)
    })
    expect(invalidatedInbox(invalidate)).toBe(true)
  })
})
