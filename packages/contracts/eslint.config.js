import tseslint from 'typescript-eslint'

// Mirrors the earlier app's eslint.config.js — the repo's established per-package
// flat-config convention (no shared root config; that app + packages/ui-react
// each carry their own). Folded into the `carrier/capability TS suites` required gate
// so lint runs alongside vitest (closes the TS-side inv-7 lint gap, 2026-06-18).
export default tseslint.config(
  ...tseslint.configs.recommended,
  {
    files: ['src/**/*.{ts,tsx}'],
    rules: {
      '@typescript-eslint/no-unused-vars': ['warn', { argsIgnorePattern: '^_' }],
      '@typescript-eslint/no-explicit-any': 'warn',
    },
  },
  {
    ignores: ['dist/', 'node_modules/'],
  },
)
