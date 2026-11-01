import { cn } from '../../lib/utils'

/** The Bitbucket bucket, inline (no external image; CSP `img-src 'self'`). Decorative: the text next to it names Bitbucket. */
export function BitbucketLogo({ className }: { className?: string }) {
  return (
    <svg aria-hidden="true" focusable="false" viewBox="0 0 24 24" className={cn('size-4 shrink-0', className)}>
      <path
        fill="#2684FF"
        d="M.778 1.213a.768.768 0 0 0-.768.892l3.263 19.81c.084.5.515.868 1.022.873H19.95a.772.772 0 0 0 .77-.646l3.27-20.03a.768.768 0 0 0-.768-.891zM14.52 15.53H9.522L8.17 8.466h7.561z"
      />
    </svg>
  )
}
