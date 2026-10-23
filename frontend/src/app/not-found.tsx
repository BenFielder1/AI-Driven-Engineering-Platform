import { NotFoundView } from '../features/shell/route-states'

/** Unknown URLs (FR-95): the v2 not-found page, HTTP 404. */
export default function NotFound() {
  return <NotFoundView />
}
