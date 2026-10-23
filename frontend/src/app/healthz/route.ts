/** FR-103: web server liveness. Never calls the API. */
export const dynamic = 'force-dynamic'

export function GET(): Response {
  return new Response('ok', { status: 200, headers: { 'content-type': 'text/plain; charset=utf-8', 'cache-control': 'no-store' } })
}
