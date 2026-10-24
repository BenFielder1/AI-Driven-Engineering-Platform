import { describe, expect, it } from 'vitest'

import { redactApprovalTokens, redactPath, redactReferer } from './log-redaction'

describe('FR-100 approval-token redaction', () => {
  // SPEC §4 worked examples.
  it.each([
    ['/approvals/abc123', '/approvals/[redacted]'],
    ['/api/approvals/abc123?x=1', '/api/approvals/[redacted]?x=1'],
    ['https://aide.example/approvals/abc123#top', 'https://aide.example/approvals/[redacted]#top'],
    ['/approvals/', '/approvals/'],
    ['/projects/1', '/projects/1'],
  ])('FR-100: SPEC §4 %s is logged as %s', (input, expected) => {
    expect(redactPath(input)).toBe(expected)
    expect(redactReferer(input)).toBe(expected)
  })

  it.each([
    ['/api/approvals/abc123/decision', '/api/approvals/[redacted]/decision'],
    ['/APPROVALS/abc', '/APPROVALS/[redacted]'],
    ['/approvals/?x=1', '/approvals/?x=1'],
    ['/projects/1/artifacts?type=approvals', '/projects/1/artifacts?type=approvals'],
    // Signed-out visit: the guard sends the user to /login with the approval URL as returnUrl.
    ['/login?returnUrl=%2Fapprovals%2Fabc123', '/login?returnUrl=%2Fapprovals%2F[redacted]'],
    ['/login?returnUrl=%2fapprovals%2fabc123&x=1', '/login?returnUrl=%2fapprovals%2f[redacted]&x=1'],
    ['/login?returnUrl=/approvals/abc123', '/login?returnUrl=/approvals/[redacted]'],
    ['/login?returnUrl=%252Fapprovals%252Fabc123', '/login?returnUrl=%252Fapprovals%252F[redacted]'],
    ['/api/auth/github/login?returnUrl=%2Fapi%2Fapprovals%2Fabc123', '/api/auth/github/login?returnUrl=%2Fapi%2Fapprovals%2F[redacted]'],
    // RSC fetches: the token in the path and in the query, next to the `_rsc` cache-busting param.
    ['/approvals/abc123?_rsc=1x2y3', '/approvals/[redacted]?_rsc=1x2y3'],
    ['/login?returnUrl=%2Fapprovals%2Fabc123&_rsc=1x2y3', '/login?returnUrl=%2Fapprovals%2F[redacted]&_rsc=1x2y3'],
    ['/login?_rsc=1x2y3&returnUrl=%2Fapprovals%2Fabc123', '/login?_rsc=1x2y3&returnUrl=%2Fapprovals%2F[redacted]'],
    // Percent-encoded characters inside the token are redacted too; encoded separators end it.
    ['/approvals/%41bc', '/approvals/[redacted]'],
    ['/approvals/ab%2bc%3D?x=1', '/approvals/[redacted]?x=1'],
    ['/approvals/abc%23top', '/approvals/[redacted]%23top'],
    ['/login?returnUrl=%2Fapprovals%2Fab%41c%3Fx%3D1', '/login?returnUrl=%2Fapprovals%2F[redacted]%3Fx%3D1'],
    ['/login?returnUrl=%2Fapi%2Fapprovals%2Fabc%2fdecision', '/login?returnUrl=%2Fapi%2Fapprovals%2F[redacted]%2fdecision'],
    ['/login?returnUrl=%2Fapprovals%2Fabc%26x%3D1', '/login?returnUrl=%2Fapprovals%2F[redacted]%26x%3D1'],
    ['/login?returnUrl=%252Fapprovals%252Fa%2541bc%253Fx', '/login?returnUrl=%252Fapprovals%252F[redacted]%253Fx'],
    // Over-redaction is acceptable in logs: any `/approvals/<segment>` is treated as a token.
    ['/projects/approvals/abc', '/projects/approvals/[redacted]'],
  ])('FR-100: path %s is logged as %s', (input, expected) => {
    expect(redactPath(input)).toBe(expected)
  })

  it.each([
    ['https://aide.example/api/approvals/abc123?x=1', 'https://aide.example/api/approvals/[redacted]?x=1'],
    ['https://aide.example/approvals/', 'https://aide.example/approvals/'],
    ['https://aide.example/projects?next=/approvals/abc', 'https://aide.example/projects?next=/approvals/[redacted]'],
    ['https://aide.example/login?returnUrl=%2Fapprovals%2Fabc123', 'https://aide.example/login?returnUrl=%2Fapprovals%2F[redacted]'],
    ['https://aide.example/login?returnUrl=%2Fapprovals%2Fabc123#top', 'https://aide.example/login?returnUrl=%2Fapprovals%2F[redacted]#top'],
    ['https://aide.example/approvals/abc123?_rsc=a1', 'https://aide.example/approvals/[redacted]?_rsc=a1'],
  ])('FR-100: Referer %s is logged as %s', (input, expected) => {
    expect(redactReferer(input)).toBe(expected)
  })

  it('FR-100: every occurrence in free text is redacted', () => {
    const text = 'GET /approvals/tok1 from https://a.example/login?returnUrl=%2Fapprovals%2Ftok2 "/api/approvals/tok3"'
    const redacted = redactApprovalTokens(text)
    expect(redacted).not.toMatch(/tok[123]/)
    expect(redacted.match(/\[redacted\]/g)).toHaveLength(3)
  })
})
