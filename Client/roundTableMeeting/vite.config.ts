import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'
import { fileURLToPath, URL } from 'node:url'
import { viteStaticCopy } from 'vite-plugin-static-copy'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), viteStaticCopy({ targets: ['cmaps', 'standard_fonts', 'wasm'].map(directory => ({ src: `node_modules/pdfjs-dist/${directory}/*`, dest: `pdfjs/${directory}`, rename: { stripBase: true } })) })],
  base: '/',
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: {
    host: '0.0.0.0',
    port: 5173,
    strictPort: true,
    proxy: { '/api': { target: 'http://localhost:5043', changeOrigin: false }, '/hubs': { target: 'http://localhost:5043', changeOrigin: false, ws: true }, '/mcp': { target: 'http://localhost:5043', changeOrigin: false } },
  },
})
