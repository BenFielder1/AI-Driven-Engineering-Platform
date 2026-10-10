import { describe, expect, it } from 'vitest'

import { lineDiff } from '../../mocks/handlers/artifacts'
import type { DiffLine } from './artifacts-api'
import { diffStats, toSideBySide } from './side-by-side'

const ctx = (oldLine: number, newLine: number, text: string): DiffLine => ({ kind: 'context', oldLine, newLine, text, skipped: null })
const del = (oldLine: number, text: string): DiffLine => ({ kind: 'removed', oldLine, newLine: null, text, skipped: null })
const add = (newLine: number, text: string): DiffLine => ({ kind: 'added', oldLine: null, newLine, text, skipped: null })
const skip = (oldLine: number, newLine: number, count: number): DiffLine => ({ kind: 'skipped', oldLine, newLine, text: '', skipped: count })

describe('side-by-side diff (FR-21)', () => {
  it('pairs a removed line with the added line that replaced it, and keeps context on both sides', () => {
    const rows = toSideBySide([ctx(1, 1, 'a'), del(2, 'b'), add(2, 'B'), add(3, 'C'), ctx(3, 4, 'd'), del(4, 'e')])
    expect(rows).toEqual([
      { kind: 'context', left: { line: 1, text: 'a' }, right: { line: 1, text: 'a' } },
      { kind: 'changed', left: { line: 2, text: 'b' }, right: { line: 2, text: 'B' } },
      { kind: 'added', left: null, right: { line: 3, text: 'C' } },
      { kind: 'context', left: { line: 3, text: 'd' }, right: { line: 4, text: 'd' } },
      { kind: 'removed', left: { line: 4, text: 'e' }, right: null },
    ])
  })

  it('pairs a change run whichever side the server lists first', () => {
    expect(toSideBySide([add(1, 'new'), del(1, 'old')])).toEqual([
      { kind: 'changed', left: { line: 1, text: 'old' }, right: { line: 1, text: 'new' } },
    ])
  })

  it('FR-21: a skipped run of unchanged lines becomes one row and ends the change run before it', () => {
    expect(toSideBySide([del(1, 'old'), skip(2, 1, 40), add(41, 'new')])).toEqual([
      { kind: 'removed', left: { line: 1, text: 'old' }, right: null },
      { kind: 'skipped', count: 40, oldLine: 2, newLine: 1 },
      { kind: 'added', left: null, right: { line: 41, text: 'new' } },
    ])
  })

  it('FR-21: identical versions are a single skipped entry with no changes', () => {
    const lines = [skip(1, 1, 12)]
    expect(toSideBySide(lines)).toEqual([{ kind: 'skipped', count: 12, oldLine: 1, newLine: 1 }])
    expect(diffStats(lines)).toEqual({ added: 0, removed: 0 })
  })

  it('counts added and removed lines', () => {
    expect(diffStats(lineDiff('a\nb\nc', 'a\nB\nc\nd'))).toEqual({ added: 2, removed: 1 })
  })
})
