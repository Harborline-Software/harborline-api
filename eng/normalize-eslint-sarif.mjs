#!/usr/bin/env node
// ESLint and Roslyn share CQG's repository-relative locations and fingerprint
// contract. The package name is the ESLint project's stable identity.
import {normalizeSarifFile} from './normalize-roslyn-sarif.mjs'

export const normalizeEslintSarifFile = (file, repoRoot, project) =>
  normalizeSarifFile(file, repoRoot, {engine: 'eslint', project})

if (process.argv[1] && process.argv[1].replaceAll('\\', '/').endsWith('eng/normalize-eslint-sarif.mjs')) {
  const args = process.argv.slice(2)
  if (args[0] !== '--repo-root' || !args[1] || !args[2] || !args[3] || args.length !== 4) {
    throw new Error('usage: normalize-eslint-sarif.mjs --repo-root <path> <package> <sarif-file>')
  }
  normalizeEslintSarifFile(args[3], args[1], args[2])
}
