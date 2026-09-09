import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'
import { fileURLToPath, URL } from 'node:url'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  base: '/',
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: {
    port: 5173,
    strictPort: true,
    proxy: { '/api': { target: 'http://localhost:5043', changeOrigin: false }, '/hubs': { target: 'http://localhost:5043', changeOrigin: false, ws: true }, '/mcp': { target: 'http://localhost:5043', changeOrigin: false } },
  },
})
