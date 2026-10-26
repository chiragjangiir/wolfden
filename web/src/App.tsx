import { useState } from 'react'
import { QueryClientProvider, type QueryClient } from '@tanstack/react-query'
import { RouterProvider, type RouterHistory } from '@tanstack/react-router'
import { createAppRouter } from './router'

/** The web UI: the den's pages (#24 to #27), routed, with the API's answers cached by TanStack Query. */
export function App({ queryClient, history }: { queryClient: QueryClient; history?: RouterHistory }) {
  const [router] = useState(() => createAppRouter(queryClient, history))
  return (
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  )
}
