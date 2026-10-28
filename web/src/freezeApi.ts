import { queryOptions } from '@tanstack/react-query'
import { ApiRequestError, apiPath, getJson, type ApiError } from './api'
import type { DiagnosticView } from './editApi'

// Freezing and unfreezing a book (#31; src/Wolfden.Api/FreezeEndpoints.cs),
// in camelCase. A freeze marks the book `status: final`, then, when the den
// has history, checkpoints and tags the snapshot; an unfreeze writes
// `status: draft` and checkpoints.

/** A book's freeze status (GET /api/book/freeze). */
export interface BookFreeze {
  version: number
  universe: string
  book: string
  name: string
  path: string
  /** The hash of the book's book.md, to send back with a freeze or unfreeze. */
  hash: string
  status: 'final' | 'draft'
  backend: string
  /** True if the den has history, so a freeze checkpoints and tags, and an unfreeze checkpoints. */
  history: boolean
  /** Why there's no history, or null. */
  reason: string | null
  /** For a draft book in a den with history, the tag a freeze would add, such as book/dracula/dracula/final. */
  nextTag: string | null
  /** For a final book in a den with history: its snapshot isn't finished (git refused the checkpoint or the tag), and freezing again finishes it. */
  unfinished: boolean
}

/** What a step in the den's history did. */
export type FreezeSnapshot =
  | 'tagged'
  | 'already_tagged'
  | 'no_history'
  | 'checkpoint_failed'
  | 'tag_failed'
  | 'not_tagged'
  | 'checkpointed'
  | 'not_checkpointed'
  | 'not_frozen'

/** What a freeze or unfreeze did (POST /api/book/freeze, /api/book/unfreeze). */
export interface FreezeResult {
  version: number
  universe: string
  book: string
  path: string
  hash: string
  status: 'final' | 'draft'
  changed: boolean
  backend: string
  snapshot: FreezeSnapshot
  checkpoint: string | null
  tag: string | null
  /** What happened, in words for the writer, including what to do if a step failed. */
  message: string
  warnings: DiagnosticView[]
  idAdded: string | null
}

/** Snapshots where a step in the history failed: the status changed, but the writer has something to do. */
export const FAILED_SNAPSHOTS: readonly FreezeSnapshot[] = ['checkpoint_failed', 'tag_failed']

export function bookFreezeQuery(universe: string, book: string) {
  return queryOptions({
    queryKey: ['book-freeze', universe, book],
    queryFn: () => getJson<BookFreeze>(apiPath('/api/book/freeze', { universe, book })),
    // A freeze always starts from the book.md as it is.
    staleTime: 0,
  })
}

/** Freezes or unfreezes a book, sent as JSON (the API takes nothing else), and returns what it did, or throws an ApiRequestError. */
export async function changeFreeze(action: 'freeze' | 'unfreeze', body: { universe: string; book: string; expectedHash: string }): Promise<FreezeResult> {
  const response = await fetch(`/api/book/${action}`, {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const answer: unknown = await response.json().catch(() => undefined)
  const json = typeof answer === 'object' && answer !== null ? (answer as Partial<ApiError>) : null
  if (!response.ok) {
    throw new ApiRequestError(
      typeof json?.code === 'string' ? json.code : 'http_error',
      typeof json?.message === 'string' ? json.message : `The server answered ${response.status}.`,
    )
  }

  if (!json) {
    throw new ApiRequestError('bad_response', "The server's answer wasn't the JSON the page expects.")
  }

  return json as unknown as FreezeResult
}
