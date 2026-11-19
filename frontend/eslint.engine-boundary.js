/**
 * 0.7.0 FR-304, D-0.7.0-14: `src/engine/` stands in for the future `@tevrin/engine-ui` package, so it may not import
 * the SDLC features (`src/features/**`) or the Next.js routes (`src/app/**`). It may use the generated API client and
 * the shared components and lib, as a package would use its peer dependencies. Proven by src/engine/boundary.test.ts.
 */
export const ENGINE_BOUNDARY_MESSAGE =
  'src/engine is the engine UI package (FR-304): it must not import from src/features or src/app. Pass what it needs in as props.'

/**
 * A relative (`../features/x`, `../../lib/../features/x`) or aliased (`@/features/x`) path with a `features` or `app`
 * segment anywhere. Written without a literal `/` (as `\x2F`) so the same source works in an esquery selector.
 */
export const ENGINE_BOUNDARY_PATTERN = '^(?:@\\x2F|\\.\\.?\\x2F)(?:.*\\x2F)?(?:features|app)(?:\\x2F|$)'

export const engineBoundary = {
  files: ['src/engine/**/*.{ts,tsx}'],
  rules: {
    // Static `import … from` and `export … from`.
    'no-restricted-imports': ['error', { patterns: [{ regex: ENGINE_BOUNDARY_PATTERN, message: ENGINE_BOUNDARY_MESSAGE }] }],
    // Dynamic `import()` and type-level `typeof import()`.
    'no-restricted-syntax': [
      'error',
      { selector: `ImportExpression[source.value=/${ENGINE_BOUNDARY_PATTERN}/]`, message: ENGINE_BOUNDARY_MESSAGE },
      { selector: `TSImportType[source.value=/${ENGINE_BOUNDARY_PATTERN}/]`, message: ENGINE_BOUNDARY_MESSAGE },
    ],
  },
}
