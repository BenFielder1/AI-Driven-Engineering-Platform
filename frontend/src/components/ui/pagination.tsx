import { Button } from './button'

/** Previous/next paging with "Page N of M"; renders nothing when everything fits on one page. */
export function Pagination({ page, pageCount, onPage }: { page: number; pageCount: number; onPage: (page: number) => void }) {
  if (pageCount <= 1) {
    return null
  }
  return (
    <nav aria-label="Pagination" className="mt-5 flex items-center justify-between gap-3">
      <Button
        variant="secondary"
        disabled={page <= 1}
        onClick={() => {
          onPage(page - 1)
        }}
      >
        Previous page
      </Button>
      <span className="text-sm text-muted">
        Page {page} of {pageCount}
      </span>
      <Button
        variant="secondary"
        disabled={page >= pageCount}
        onClick={() => {
          onPage(page + 1)
        }}
      >
        Next page
      </Button>
    </nav>
  )
}

