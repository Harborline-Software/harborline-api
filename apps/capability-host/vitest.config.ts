import { defineConfig } from 'vitest/config'

/**
 * The Capability shell imports the @harborline-software/api-contracts capability TYPES. Those imports
 * are type-only (the namespace is pure types) so they compile away and vitest
 * never loads the module at runtime. @harborline-software/api-contracts resolves as a normal
 * `file:` dependency (its `dist`), so no alias is needed.
 */
export default defineConfig({
  test: {
    include: ['src/**/*.test.ts'],
    environment: 'node',
  },
})
