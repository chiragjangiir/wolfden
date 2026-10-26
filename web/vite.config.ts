import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The port 'wolfden serve' listens on by default (WolfdenApi.DefaultPort).
// Run 'wolfden serve --no-open' beside 'npm run dev', and the dev server
// passes /api on to it.
const servePort = 9653

export default defineConfig({
  plugins: [react()],
  server: {
    // Local only, like 'wolfden serve'.
    host: '127.0.0.1',
    proxy: {
      '/api': `http://127.0.0.1:${servePort}`,
    },
  },
})
