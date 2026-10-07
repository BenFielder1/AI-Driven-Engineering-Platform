import { QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider } from '@tanstack/react-router'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import './index.css'
import { refreshAntiforgeryToken } from './api/client'
import { authKeys } from './features/auth/auth-api'
import { createQueryClient } from './lib/query-client'
import { initTheme } from './lib/theme'
import { createAppRouter } from './router'

initTheme()
// NFR-SEC: obtain the XSRF-TOKEN cookie up front (the client also fetches it lazily before a mutation).
void refreshAntiforgeryToken()

const queryClient = createQueryClient({
  onUnauthenticated: () => {
    queryClient.setQueryData(authKeys.me(), null)
    const { pathname, href } = router.state.location
    if (pathname !== '/login' && pathname !== '/register') {
      void router.navigate({ to: '/login', search: { returnUrl: href } })
    }
  },
})
const router = createAppRouter(queryClient)

const rootElement = document.getElementById('root')
if (!rootElement) {
  throw new Error('Root element #root not found')
}

createRoot(rootElement).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
)
