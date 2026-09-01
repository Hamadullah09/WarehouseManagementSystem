import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The API and the SPA are served from one origin in production: `npm run build`
// emits into the API's wwwroot, and Program.cs falls back to index.html.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../Warehouse.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5080', changeOrigin: true },
      '/hubs': { target: 'http://localhost:5080', ws: true, changeOrigin: true },
    },
  },
})
