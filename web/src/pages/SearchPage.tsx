import { useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, getRouteApi } from '@tanstack/react-router'
import {
  ApiRequestError,
  SEARCH_LIMITS,
  STATUSES,
  count,
  fieldsQuery,
  hierarchyQuery,
  positionsQuery,
  searchQuery,
  type HierarchyResult,
  type SearchFilters,
  type SearchHit,
  type SearchKey,
  type SearchResult,
  type EntitySummary,
} from '../api'
import { EntityLink, sectionAnchor } from '../components/Links'
import { ErrorState, Loading, StatusBadge } from '../components/States'
import { useDocumentTitle } from '../hooks'
import { snippetParts, type TextMatch } from '../snippet'
import { describePosition } from '../story'
import type { SearchPageSearch } from '../router'

const route = getRouteApi('/u/$universe/search')

/**
 * The search page (#25): full-text search of a universe's notes and their
 * field values, filtered by type, scope, and status, or to canon text only,
 * or narrowed to one field's values (#179). As of a position, it finds only
 * what the reader has met by then (the API's asOf), and says so.
 */
export function SearchPage() {
  const { universe } = route.useParams()
  const search = route.useSearch() as SearchPageSearch
  const navigate = route.useNavigate()
  // A blank `at` is '' (it fails closed, as on every page), never everything.
  const at = search.at
  // Scopes are books and series, shown whatever the position; types only as
  // the reader has met them, so a type met later isn't named.
  const hierarchy = useQuery(hierarchyQuery(universe, undefined))
  const metHierarchy = useQuery({ ...hierarchyQuery(universe, at), enabled: at !== '' })
  // Fields as types are: only those of values the reader has met.
  const fields = useQuery({ ...fieldsQuery(universe, at), enabled: at !== '' })
  const positions = useQuery(positionsQuery(universe))
  const q = search.q
  const filters: SearchFilters = {
    field: search.field,
    type: search.type,
    status: search.status,
    series: search.series,
    book: search.book,
    canonOnly: search.canon,
  }
  const filtered = Object.values(filters).some((v) => v !== undefined && v !== false)

  const setFilters = (patch: Partial<SearchPageSearch>) =>
    void navigate({ search: (prev: SearchPageSearch) => ({ ...prev, ...patch }), replace: true })

  const where = at === undefined || at === '' ? undefined : describePosition(at, positions.data?.positions ?? [])

  useDocumentTitle(q ? `Search: ${q}` : 'Search')

  return (
    <div className="page-body search-page">
      <p className="eyebrow">Search</p>
      <h1>{q ? <>Matches for “{q}”</> : 'Search'}</h1>
      <Filters hierarchy={hierarchy.data} metHierarchy={metHierarchy.data} fields={fields.data?.fields} search={search} onChange={setFilters} />
      {at === '' && !q && <ErrorState error={new ApiRequestError('bad_argument', "The story position in the address is empty, so there's nothing to search as of.")} />}
      {where !== undefined && (
        <p className="search-position">
          Searching as of <strong>{where}</strong>: only what the reader has met by then.{' '}
          <Link to="." search={(prev: SearchPageSearch) => ({ ...prev, at: undefined })}>
            Search everything
          </Link>
        </p>
      )}
      {q ? (
        <Results universe={universe} q={q} filters={filters} filtered={filtered} at={at} where={where} hierarchy={hierarchy.data} />
      ) : (
        <p className="muted">Type in the search box at the top to search this universe's notes. Press / to go to it from anywhere.</p>
      )}
    </div>
  )
}

function Filters({
  hierarchy,
  metHierarchy,
  fields,
  search,
  onChange,
}: {
  hierarchy: HierarchyResult | undefined
  metHierarchy: HierarchyResult | undefined
  fields: string[] | undefined
  search: SearchPageSearch
  onChange: (patch: Partial<SearchPageSearch>) => void
}) {
  const u = hierarchy?.universe
  const books = u ? [...u.series.flatMap((s) => s.books), ...u.books] : []
  const types = typesIn(metHierarchy)
  // A filter the lists don't have yet (still loading, or typed into the URL) stays chosen.
  const withCurrent = (list: string[], current: string | undefined) => (current && !list.includes(current) ? [...list, current] : list)
  const scope = search.book ? `book:${search.book}` : search.series ? `series:${search.series}` : ''

  return (
    <div className="search-filters" role="group" aria-label="Filters">
      <label>
        <span>Field</span>
        <select value={search.field ?? ''} onChange={(e) => onChange({ field: e.currentTarget.value || undefined })}>
          <option value="">Anywhere</option>
          {withCurrent(fields ?? [], search.field).map((f) => (
            <option key={f} value={f}>
              {f}
            </option>
          ))}
        </select>
      </label>
      <label>
        <span>Type</span>
        <select value={search.type ?? ''} onChange={(e) => onChange({ type: e.currentTarget.value || undefined })}>
          <option value="">Any type</option>
          {withCurrent(types, search.type).map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
      </label>
      <label>
        <span>Scope</span>
        <select
          value={scope}
          onChange={(e) => {
            const [kind, key] = e.currentTarget.value.split(/:(.*)/s)
            onChange({ series: kind === 'series' ? key : undefined, book: kind === 'book' ? key : undefined })
          }}
        >
          <option value="">Whole universe</option>
          {u && u.series.length > 0 && (
            <optgroup label="Series">
              {u.series.map((s) => (
                <option key={s.key} value={`series:${s.key}`}>
                  {s.name}
                </option>
              ))}
            </optgroup>
          )}
          <optgroup label="Books">
            {books.map((b) => (
              <option key={b.key} value={`book:${b.key}`}>
                {b.name}
              </option>
            ))}
            {scope && !u && <option value={scope}>{search.book ?? search.series}</option>}
          </optgroup>
        </select>
      </label>
      <label>
        <span>Status</span>
        <select value={search.status ?? ''} onChange={(e) => onChange({ status: e.currentTarget.value || undefined })}>
          <option value="">Any status</option>
          {withCurrent([...STATUSES], search.status).map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
      </label>
      <label className="check">
        <input type="checkbox" checked={search.canon === true} onChange={(e) => onChange({ canon: e.currentTarget.checked || undefined })} />
        <span>Canon text only</span>
      </label>
    </div>
  )
}

/** The types of the notes a hierarchy counts: as of a position, only those the reader has met. */
function typesIn(hierarchy: HierarchyResult | undefined): string[] {
  const u = hierarchy?.universe
  if (!u) {
    return []
  }

  const books = [...u.series.flatMap((s) => s.books), ...u.books]
  const counts = [u.entities, ...u.series.map((s) => s.entities), ...books.map((b) => b.entities)]
  return [...new Set(counts.flatMap((c) => Object.entries(c.byType).filter(([, n]) => n > 0).map(([type]) => type)))].sort()
}

/**
 * Keeps the last answer while the next one loads, but only at the same
 * position: one from a later position could show what the reader hasn't met.
 */
export function keepAtSamePosition(at: string | undefined) {
  return (previous: SearchResult | undefined, previousQuery: { queryKey: readonly unknown[] } | undefined): SearchResult | undefined => {
    const key = previousQuery?.queryKey[1] as SearchKey | undefined
    return key && key.at === (at ?? null) ? previous : undefined
  }
}

function Results({
  universe,
  q,
  filters,
  filtered,
  at,
  where,
  hierarchy,
}: {
  universe: string
  q: string
  filters: SearchFilters
  filtered: boolean
  at: string | undefined
  where: string | undefined
  hierarchy: HierarchyResult | undefined
}) {
  // "Show more" asks again for more hits; a new search starts from the first step.
  const searchKey = JSON.stringify([q, filters, at])
  const [more, setMore] = useState({ key: searchKey, step: 0 })
  const step = more.key === searchKey ? more.step : 0
  const result = useQuery({
    ...searchQuery(universe, q, filters, at, SEARCH_LIMITS[step]),
    placeholderData: keepAtSamePosition(at),
  })
  // After "Show more", when the button goes (every match is shown, or the
  // API's limit is reached), focus moves to the line that says so.
  const askedForMore = useRef(false)
  const end = useRef<HTMLParagraphElement>(null)
  const showsButton = result.data?.truncated === true && step < SEARCH_LIMITS.length - 1
  const settled = result.isSuccess && !result.isPlaceholderData
  useEffect(() => {
    if (askedForMore.current && settled && !showsButton) {
      askedForMore.current = false
      end.current?.focus()
    }
  }, [settled, showsButton])

  if (result.isPending) {
    return <Loading what="matches" />
  }

  if (result.isError) {
    return <ErrorState error={result.error} />
  }

  // The answer shown may be the last search's while the next loads; its hits mark their own matches.
  const { hits, truncated } = result.data
  if (hits.length === 0) {
    return (
      <p className="muted" role="status">
        {where !== undefined ? `No matches as of ${where}.` : `No matches for “${q}”.`}
        {filtered && ' Try without some of the filters.'}
      </p>
    )
  }

  const groups = groupByEntity(hits)
  const last = step === SEARCH_LIMITS.length - 1
  return (
    <div aria-busy={result.isPlaceholderData} className={result.isPlaceholderData ? 'search-results stale' : 'search-results'}>
      <p className="muted" role="status">
        {truncated ? `The first ${count(hits.length, 'match', 'matches')}` : count(hits.length, 'match', 'matches')}, in{' '}
        {count(groups.length, 'note', 'notes')}.
      </p>
      <ol className="hit-groups">
        {groups.map(({ entity, hits: own }) => (
          <li key={entity.key}>
            <p className="hit-entity">
              <EntityLink universe={universe} entityKey={entity.key}>
                {entity.name}
              </EntityLink>{' '}
              <StatusBadge status={entity.status} /> <span className="muted">{entity.type}, in {scopeName(entity, hierarchy)}</span>
            </p>
            <ul className="hits">
              {own.map((hit, i) => (
                <Hit key={i} universe={universe} hit={hit} />
              ))}
            </ul>
          </li>
        ))}
      </ol>
      {truncated && !last ? (
        <button
          type="button"
          onClick={() => {
            askedForMore.current = true
            setMore({ key: searchKey, step: step + 1 })
          }}
          disabled={result.isFetching}
        >
          Show more matches
        </button>
      ) : truncated ? (
        <p className="muted" ref={end} tabIndex={-1}>
          Only the first {hits.length} matches are shown. Add words or a filter to narrow the search.
        </p>
      ) : (
        step > 0 && (
          <p className="muted" ref={end} tabIndex={-1}>
            That's every match.
          </p>
        )
      )}
    </div>
  )
}

function Hit({ universe, hit }: { universe: string; hit: SearchHit }) {
  const bookkeeping = !hit.inCanon && !hit.inComment && !hit.field && (hit.status === 'canon' || hit.status === 'draft')
  return (
    <li>
      <p className="hit-place">
        <Link
          to="/u/$universe/e/$"
          params={{ universe, _splat: hit.entity.key }}
          hash={hit.section ? sectionAnchor(hit.section) : undefined}
        >
          {hit.field ? `Field: ${hit.field}` : (hit.section ?? 'Name or opening')}
        </Link>
        {hit.status !== hit.entity.status && <StatusBadge status={hit.status} />}
        {bookkeeping && <span className="badge">bookkeeping</span>}
        {hit.inComment && <span className="badge">in a comment</span>}
      </p>
      <p className="snippet">
        <Snippet text={hit.text} matches={hit.matches} />
      </p>
    </li>
  )
}

/** A snippet with its matches marked, rendered as text. */
export function Snippet({ text, matches }: { text: string; matches: readonly TextMatch[] }) {
  return (
    <>
      {snippetParts(text, matches).map((part, i) => (part.match ? <mark key={i}>{part.text}</mark> : part.text))}
    </>
  )
}

function groupByEntity(hits: readonly SearchHit[]): { entity: EntitySummary; hits: SearchHit[] }[] {
  const groups = new Map<string, { entity: EntitySummary; hits: SearchHit[] }>()
  for (const hit of hits) {
    const group = groups.get(hit.entity.key) ?? { entity: hit.entity, hits: [] }
    group.hits.push(hit)
    groups.set(hit.entity.key, group)
  }

  return [...groups.values()]
}

/** The name of the universe, series, or book an entity is filed in. */
function scopeName(entity: EntitySummary, hierarchy: HierarchyResult | undefined): string {
  const u = hierarchy?.universe
  if (!u) {
    return entity.scopeKey
  }

  switch (entity.scopeKind) {
    case 'universe':
      return u.name
    case 'series':
      return u.series.find((s) => s.key === entity.scopeKey)?.name ?? entity.scopeKey
    default:
      return [...u.series.flatMap((s) => s.books), ...u.books].find((b) => b.key === entity.scopeKey)?.name ?? entity.scopeKey
  }
}
