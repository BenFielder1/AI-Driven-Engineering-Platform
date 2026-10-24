// Ambient types for the two bundlers, declared here instead of pulling in `vite/client` (whose `import.meta.env` and
// `import.meta.hot` would type-check in app code although Next.js defines neither: use process.env.NODE_ENV).
// - Next.js and Vitest: side-effect `*.css` imports (fonts, global CSS).
// - Vitest only (it runs on Vite): `import.meta.glob` (src/test/app-routes.ts, src/test/render.tsx, src/lib/lib.test.ts)
//   and `?raw` imports (src/lib/ui/tokens.test.ts).

declare module '*.css' {}

declare module '*?raw' {
  const content: string
  export default content
}

interface ImportMetaGlobOptions<Eager extends boolean> {
  eager?: Eager
  import?: string
  query?: string | Record<string, string | number | boolean>
}

interface ImportMeta {
  glob<T = unknown>(pattern: string | string[], options: ImportMetaGlobOptions<true> & { eager: true }): Record<string, T>
  glob<T = unknown>(pattern: string | string[], options?: ImportMetaGlobOptions<false>): Record<string, () => Promise<T>>
}
