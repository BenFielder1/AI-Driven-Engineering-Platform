import js from '@eslint/js'
import nextPlugin from '@next/eslint-plugin-next'
import pluginQuery from '@tanstack/eslint-plugin-query'
import reactHooks from 'eslint-plugin-react-hooks'
import { defineConfig, globalIgnores } from 'eslint/config'
import globals from 'globals'
import tseslint from 'typescript-eslint'

export default defineConfig([
  // Generated code is never linted (or hand-edited).
  globalIgnores([
    '.next',
    'dist',
    'coverage',
    'playwright-report',
    'test-results',
    'next-env.d.ts',
    'src/api/generated',
  ]),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.strictTypeChecked,
      tseslint.configs.stylisticTypeChecked,
      reactHooks.configs.flat.recommended,
      pluginQuery.configs['flat/recommended'],
      nextPlugin.configs['core-web-vitals'],
    ],
    languageOptions: {
      ecmaVersion: 2023,
      globals: globals.browser,
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },
    rules: {
      '@typescript-eslint/no-explicit-any': 'error',
      '@typescript-eslint/consistent-type-imports': 'error',
      // Avatars are external URLs (GitHub) shown at a fixed small size; next/image isn't used (no image optimisation).
      '@next/next/no-img-element': 'off',
    },
  },
  {
    // Node code: the custom web server, its helpers and tool configs.
    files: ['server.ts', 'src/server/**/*.ts', 'e2e/**/*.ts', '*.config.{ts,js,mjs}'],
    languageOptions: { globals: globals.node },
  },
])
