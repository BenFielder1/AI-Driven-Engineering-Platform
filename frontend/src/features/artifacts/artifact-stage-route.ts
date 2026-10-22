/** `?version=N` on the artifact stage pages (specification, planning) and artifact detail: a positive integer or absent. */
export interface ArtifactVersionSearch {
  version?: number
}

export function validateArtifactVersionSearch(search: Record<string, unknown>): ArtifactVersionSearch {
  const version = typeof search.version === 'number' ? search.version : Number(search.version)
  return { version: Number.isInteger(version) && version > 0 ? version : undefined }
}
