import { QueryClient } from '@tanstack/react-query'

// The shapes of Wolfden's API (src/Wolfden.Api/DenEndpoints.cs), in camelCase.

export interface UniverseSummary {
  key: string
  name: string
  id: string | null
  series: number
  books: number
  entities: number
}

export interface DenSummary {
  version: number
  name: string
  pinnedUniverse: string | null
  universes: UniverseSummary[]
}

export interface ApiError {
  code: string
  message: string
}

/** An error the API answered with, carrying its stable code. */
export class ApiRequestError extends Error {
  readonly code: string

  constructor(code: string, message: string) {
    super(message)
    this.code = code
  }
}

/** Fetches JSON from the API, turning an error answer into an ApiRequestError. */
export async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(path, { headers: { Accept: 'application/json' } })
  const body: unknown = await response.json().catch(() => undefined)
  if (!response.ok) {
    const error = isObject(body) ? (body as Partial<ApiError>) : null
    throw new ApiRequestError(
      typeof error?.code === 'string' ? error.code : 'http_error',
      typeof error?.message === 'string' ? error.message : `The server answered ${response.status}.`,
    )
  }

  if (!isObject(body)) {
    throw new ApiRequestError('bad_response', `The server's answer to ${path} wasn't the JSON the page expects.`)
  }

  return body as T
}

function isObject(value: unknown): value is object {
  return typeof value === 'object' && value !== null
}

/**
 * The page's query client. An error the API answered with (an ApiRequestError,
 * such as den_not_loaded) comes back the same until the writer acts, so it's
 * shown at once rather than tried again; a failed connection is tried again.
 */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: (failures, error) => !(error instanceof ApiRequestError) && failures < 3,
      },
    },
  })
}

export const denQuery = {
  queryKey: ['den'],
  queryFn: () => getJson<DenSummary>('/api/den'),
} as const

/** "1 book", "2 books". */
export function count(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`
}
