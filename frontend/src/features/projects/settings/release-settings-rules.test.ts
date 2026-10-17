import { describe, expect, it } from 'vitest'

import { checkTagPrefix } from './release-settings-rules'

describe('checkTagPrefix (§4.10, FR-64; mirrors ReleaseVersionRules.IsValidTagPrefix)', () => {
  it.each(['', 'v', 'release/', 'rel-', 'a.b', 'v_1/', 'x.lock', 'abcdefghij0123456789'])('FR64_accepts "%s"', (prefix) => {
    expect(checkTagPrefix(prefix)).toBeNull()
  })

  it.each(['a..b', 'a//b', 'a/.b', 'x.lock/', '-v', '.v', '/v', 'v 1', 'v@', 'abcdefghij0123456789x'])(
    'FR64_rejects "%s"',
    (prefix) => {
      expect(checkTagPrefix(prefix)).not.toBeNull()
    },
  )

  it('FR64_names_the_forbidden_sequence', () => {
    expect(checkTagPrefix('a..b')).toBe('The prefix can’t contain ".." (it must make a valid Git tag name).')
    expect(checkTagPrefix('x.lock/')).toBe('The prefix can’t contain ".lock/" (it must make a valid Git tag name).')
  })
})
