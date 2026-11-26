import { appearanceKey, colors } from './src/app/theme/appearanceColors.ts'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'
import { fileURLToPath, URL } from 'node:url'
import { viteStaticCopy } from 'vite-plugin-static-copy'

// https://vite.dev/config/
export default defineConfig({
  plugins: [{
    name: 'initial-appearance',
    transformIndexHtml(html) {
      return html.replace('__RT_PALETTES__', JSON.stringify(colors)).replace('__RT_APPEARANCE_KEY__', appearanceKey)
    },
  }, react(), viteStaticCopy({ targets: ['cmaps', 'standard_fonts', 'wasm'].map(directory => ({ src: `node_modules/pdfjs-dist/${directory}/*`, dest: `pdfjs/${directory}`, rename: { stripBase: true } })) })],
  base: '/',
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: {
    host: '0.0.0.0',
    port: 5173,
    strictPort: true,
    proxy: { '/Avatars/': 'http://localhost:5043', '/agent/0.1.0/native-guide.txt': 'http://localhost:5043', '/api': { target: 'http://localhost:5043', changeOrigin: false }, '/hubs': { target: 'http://localhost:5043', changeOrigin: false, ws: true }, '/mcp': { target: 'http://localhost:5043', changeOrigin: false } },
  },
})
