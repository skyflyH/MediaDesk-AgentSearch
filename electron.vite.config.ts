import { resolve } from 'node:path'
import { defineConfig } from 'electron-vite'
import vue from '@vitejs/plugin-vue'

const root = __dirname

export default defineConfig({
  main: {
    build: {
      externalizeDeps: false,
      rollupOptions: {
        input: {
          index: resolve(root, 'apps/desktop/src/main/index.ts')
        }
      }
    }
  },
  preload: {
    build: {
      externalizeDeps: false,
      rollupOptions: {
        input: {
          index: resolve(root, 'apps/desktop/src/preload/index.ts')
        }
      }
    }
  },
  renderer: {
    root: resolve(root, 'apps/desktop/src/renderer'),
    plugins: [vue()],
    build: {
      rollupOptions: {
        input: {
          index: resolve(root, 'apps/desktop/src/renderer/index.html')
        }
      }
    }
  }
})
