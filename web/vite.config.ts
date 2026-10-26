import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The port 'wolfden serve' listens on: WOLFDEN_PORT if set, or 9653
// (WolfdenApi.DefaultPort). Run 'wolfden serve --no-open' beside
// 'npm run dev', and the dev server passes /api on to it.
const servePort = Number(process.env.WOLFDEN_PORT ?? 9653)

/**
 * True if `origin` is this dev server's own page: http on localhost or
 * 127.0.0.1, at the host and port the request was sent to.
 */
export function isDevServerOrigin(origin: string, host: string | undefined): boolean {
  if (!host) {
    return false
  }

  const own = `http://${host}`.toLowerCase()
  const hostname = host.replace(/:\d+$/, '').toLowerCase()
  return (hostname === 'localhost' || hostname === '127.0.0.1') && origin.toLowerCase() === own
}

export default defineConfig({
  plugins: [react()],
  server: {
    // Local only, like 'wolfden serve'.
    host: '127.0.0.1',
    proxy: {
      '/api': {
        target: `http://127.0.0.1:${servePort}`,
        configure: (proxy) => {
          // The API refuses an Origin other than its own port, and this
          // page's port is Vite's: drop the Origin of this dev server's own
          // page only. Any other Origin goes through, so the API refuses it.
          proxy.on('proxyReq', (proxyRequest, request) => {
            const origin = request.headers.origin
            if (typeof origin === 'string' && isDevServerOrigin(origin, request.headers.host)) {
              proxyRequest.removeHeader('origin')
            }
          })
        },
      },
    },
  },
})
