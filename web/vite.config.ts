import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The port 'wolfden serve' listens on: WOLFDEN_PORT if set, or 9653
// (WolfdenApi.DefaultPort). Run 'wolfden serve --no-open' beside
// 'npm run dev', and the dev server passes /api on to it.
const servePort = Number(process.env.WOLFDEN_PORT ?? 9653)

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
          // page's port is Vite's. Vite's own checks guard the dev server.
          proxy.on('proxyReq', (request) => request.removeHeader('origin'))
        },
      },
    },
  },
})
