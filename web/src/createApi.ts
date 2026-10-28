import { queryOptions } from '@tanstack/react-query'
import { ApiRequestError, getJson, type ApiError, type ScopeKind } from './api'
import type { WriteResult } from './editApi'

// Creating notes from type templates (#29; src/Wolfden.Api/CreateEndpoints.cs),
// in camelCase: the den's types, and a new note made from one.

/** One type a new note can be made from (GET /api/types). */
export interface TypeView {
  key: string
  name: string
  /** Its folder under entities/, such as characters. */
  plural: string
  description: string | null
  pack: string
  builtIn: boolean
}

export interface TypeListResult {
  version: number
  /** By display name. */
  types: TypeView[]
}

/** The den's enabled types, built-in and den-local. They're the den's, not a universe's. */
export const typesQuery = queryOptions({
  queryKey: ['types'],
  queryFn: () => getJson<TypeListResult>('/api/types'),
})

/** Where a new note goes: the universe, or one of its series or books. */
export interface NewScope {
  kind: ScopeKind
  key: string
}

/** What the New note form sends (POST /api/entity/create). */
export interface CreateRequest {
  type: string
  name: string
  universe: string
  series?: string
  book?: string
  /** Where the reader first meets it; left out, it's canon from the start of where it goes. */
  from?: string
  /** Make it even though links that reach another note would reach this one (they're listed in the result's effects). */
  allowRelinks?: boolean
}

/**
 * A create the API refused. For one refused because links that reach
 * another note would reach the new one (#29), `relinks` lists them, in
 * words, and the form offers Create anyway.
 */
export class CreateError extends ApiRequestError {
  readonly relinks: string[] | null

  constructor(code: string, message: string, relinks: string[] | null) {
    super(code, message)
    this.relinks = relinks
  }
}

/** Creates a note from its type's template, sent as JSON (the API takes nothing else), and returns the write's result, or throws a CreateError. */
export async function createEntity(body: CreateRequest): Promise<WriteResult> {
  const response = await fetch('/api/entity/create', {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const answer: unknown = await response.json().catch(() => undefined)
  const json = typeof answer === 'object' && answer !== null ? (answer as Partial<ApiError> & { relinks?: unknown }) : null
  if (!response.ok) {
    const relinks = Array.isArray(json?.relinks) ? json.relinks.filter((r): r is string => typeof r === 'string') : null
    throw new CreateError(
      typeof json?.code === 'string' ? json.code : 'http_error',
      typeof json?.message === 'string' ? json.message : `The server answered ${response.status}.`,
      relinks && relinks.length > 0 ? relinks : null,
    )
  }

  const result = json as unknown as WriteResult | null
  if (!result?.entity) {
    throw new ApiRequestError('bad_response', "The server's answer wasn't the JSON the page expects.")
  }

  return result
}
