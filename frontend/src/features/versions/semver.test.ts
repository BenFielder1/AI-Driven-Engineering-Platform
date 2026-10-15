import { describe, expect, it } from 'vitest'

import { checkNewVersion, compareSemVer, normalizeVersion, parseSemVer, suggestNextVersion } from './semver'

const cmp = (a: string, b: string) => {
  const pa = parseSemVer(a)
  const pb = parseSemVer(b)
  if (!pa || !pb) {
    throw new Error('invalid')
  }
  return compareSemVer(pa, pb)
}

describe('FR61 semver (§4.10)', () => {
  it('FR61_strips_a_leading_v_and_normalises', () => {
    expect(normalizeVersion('v1.2.0')).toBe('1.2.0')
    expect(normalizeVersion(' V2.0.0-beta.1 ')).toBe('2.0.0-beta.1')
  })

  it('FR61_rejects_invalid_versions', () => {
    for (const bad of ['01.2.0', '1.2', '1.2.0+build.5', '', 'vv1.2.0', '1.02.0', '1.2.0-', 'abc']) {
      expect(parseSemVer(bad), bad).toBeNull()
    }
  })

  it('FR61_rejects_core_parts_beyond_the_domain_long_range', () => {
    expect(parseSemVer('99999999999999999999.0.0')).toBeNull()
    expect(parseSemVer('1.9223372036854775808.0')).toBeNull()
    expect(checkNewVersion('99999999999999999999.0.0', '1.1.0')).toMatchObject({ ok: false, code: 'VERSION_INVALID' })
    expect(normalizeVersion('9223372036854775807.0.0')).toBe('9223372036854775807.0.0')
  })

  it('FR61_compares_parts_beyond_2_pow_53_exactly', () => {
    expect(cmp('9007199254740993.0.0', '9007199254740992.0.0')).toBeGreaterThan(0)
    expect(cmp('1.9223372036854775807.0', '1.9223372036854775806.0')).toBeGreaterThan(0)
    expect(checkNewVersion('9007199254740993.0.0', '9007199254740992.0.0').ok).toBe(true)
  })

  it('FR61_orders_by_semver_precedence', () => {
    expect(cmp('1.2.0', '1.1.0')).toBeGreaterThan(0)
    expect(cmp('1.2.0-rc.1', '1.2.0')).toBeLessThan(0)
    expect(cmp('1.0.0-alpha', '1.0.0-alpha.1')).toBeLessThan(0)
    expect(cmp('1.0.0-alpha.1', '1.0.0-alpha.beta')).toBeLessThan(0)
    expect(cmp('1.0.0-beta.2', '1.0.0-beta.11')).toBeLessThan(0)
    expect(cmp('1.0.0', '1.0.0')).toBe(0)
  })

  it('FR61_worked_examples_against_released_1_1_0', () => {
    expect(checkNewVersion('1.2.0', '1.1.0')).toEqual({ ok: true, version: '1.2.0' })
    expect(checkNewVersion('1.1.1', '1.1.0').ok).toBe(true)
    expect(checkNewVersion('2.0.0-beta.1', '1.1.0').ok).toBe(true)
    expect(checkNewVersion('1.1.0', '1.1.0')).toMatchObject({ ok: false, code: 'VERSION_NOT_GREATER' })
    expect(checkNewVersion('1.0.9', '1.1.0')).toMatchObject({ ok: false, code: 'VERSION_NOT_GREATER' })
    expect(checkNewVersion('1.0.5', '1.1.0')).toMatchObject({ ok: false, code: 'VERSION_NOT_GREATER' })
    expect(checkNewVersion('01.2.0', '1.1.0')).toMatchObject({ ok: false, code: 'VERSION_INVALID' })
    expect(checkNewVersion('v1.2.0', '1.1.0')).toEqual({ ok: true, version: '1.2.0' })
  })

  it('FR61_rc_then_release_are_separate_versions', () => {
    expect(checkNewVersion('1.2.0-rc.1', '1.1.0').ok).toBe(true)
    expect(checkNewVersion('1.2.0', '1.2.0-rc.1').ok).toBe(true)
  })

  it('FR61_refuses_a_version_the_project_already_has', () => {
    expect(checkNewVersion('1.3.0', '1.1.0', ['1.3.0'])).toMatchObject({ ok: false, code: 'VERSION_NOT_GREATER' })
  })

  it('FR66_any_valid_version_is_accepted_with_nothing_released', () => {
    expect(checkNewVersion('0.1.0', null).ok).toBe(true)
  })

  it('FR60_suggests_the_next_minor', () => {
    expect(suggestNextVersion('1.1.0')).toBe('1.2.0')
    expect(suggestNextVersion('1.1.3')).toBe('1.2.0')
    expect(suggestNextVersion('2.0.0-beta.1')).toBe('2.1.0')
    expect(suggestNextVersion(null)).toBe('0.1.0')
  })
})
