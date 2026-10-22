/**
 * FR-55 Specification panel: the section outline and the "Changes in this version" tags (§4.13), read from an
 * artifact's Markdown. Heading anchors are added to the rendered Markdown by `remarkHeadingIds`, with the same slugs
 * as `markdownOutline`, so outline links land on the rendered headings.
 */

export interface OutlineEntry {
  /** Markdown heading level (1–6). */
  level: number
  text: string
  /** The rendered heading's id. */
  id: string
}

export const requirementChanges = ['new', 'changed', 'removed', 'unchanged'] as const
export type RequirementChange = (typeof requirementChanges)[number]

export interface RequirementChangeEntry extends OutlineEntry {
  change: RequirementChange
  /** The heading text without its tag. */
  title: string
}

/** A heading's anchor id, e.g. "3.1 UI redesign" → "md-3-1-ui-redesign". */
export function headingSlug(text: string): string {
  const slug = text
    .toLowerCase()
    .replace(/[^\p{L}\p{N}]+/gu, '-')
    .replace(/^-+|-+$/g, '')
  return `md-${slug || 'section'}`
}

/** Unique ids in document order: a repeated slug gets -2, -3… */
function uniqueIds() {
  const seen = new Map<string, number>()
  return (text: string) => {
    const base = headingSlug(text)
    const count = (seen.get(base) ?? 0) + 1
    seen.set(base, count)
    return count === 1 ? base : `${base}-${String(count)}`
  }
}

/** Inline Markdown to plain text, as react-markdown renders it: images/links keep their text, emphasis and code marks go. */
function plainText(inline: string): string {
  return inline
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/`([^`]*)`/g, '$1')
    .replace(/(\*\*|__)(.+?)\1/g, '$2')
    .replace(/(^|[^\w*])[*_]([^*_]+)[*_](?=[^\w*]|$)/g, '$1$2')
    .replace(/\s+#+\s*$/, '')
    .trim()
}

const headingLine = /^ {0,3}(#{1,6})[ \t]+(.+?)[ \t]*$/
const fence = /^ {0,3}(```|~~~)/

/** Every ATX heading outside fenced code, in order, with its rendered id. */
export function markdownOutline(markdown: string): OutlineEntry[] {
  const next = uniqueIds()
  const out: OutlineEntry[] = []
  let inFence: string | null = null
  for (const line of markdown.replaceAll('\r\n', '\n').split('\n')) {
    const f = fence.exec(line)
    if (f) {
      const mark = f[1] ?? ''
      inFence = inFence === null ? mark : inFence === mark ? null : inFence
      continue
    }
    if (inFence !== null) {
      continue
    }
    const m = headingLine.exec(line)
    if (m) {
      const text = plainText(m[2] ?? '')
      out.push({ level: (m[1] ?? '#').length, text, id: next(text) })
    }
  }
  return out
}

const tagPattern = /\[(new|changed|removed|unchanged)\]/i
/**
 * §4.13, backend RequirementParser: a requirement line's text starts with `FR-n` after emphasis, code marks, quotes or
 * tags, e.g. `FR-3 [new] Export`, `**FR-3** [changed]: Export`, `[new] FR-3 Export`.
 */
const requirementStart = /^(?:[\s*_`~>]|\[(?:new|changed|unchanged|removed)\])*(?:requirement\s+)?\[?FR-(\d{1,9})\b/i
/** A top-level list item (at most three spaces of indent): `- …`, `* …`, `+ …`, `1. …`, `1) …`. */
const listItemLine = /^ {0,3}(?:[-*+]|\d{1,3}[.)])[ \t]+(.*\S)\s*$/

/** A tagged requirement line's change and its title without the tag; `null` when the text has no tag. */
function tagged(text: string): { change: RequirementChange; title: string } | null {
  const m = tagPattern.exec(text)
  if (!m) {
    return null
  }
  const change = (m[1] ?? '').toLowerCase() as RequirementChange
  const title = plainText(text.replace(tagPattern, ' '))
    .replace(/^[\s:–—-]+|[\s:–—-]+$/g, '')
    .replace(/\s{2,}/g, ' ')
    .replace(/\s+([:.,])/g, '$1')
  return { change, title }
}

/** The anchor id of a list-item or paragraph requirement (`md-req-fr-3`), made unique like heading ids. */
function requirementIds() {
  const seen = new Map<string, number>()
  return (number: string) => {
    const base = `md-req-fr-${String(Number(number))}`
    const count = (seen.get(base) ?? 0) + 1
    seen.set(base, count)
    return count === 1 ? base : `${base}-${String(count)}`
  }
}

/** §4.13: the requirement headings tagged [new], [changed], [unchanged] or [removed] (empty when none are tagged). */
export function requirementChangeEntries(outline: readonly OutlineEntry[]): RequirementChangeEntry[] {
  return outline.flatMap((entry) => {
    const t = tagged(entry.text)
    return t ? [{ ...entry, ...t }] : []
  })
}

/**
 * §4.13: every tagged requirement of a spec, in document order: headings with a tag (`### FR-12 [new] Title`), and
 * top-level list items or paragraph lines that start with `FR-n` and carry a tag (`- **FR-3** [changed] Export`,
 * `**FR-4** [unchanged]: Search`). Headings keep their outline ids; the others get the ids `remarkHeadingIds` gives
 * their rendered list item or paragraph. Fenced code is skipped.
 */
export function requirementChangesIn(markdown: string): RequirementChangeEntry[] {
  const headingIds = uniqueIds()
  const nextRequirementId = requirementIds()
  const out: RequirementChangeEntry[] = []
  let inFence: string | null = null
  let paragraphStart = true
  for (const line of markdown.replaceAll('\r\n', '\n').split('\n')) {
    const f = fence.exec(line)
    if (f) {
      const mark = f[1] ?? ''
      inFence = inFence === null ? mark : inFence === mark ? null : inFence
      paragraphStart = true
      continue
    }
    if (inFence !== null) {
      continue
    }
    const heading = headingLine.exec(line)
    if (heading) {
      const text = plainText(heading[2] ?? '')
      const id = headingIds(text)
      const t = tagged(text)
      if (t) {
        out.push({ level: (heading[1] ?? '#').length, text, id, ...t })
      }
      paragraphStart = true
      continue
    }
    if (line.trim() === '') {
      paragraphStart = true
      continue
    }
    const item = listItemLine.exec(line)
    // Only a list item or the first line of a paragraph can start a requirement (as the rendered list item / paragraph).
    const text = item ? (item[1] ?? '') : paragraphStart && !/^\s/.test(line) ? line : null
    paragraphStart = false
    const start = text === null ? null : requirementStart.exec(text)
    const t = start && text !== null ? tagged(text) : null
    if (start && t) {
      out.push({ level: 0, text: plainText(text ?? ''), id: nextRequirementId(start[1] ?? '0'), ...t })
    }
  }
  return out
}

interface MdNode {
  type: string
  value?: unknown
  alt?: unknown
  children?: MdNode[]
  data?: { hProperties?: Record<string, unknown> }
}

function nodeText(node: MdNode): string {
  if (typeof node.value === 'string') {
    return node.value
  }
  if (node.type === 'image') {
    return typeof node.alt === 'string' ? node.alt : ''
  }
  return (node.children ?? []).map(nodeText).join('')
}

/**
 * A remark plugin: gives every heading the id `markdownOutline` computes for it, and every tagged requirement list item
 * or paragraph the id `requirementChangesIn` computes (§4.13), so the "Changes in this version" links land on them.
 */
export function remarkHeadingIds() {
  return (tree: MdNode) => {
    const next = uniqueIds()
    const nextRequirementId = requirementIds()
    const markRequirement = (node: MdNode, text: string) => {
      const start = requirementStart.exec(text)
      if (start && tagPattern.test(text.split('\n')[0] ?? '')) {
        node.data = { ...node.data, hProperties: { ...node.data?.hProperties, id: nextRequirementId(start[1] ?? '0') } }
      }
    }
    const walk = (node: MdNode, depth: number) => {
      if (node.type === 'heading') {
        node.data = { ...node.data, hProperties: { ...node.data?.hProperties, id: next(nodeText(node).trim()) } }
        return
      }
      if (node.type === 'code') {
        return
      }
      if (node.type === 'listItem' && depth === 0) {
        const first = node.children?.[0]
        if (first?.type === 'paragraph') {
          markRequirement(node, nodeText(first))
        }
        node.children?.forEach((child) => {
          walk(child, depth + 1)
        })
        return
      }
      if (node.type === 'paragraph' && depth === 0) {
        markRequirement(node, nodeText(node))
        return
      }
      const nested = node.type === 'listItem' ? depth + 1 : depth
      node.children?.forEach((child) => {
        walk(child, nested)
      })
    }
    walk(tree, 0)
  }
}
