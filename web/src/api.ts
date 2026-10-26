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
  const body: unknown = await response.json().catch(() => null)
  if (!response.ok) {
    const error = body as Partial<ApiError> | null
    throw new ApiRequestError(error?.code ?? 'http_error', error?.message ?? `The server answered ${response.status}.`)
  }

  return body as T
}

export const denQuery = {
  queryKey: ['den'],
  queryFn: () => getJson<DenSummary>('/api/den'),
} as const

/** "1 book", "2 books". */
export function count(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`
}
