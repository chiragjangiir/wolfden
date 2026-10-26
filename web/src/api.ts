import { QueryClient, queryOptions } from '@tanstack/react-query'
import type { TextMatch } from './snippet'

// The shapes of Wolfden's API (src/Wolfden.Api/DenEndpoints.cs,
// ReadEndpoints.cs, ReadResults.cs, and Wolfden.Index's read results), in
// camelCase. Only the members the pages use are listed.

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

export interface EntityCounts {
  total: number
  byType: Record<string, number>
}

export interface ChapterNode {
  key: string
  number: number | null
  title: string | null
  position: string
}

export interface BookNode {
  key: string
  name: string
  id: string | null
  path: string
  status: string
  position: string
  entities: EntityCounts
  chapters: ChapterNode[]
}

export interface SeriesNode {
  key: string
  name: string
  id: string | null
  path: string
  entities: EntityCounts
  books: BookNode[]
}

export interface UniverseNode {
  key: string
  name: string
  id: string | null
  path: string
  entities: EntityCounts
  series: SeriesNode[]
  books: BookNode[]
}

export interface HierarchyResult {
  version: number
  position: string | null
  universe: UniverseNode
}

export interface PositionView {
  position: string
  book: string
  bookName: string
  series: string | null
  chapter: string | null
  number: number | null
  title: string | null
}

export interface PositionsResult {
  version: number
  universe: string
  count: number
  positions: PositionView[]
}

export type ScopeKind = 'universe' | 'series' | 'book'

export interface EntitySummary {
  key: string
  id: string | null
  name: string
  type: string
  status: string
  universe: string
  scopeKind: ScopeKind
  scopeKey: string
  path: string
  from: string | null
}

export interface EntityListResult {
  version: number
  count: number
  total: number
  truncated: boolean
  entities: EntitySummary[]
}

export interface EntityRef {
  key: string
  id: string | null
  name: string
  type: string
}

export interface ContainerRef {
  kind: ScopeKind
  key: string
  name: string
  id: string | null
  path: string
}

export interface LinkView {
  text: string
  target: string
  heading: string | null
  label: string | null
  line: number
  column: number
  outcome: 'resolved' | 'ambiguous' | 'broken'
  entity: EntityRef | null
  section: string | null
  matches: EntityRef[] | null
  problem: string | null
  inView: boolean | null
}

export interface PageField {
  name: string
  value: unknown
  kind: 'plain' | 'entry' | 'temporal' | 'default'
  status: string | null
  pinned: boolean
  from: string | null
  until: string | null
  line: number
  entryIndex: number | null
  live: boolean | null
  links: LinkView[]
}

export interface PageSection {
  name: string
  level: number
  headingPath: string[]
  status: string
  pinned: boolean
  from: string | null
  until: string | null
  line: number
  body: string
  bookkeeping: boolean | null
  links: LinkView[]
}

export interface EntryView {
  position: string | null
  inherited: boolean
  description: string
}

export interface BacklinkView {
  source: EntityRef | null
  container: ContainerRef | null
  site: string
  path: string
  line: number
  column: number
  text: string
  field: string | null
  section: string | null
  chapter: string | null
  outlineRow: string | null
  status: string | null
  bookkeeping: boolean
}

export interface EntityPage {
  key: string
  id: string | null
  name: string
  type: string
  status: string
  universe: string
  scopeKind: ScopeKind
  scopeKey: string
  path: string
  from: string | null
  aliases: string[] | null
  lead: string | null
  fields: PageField[]
  sections: PageSection[]
  scope: ContainerRef[]
  entry: EntryView
  leadLinks: LinkView[]
  backlinks: BacklinkView[]
}

export interface EntityPageResult {
  version: number
  view: string
  position: string | null
  inView: boolean
  note: string | null
  entity: EntityPage | null
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
 * An API path with its query parameters; those left undefined are left out.
 * An empty value is sent as it is, so the API refuses it (an empty `asOf` is
 * `bad_argument`) rather than the page quietly showing everything.
 */
export function apiPath(path: string, params: Record<string, string | number | undefined>): string {
  const query = new URLSearchParams()
  for (const [name, value] of Object.entries(params)) {
    if (value !== undefined) {
      query.set(name, String(value))
    }
  }

  const text = query.toString()
  return text ? `${path}?${text}` : path
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

export const denQuery = queryOptions({
  queryKey: ['den'],
  queryFn: () => getJson<DenSummary>('/api/den'),
})

// Every query that depends on the story position has the position in its key
// (null for the full view), so each position is cached on its own.

export function hierarchyQuery(universe: string, at: string | undefined) {
  return queryOptions({
    queryKey: ['hierarchy', universe, at ?? null],
    queryFn: () => getJson<HierarchyResult>(apiPath('/api/hierarchy', { universe, asOf: at })),
  })
}

export function positionsQuery(universe: string) {
  return queryOptions({
    queryKey: ['positions', universe],
    queryFn: () => getJson<PositionsResult>(apiPath('/api/positions', { universe })),
  })
}

/** The most entities one list asks for: the API's own limit. */
export const ENTITY_LIMIT = 1000

export function entitiesQuery(universe: string, at: string | undefined) {
  return queryOptions({
    queryKey: ['entities', universe, at ?? null],
    queryFn: () => getJson<EntityListResult>(apiPath('/api/entities', { universe, asOf: at, limit: ENTITY_LIMIT })),
  })
}

export function entityQuery(universe: string, entity: string, at: string | undefined) {
  return queryOptions({
    queryKey: ['entity', universe, entity, at ?? null],
    queryFn: () => getJson<EntityPageResult>(apiPath('/api/entity', { entity, universe, asOf: at })),
  })
}

/** "1 book", "2 books". */
export function count(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`
}

export interface SearchHit {
  entity: EntitySummary
  /** The section the match is in, or null for the name, aliases, lead, or a field. */
  section: string | null
  line: number | null
  /** The field the match is in (#179), or null (or left out) for the name, aliases, lead, or a section. */
  field?: string | null
  /** An excerpt with each match in square brackets, as the MCP tool gives it. The page marks matches from `text` and `matches`. */
  snippet: string
  /** The excerpt without marks. */
  text: string
  /** Where the matches are in `text`. */
  matches: TextMatch[]
  status: string
  inCanon: boolean
  inComment: boolean
}

/** The field names a search can be narrowed to (/api/fields): as of a position, only those of values the reader has met. */
export interface FieldNamesResult {
  version: number
  position: string | null
  count: number
  fields: string[]
}

export interface SearchResult {
  version: number
  query: string
  count: number
  truncated: boolean
  hits: SearchHit[]
}

/** What a search asks for besides its words: the filters /api/search takes. */
export interface SearchFilters {
  /** Search only this field's values. */
  field?: string
  type?: string
  status?: string
  series?: string
  book?: string
  canonOnly?: boolean
}

/** The statuses /api/search's `status` takes (DenReader.Statuses). */
export const STATUSES = ['idea', 'undecided', 'candidate', 'draft', 'canon', 'retconned'] as const

/** How many hits a search asks for at first, then on each "Show more", up to the API's own limit. */
export const SEARCH_LIMITS = [20, 50, 100, 200] as const

/** What a search's cache key holds: everything it was asked with, the position as `at` (null for everything). */
export interface SearchKey {
  universe: string
  query: string
  field: string | null
  type: string | null
  status: string | null
  series: string | null
  book: string | null
  canonOnly: boolean
  at: string | null
  limit: number
}

export function searchQuery(universe: string, query: string, filters: SearchFilters, at: string | undefined, limit: number) {
  const { field, type, status, series, book, canonOnly } = filters
  const key: SearchKey = {
    universe,
    query,
    field: field ?? null,
    type: type ?? null,
    status: status ?? null,
    series: series ?? null,
    book: book ?? null,
    canonOnly: canonOnly ?? false,
    at: at ?? null,
    limit,
  }
  return queryOptions({
    queryKey: ['search', key] as const,
    queryFn: () =>
      getJson<SearchResult>(
        apiPath('/api/search', { query, universe, field, type, status, series, book, canonOnly: canonOnly ? 'true' : undefined, asOf: at, limit }),
      ),
  })
}

/** The fields a search can be narrowed to: as of a position, only those the reader has met values of. */
export function fieldsQuery(universe: string, at: string | undefined) {
  return queryOptions({
    queryKey: ['fields', universe, at ?? null],
    queryFn: () => getJson<FieldNamesResult>(apiPath('/api/fields', { universe, asOf: at })),
  })
}
