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

/** §4.13: the requirement headings tagged [new], [changed], [unchanged] or [removed] (empty when none are tagged). */
export function requirementChangeEntries(outline: readonly OutlineEntry[]): RequirementChangeEntry[] {
  return outline.flatMap((entry) => {
    const m = tagPattern.exec(entry.text)
    if (!m) {
      return []
    }
    const change = (m[1] ?? '').toLowerCase() as RequirementChange
    const title = entry.text.replace(tagPattern, '').replace(/\s{2,}/g, ' ').trim()
    return [{ ...entry, change, title }]
  })
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

/** A remark plugin: gives every heading the id `markdownOutline` computes for it. */
export function remarkHeadingIds() {
  return (tree: MdNode) => {
    const next = uniqueIds()
    const walk = (node: MdNode) => {
      if (node.type === 'heading') {
        node.data = { ...node.data, hProperties: { ...node.data?.hProperties, id: next(nodeText(node).trim()) } }
        return
      }
      if (node.type === 'code') {
        return
      }
      node.children?.forEach(walk)
    }
    walk(tree)
  }
}
