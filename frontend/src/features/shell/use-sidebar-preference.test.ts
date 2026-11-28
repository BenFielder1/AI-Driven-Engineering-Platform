import { act, renderHook } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import {
  LEGACY_SIDEBAR_STORAGE_KEY,
  readSidebarMode,
  SIDEBAR_STORAGE_KEY,
  useSidebarPreference,
} from './use-sidebar-preference'

describe('FR-326: the sidebar preference key', () => {
  it('FR-326 sidebar_key: the pin is stored under tevrin.sidebar', () => {
    expect(SIDEBAR_STORAGE_KEY).toBe('tevrin.sidebar')
    localStorage.setItem(SIDEBAR_STORAGE_KEY, 'collapsed')
    expect(readSidebarMode()).toBe('collapsed')
  })

  it('FR-326 sidebar_legacy_read: a pin saved under aide.sidebar is read when tevrin.sidebar is missing', () => {
    localStorage.setItem(LEGACY_SIDEBAR_STORAGE_KEY, 'expanded')
    expect(readSidebarMode()).toBe('expanded')

    localStorage.setItem(SIDEBAR_STORAGE_KEY, 'collapsed')
    expect(readSidebarMode()).toBe('collapsed')
  })

  it('FR-326 sidebar_legacy_migrated: toggling writes tevrin.sidebar and removes aide.sidebar', () => {
    localStorage.setItem(LEGACY_SIDEBAR_STORAGE_KEY, 'expanded')
    const { result } = renderHook(() => useSidebarPreference())
    expect(result.current.mode).toBe('expanded')

    act(() => {
      result.current.toggle()
    })
    expect(result.current.mode).toBe('collapsed')
    expect(localStorage.getItem(SIDEBAR_STORAGE_KEY)).toBe('collapsed')
    expect(localStorage.getItem(LEGACY_SIDEBAR_STORAGE_KEY)).toBeNull()
  })
})
