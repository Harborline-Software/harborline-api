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
    // HLQ.TS.1000 (ticket 340, ADR 0085 Preview-mode catalog) needs type information, so only
    // the files tsconfig.json compiles get the project service: the tests are excluded there
    // (tsconfig.json exclude) and the typed parser would report them as not found (340 s2 review).
    files: ['src/**/*.{ts,tsx}'],
    ignores: ['src/__tests__/**'],
    languageOptions: {
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },
    rules: {
      // `void` is not an exception because it does not observe a rejection.
      '@typescript-eslint/no-floating-promises': ['error', { ignoreVoid: false }],
    },
  },
  {
    ignores: ['dist/', 'node_modules/'],
  },
)
