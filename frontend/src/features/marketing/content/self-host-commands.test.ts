import { readFileSync } from 'node:fs'
import path from 'node:path'

import { describe, expect, it } from 'vitest'

import { parseCommandLine, selfHostPaths } from './self-host-commands'

/** Vitest runs from frontend/ (package.json), so the repo root is its parent. */
const repoRoot = path.resolve(process.cwd(), '..')

/** The lines of the first ```bash block in `file` after the `## heading` line (or the file's first one when ''). */
function docsBlock(file: string, heading: string): string[] {
  const lines = readFileSync(path.join(repoRoot, file), 'utf8').split(/\r?\n/)
  let i = heading === '' ? 0 : lines.findIndex((l) => l.trim() === `## ${heading}`)
  expect(i, `no "## ${heading}" in ${file}`).toBeGreaterThanOrEqual(0)
  while (i < lines.length && lines[i]?.trim() !== '```bash') {
    i++
  }
  const block: string[] = []
  for (i++; i < lines.length && lines[i]?.trim() !== '```'; i++) {
    const line = lines[i] ?? ''
    if (line.trim() !== '') {
      block.push(line)
    }
  }
  expect(block.length, `no bash block under "${heading}" in ${file}`).toBeGreaterThan(0)
  return block
}

describe('FR-222: self-host commands match the docs', () => {
  it.each(selfHostPaths.map((p) => [p.id, p]))('FR222_self_host_commands_match_docs: %s', (_id, path) => {
    const docs = docsBlock(path.source.file, path.source.heading).map(parseCommandLine)
    // Every command in the module is in the docs block, and every command line in the block is in the module, in order.
    expect(path.commands).toEqual(docs)
  })

  it('FR222_parse_command_line: splits the command from its trailing comment', () => {
    expect(parseCommandLine('railway config plan      # read-only diff')).toEqual({
      command: 'railway config plan',
      note: 'read-only diff',
    })
    expect(parseCommandLine('bash x.sh <web-url>')).toEqual({ command: 'bash x.sh <web-url>', note: '' })
  })
})
