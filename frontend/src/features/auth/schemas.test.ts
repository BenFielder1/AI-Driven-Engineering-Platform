import { describe, expect, it } from 'vitest'

import { passwordSchema } from './schemas'

describe('passwordSchema (FR-1, matches the backend Identity policy)', () => {
  it.each([
    ['password1', true],
    ['pässwörd1', true], // Unicode letters are fine
    ['abcdefgh', false], // no digit
    ['12345678', false], // no letter
    ['short1', false], // too short
    ['password١', false], // Arabic-Indic digit: Identity's RequireDigit is ASCII 0-9 only
    ['password０', false], // full-width digit
  ])('FR-1: %s → %s', (password, valid) => {
    expect(passwordSchema.safeParse(password).success).toBe(valid)
  })
})
