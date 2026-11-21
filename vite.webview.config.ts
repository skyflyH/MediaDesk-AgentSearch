import { resolve } from 'node:path'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  root: resolve(__dirname, 'apps/desktop/src/renderer'),
  base: './',
  plugins: [vue()],
  build: { outDir: resolve(__dirname, 'out/renderer'), emptyOutDir: true }
})
