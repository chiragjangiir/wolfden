import { vi } from 'vitest'
import { render } from '@testing-library/react'
import { QueryClient } from '@tanstack/react-query'
import { createMemoryHistory } from '@tanstack/react-router'
import { App } from '../App'
import type {
  DenSummary,
  EntityListResult,
  EntityPage,
  EntityPageResult,
  EntitySummary,
  HierarchyResult,
  PositionView,
  PositionsResult,
} from '../api'

// A small den for the UI's tests, from Dracula (public domain): one book of
// five chapters and a closing note, and a few of its notes.

export const den: DenSummary = {
  version: 1,
  name: 'Dracula',
  pinnedUniverse: null,
  universes: [{ key: 'dracula', name: 'Dracula', id: 'U1', series: 0, books: 1, entities: 7 }],
}

const titles = ["Jonathan Harker's Journal", "Jonathan Harker's Journal", "Jonathan Harker's Journal", "Jonathan Harker's Journal", 'Letters—Lucy and Mina']

export const positions: PositionView[] = [
  ...titles.map((title, i) => ({
    position: `dracula:${i + 1}`,
    book: 'dracula',
    bookName: 'Dracula',
    series: null,
    chapter: String(i + 1),
    number: i + 1,
    title,
  })),
  { position: 'dracula:note', book: 'dracula', bookName: 'Dracula', series: null, chapter: 'note', number: null, title: 'Note' },
]

function entity(key: string, name: string, type: string, scopeKind: EntitySummary['scopeKind'], from: string | null, status = 'canon'): EntitySummary {
  return {
    key,
    id: key,
    name,
    type,
    status,
    universe: 'dracula',
    scopeKind,
    scopeKey: 'dracula',
    path: `universes/dracula/${name.toLowerCase().replace(/\W+/g, '-')}.md`,
    from,
  }
}

export const entities: EntitySummary[] = [
  entity('E-CASTLE', 'Castle Dracula', 'location', 'universe', 'dracula:1'),
  entity('E-DATES', 'Dates and the year', 'note', 'universe', null),
  entity('E-JONATHAN', 'Jonathan Harker', 'character', 'book', 'dracula:1'),
  entity('E-MINA', 'Mina Harker', 'character', 'book', 'dracula:1'),
  entity('E-LUCY', 'Lucy Westenra', 'character', 'book', 'dracula:5'),
  entity('E-QUINCEY', 'Quincey P. Morris', 'character', 'book', 'dracula:5'),
  entity('E-SCENE', 'A scene at the inn', 'event', 'book', null, 'idea'),
]

/** Which entities are in the canon view at a position: draft or canon, and entered by then. */
export function inViewAt(at: string): EntitySummary[] {
  const ordinal = (p: string) => (p === 'dracula' ? -0.5 : positions.findIndex((x) => x.position === p))
  return entities.filter((e) => ['canon', 'draft'].includes(e.status) && ordinal(e.from ?? 'dracula') <= ordinal(at))
}

export const hierarchy: HierarchyResult = {
  version: 1,
  position: null,
  universe: {
    key: 'dracula',
    name: 'Dracula',
    id: 'U1',
    path: 'universes/dracula',
    entities: { total: 2, byType: { location: 1, note: 1 } },
    series: [],
    books: [
      {
        key: 'dracula',
        name: 'Dracula',
        id: 'B1',
        path: 'universes/dracula/books/dracula',
        status: 'draft',
        position: 'dracula',
        entities: { total: 5, byType: { character: 4, event: 1 } },
        chapters: positions.map((p) => ({ key: p.chapter!, number: p.number, title: p.title, position: p.position })),
      },
    ],
  },
}

/** An entity page with nothing on it but its name, to build on. */
export function page(summary: EntitySummary, extra: Partial<EntityPage> = {}): EntityPage {
  return {
    ...summary,
    aliases: null,
    lead: `# ${summary.name}`,
    fields: [],
    sections: [],
    scope: [
      { kind: 'universe', key: 'dracula', name: 'Dracula', id: 'U1', path: 'universes/dracula' },
      { kind: 'book', key: 'dracula', name: 'Dracula', id: 'B1', path: 'universes/dracula/books/dracula' },
    ],
    entry: { position: summary.from, inherited: summary.from === null, description: '' },
    leadLinks: [],
    backlinks: [],
    ...extra,
  }
}

export type Responder = (path: string, params: URLSearchParams) => unknown

/**
 * Stubs fetch with the test den's API. `entityPage` answers /api/entity;
 * by default, the entity's bare page, or not in view as of a position
 * before it enters. `override` answers anything first, if it returns a value.
 */
export function stubApi(options: { entityPage?: (key: string, at: string | null) => EntityPageResult; override?: Responder; den?: DenSummary } = {}) {
  const calls: string[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), 'http://127.0.0.1')
    calls.push(`${url.pathname}${url.search}`)
    const params = url.searchParams
    const at = params.get('asOf')
    const body = options.override?.(url.pathname, params) ?? answer(url.pathname, params, at, options)
    if (body instanceof Response) {
      return body
    }

    return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
  })
  vi.stubGlobal('fetch', fetchMock)
  return { calls, fetchMock }
}

function answer(path: string, params: URLSearchParams, at: string | null, options: Parameters<typeof stubApi>[0] = {}): unknown {
  switch (path) {
    case '/api/den':
      return options.den ?? den
    case '/api/positions':
      return { version: 1, universe: 'dracula', count: positions.length, positions } satisfies PositionsResult
    case '/api/hierarchy':
      return { ...hierarchy, position: at }
    case '/api/entities': {
      const list = at ? inViewAt(at) : entities
      return { version: 1, count: list.length, total: list.length, truncated: false, entities: list } satisfies EntityListResult
    }
    case '/api/entity': {
      const key = params.get('entity')!
      if (options.entityPage) {
        return options.entityPage(key, at)
      }

      const summary = entities.find((e) => e.key === key)!
      const inView = !at || inViewAt(at).includes(summary)
      return {
        version: 1,
        view: at ? 'canon_as_of' : 'full',
        position: at,
        inView,
        note: inView ? null : `'${key}' isn't in canon as of ${at}.`,
        entity: inView ? page(summary) : null,
      } satisfies EntityPageResult
    }
    default:
      return new Response(JSON.stringify({ code: 'not_found', message: `No ${path}.` }), { status: 404 })
  }
}

/** Renders the whole app at `url`, with a memory history the test can read. */
export function renderApp(url: string) {
  // jsdom has no scrolling; the router restores scroll positions.
  window.scrollTo = () => {}
  const history = createMemoryHistory({ initialEntries: [url] })
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const result = render(<App queryClient={queryClient} history={history} />)
  return { ...result, history, queryClient }
}

/** The `at` in the history's current URL, or null. */
export function atOf(history: { location: { search: string } }): string | null {
  return new URLSearchParams(history.location.search).get('at')
}
