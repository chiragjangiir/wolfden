import { queryOptions, type QueryClient } from '@tanstack/react-query'
import { ApiRequestError, getJson, type ApiError } from './api'
import type { ChangeKind } from './historyApi'

// The shapes of /api/checkpoint (Wolfden.Api's CheckpointEndpoints), in
// camelCase (#30).

export interface PendingChange {
  kind: ChangeKind
  path: string
  oldPath: string | null
  /** The universe it's in, or null for a den-level file such as den.yaml. */
  universe: string | null
  /** True if this server's web UI saved it and nothing else changed it since. */
  savedHere: boolean
}

export interface CheckpointSummary {
  id: string
  when: string
  author: string
  message: string
  files: number
}

export interface AutoCheckpointView {
  /** The wait, such as "30m", or null when automatic checkpoints are off. */
  every: string | null
  problem: string | null
  /** True if this server makes automatic checkpoints when the setting is on. */
  runs: boolean
}

export interface CheckpointStatus {
  version: number
  backend: string
  available: boolean
  reason: string | null
  /** For a den without history, how to turn it on. */
  enable: string | null
  /** The latest checkpoint, by id and time only (its message could name another universe's notes on a pinned server). */
  latest: { id: string; when: string } | null
  /** What git is in the middle of (a rebase, a merge, a detached HEAD), or null. */
  inProgress: string | null
  count: number
  changes: PendingChange[]
  /** For a pinned server: changed files in other universes, recorded too but not named. */
  otherUniverses: number
  autoCheckpoint: AutoCheckpointView
}

export interface CheckpointMade {
  version: number
  backend: string
  recorded: boolean
  checkpoint: CheckpointSummary | null
  inProgress: string | null
  changes: PendingChange[]
  otherUniverses: number
}

/**
 * The checkpoint status's query key. Anything that changes the den's files
 * (the editor's saves, #28) invalidates it, so the indicator catches up at
 * once rather than at the next poll.
 */
export const CHECKPOINT_STATUS_KEY = ['checkpoint-status'] as const

/** How often the indicator asks again while the page is open. */
export const CHECKPOINT_POLL_MS = 60_000

export const checkpointStatusQuery = queryOptions({
  queryKey: CHECKPOINT_STATUS_KEY,
  queryFn: () => getJson<CheckpointStatus>('/api/checkpoint'),
  refetchInterval: CHECKPOINT_POLL_MS,
  refetchOnWindowFocus: true,
  staleTime: 5000,
})

/** After a save elsewhere in the page: the indicator should look again. */
export function invalidateCheckpointStatus(client: QueryClient): Promise<void> {
  return client.invalidateQueries({ queryKey: CHECKPOINT_STATUS_KEY })
}

/**
 * Makes a checkpoint with the writer's message. It's sent as JSON, which
 * the server requires of every write, so another site can't make one.
 */
export async function makeCheckpoint(message: string): Promise<CheckpointMade> {
  const response = await fetch('/api/checkpoint', {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify({ message }),
  })
  const body: unknown = await response.json().catch(() => undefined)
  if (!response.ok) {
    const error = typeof body === 'object' && body !== null ? (body as Partial<ApiError>) : null
    throw new ApiRequestError(
      typeof error?.code === 'string' ? error.code : 'http_error',
      typeof error?.message === 'string' ? error.message : `The server answered ${response.status}.`,
    )
  }

  if (typeof body !== 'object' || body === null) {
    throw new ApiRequestError('bad_response', "The server's answer to the checkpoint wasn't the JSON the page expects.")
  }

  return body as CheckpointMade
}

/** One group of changes in the dialog: a universe's, one of its books' or series', or the den's own files. */
export interface ChangeGroup {
  key: string
  /** What the group is, such as "Universe dracula · Book dracula". */
  title: string
  /** Each change, with its path shortened to within the group's folder. */
  changes: { change: PendingChange; shown: string }[]
}

/**
 * Groups changes the way a notes pass is reviewed: by universe, then by the
 * book or series they're filed under (the folder layout, design.md §3), so
 * a pass over one book's notes reads as one block. Den-level files (den.yaml,
 * packs) come first.
 */
export function groupChanges(changes: readonly PendingChange[]): ChangeGroup[] {
  const groups = new Map<string, ChangeGroup>()
  for (const change of changes) {
    const { key, title, prefix } = scopeOf(change.path)
    let group = groups.get(key)
    if (!group) {
      group = { key, title, changes: [] }
      groups.set(key, group)
    }

    group.changes.push({ change, shown: change.path.slice(prefix.length) })
  }

  return [...groups.values()].sort((a, b) => (a.key === '' ? -1 : b.key === '' ? 1 : a.key.localeCompare(b.key)))
}

function scopeOf(path: string): { key: string; title: string; prefix: string } {
  const parts = path.split('/')
  if (parts[0] !== 'universes' || parts.length < 3) {
    return { key: '', title: 'Den settings and packs', prefix: '' }
  }

  const universe = parts[1]
  const base = `universes/${universe}/`
  // universes/u/series/s/books/b/…, universes/u/series/s/…, universes/u/books/b/…
  if (parts[2] === 'series' && parts.length > 4) {
    if (parts[4] === 'books' && parts.length > 6) {
      return {
        key: `${base}series/${parts[3]}/books/${parts[5]}`,
        title: `Universe ${universe} · Series ${parts[3]} · Book ${parts[5]}`,
        prefix: `${base}series/${parts[3]}/books/${parts[5]}/`,
      }
    }

    return { key: `${base}series/${parts[3]}`, title: `Universe ${universe} · Series ${parts[3]}`, prefix: `${base}series/${parts[3]}/` }
  }

  if (parts[2] === 'books' && parts.length > 4) {
    return { key: `${base}books/${parts[3]}`, title: `Universe ${universe} · Book ${parts[3]}`, prefix: `${base}books/${parts[3]}/` }
  }

  return { key: base, title: `Universe ${universe}`, prefix: base }
}

/** How a change is said in the dialog. */
export function changeWord(kind: ChangeKind): string {
  switch (kind) {
    case 'modified':
      return 'edited'
    case 'added':
      return 'new'
    case 'deleted':
      return 'deleted'
    default:
      return 'renamed'
  }
}
