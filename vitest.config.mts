import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    include: ['apps/desktop/tests/**/*.test.ts'],
    environment: 'node',
    testTimeout: 15_000,
    hookTimeout: 15_000
  }
})
