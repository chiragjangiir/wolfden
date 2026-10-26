import { infiniteQueryOptions, queryOptions } from '@tanstack/react-query'
import { apiPath, getJson, type EntitySummary } from './api'

// The shapes of /api/history and /api/history/diff (Wolfden.Index's
// GetHistoryResult and Wolfden.Api's HistoryDiffResult), in camelCase. History
// isn't a story view: neither endpoint takes asOf, so these queries have no
// position in their keys.

export type ChangeKind = 'added' | 'modified' | 'deleted' | 'renamed'

export interface HistoryChange {
  kind: ChangeKind
  path: string
  oldPath: string | null
}

export interface HistoryEntry {
  id: string
  when: string
  author: string
  message: string
  changes: HistoryChange[]
}

export interface HistoryResult {
  version: number
  entity: EntitySummary
  backend: string
  available: boolean
  reason: string | null
  count: number
  truncated: boolean
  nextBefore: string | null
  entries: HistoryEntry[] | null
}

export interface FileDiff {
  change: HistoryChange
  patch: string
}

export interface HistoryDiffResult {
  version: number
  entity: EntitySummary
  backend: string
  available: boolean
  reason: string | null
  diff: { from: string | null; to: string | null; files: FileDiff[] } | null
}

/** How many checkpoints one page of the list asks for. */
export const HISTORY_PAGE = 20

/** An entity's checkpoints, newest first, a page at a time (`before` the last one shown). */
export function historyQuery(universe: string, entity: string) {
  return infiniteQueryOptions({
    queryKey: ['history', universe, entity],
    queryFn: ({ pageParam }) =>
      getJson<HistoryResult>(apiPath('/api/history', { entity, universe, limit: HISTORY_PAGE, before: pageParam ?? undefined })),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => (last.available && last.truncated ? last.nextBefore : null),
  })
}

/**
 * What to show a diff of: one checkpoint's own change, the change between
 * two (`to` only with `from`), or, with neither, what changed since the
 * latest checkpoint: the uncommitted changes.
 */
export interface DiffSelection {
  checkpoint?: string
  from?: string
  to?: string
}

/** A full commit id (SHA-1 or SHA-256), which always names the same checkpoint. */
export function isCommitId(revision: string): boolean {
  return /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/i.test(revision)
}

/** How long a diff that can change is taken as current. */
export const UNCOMMITTED_STALE_MS = 5000

export function historyDiffQuery(universe: string, entity: string, selection: DiffSelection) {
  const { checkpoint, from, to } = selection
  // One checkpoint's change, or the change between two, named by full commit
  // ids, can't change, so it's fetched once. A name that can move (a short
  // id, a tag, HEAD~1), or a diff up to the files as they are now, is
  // fetched again when the page comes back to it, but not more often than
  // every few seconds.
  const fixed = checkpoint !== undefined ? isCommitId(checkpoint) : from !== undefined && to !== undefined && isCommitId(from) && isCommitId(to)
  return queryOptions({
    queryKey: ['history-diff', universe, entity, checkpoint ?? null, from ?? null, to ?? null],
    queryFn: () => getJson<HistoryDiffResult>(apiPath('/api/history/diff', { entity, universe, checkpoint, from, to })),
    staleTime: fixed ? Infinity : UNCOMMITTED_STALE_MS,
  })
}
