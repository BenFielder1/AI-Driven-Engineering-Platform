import { describe, expect, it } from 'vitest'

import * as changelog from './changelog'
import * as faq from './faq'
import * as headings from './headings'
import * as home from './home'
import * as howItWorks from './how-it-works'
import * as integrations from './integrations'
import * as lifecycle from './lifecycle'
import * as pages from './pages'
import * as previews from './previews'
import * as product from './product'
import * as security from './security'
import * as selfHost from './self-host'

/** Every string reachable from a module's exports (functions skipped). */
function strings(value: unknown, path: string, out: { path: string; text: string }[] = []): { path: string; text: string }[] {
  if (typeof value === 'string') {
    out.push({ path, text: value })
  } else if (Array.isArray(value)) {
    value.forEach((v: unknown, i) => strings(v, `${path}[${String(i)}]`, out))
  } else if (value !== null && typeof value === 'object') {
    for (const [k, v] of Object.entries(value)) {
      strings(v, `${path}.${k}`, out)
    }
  }
  return out
}

const modules = { changelog, faq, headings, home, howItWorks, integrations, lifecycle, pages, previews, product, security, selfHost }
const copy = Object.entries(modules).flatMap(([name, mod]) => strings(mod, name))

describe('FR-222 content truthfulness', () => {
  it('FR222_Q3_no_open_source: no copy claims "open source" (SPEC Q3: "source available on GitHub")', () => {
    expect(copy.filter((c) => /open[\s-]?source/i.test(c.text))).toEqual([])
  })

  it('FR222_no_testimonials_or_metrics: no testimonials, customer claims or invented metrics', () => {
    const banned = /testimonial|trusted by|our customers|loved by|\d+\s?%|\d+x faster|\bteams? ship\b/i
    expect(copy.filter((c) => banned.test(c.text))).toEqual([])
  })

  it('FR197_FR218_FR219_old_name_only_in_history: "AIDE" appears only in the FAQ answer about the name and the Changelog note', () => {
    const allowed = new Set(['faq.faqs[11].question', 'faq.faqs[11].answer[0]', 'headings.changelogNote', 'changelog.AS_AIDE'])
    const withOldName = copy.filter((c) => c.text.includes('AIDE')).map((c) => c.path)

    expect(withOldName.filter((p) => !allowed.has(p))).toEqual([])
    expect(faq.faqs[11]?.id).toBe('aide')
  })

  it('FR212_FR215_works_with_subset: every "Works with" name is listed on Integrations', () => {
    const listed = new Set(integrations.integrationGroups.flatMap((g) => g.items.map((i) => i.name)))
    for (const item of integrations.worksWith) {
      expect(listed.has(item.name), item.name).toBe(true)
    }
  })
})
