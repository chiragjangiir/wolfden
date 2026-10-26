import type { QueryClient } from '@tanstack/react-query'
import {
  createRootRouteWithContext,
  createRoute,
  createRouter,
  retainSearchParams,
  type RouterHistory,
} from '@tanstack/react-router'
import { Layout } from './components/Layout'
import { Home } from './pages/Home'
import { UniversePage } from './pages/UniversePage'
import { SeriesPage } from './pages/SeriesPage'
import { BookPage } from './pages/BookPage'
import { ChapterPage } from './pages/ChapterPage'
import { EntityPageView } from './pages/EntityPageView'
import { SearchPage } from './pages/SearchPage'
import { NotFound } from './components/States'

/**
 * The search params every page shares. `at` is the story position the pages
 * show the den as of (the API's asOf), or undefined for everything: the end
 * of the story, the author's full view.
 */
export interface StorySearch {
  at?: string
}

export function validateStorySearch(search: Record<string, unknown>): StorySearch {
  const at = typeof search.at === 'string' ? search.at.trim() : ''
  return at ? { at } : {}
}

/**
 * Plain query strings, so a link reads `?at=dracula:9`. The router's default
 * would quote a value that looks like JSON, such as a numeric book key.
 */
export function parseSearch(text: string): Record<string, unknown> {
  return Object.fromEntries(new URLSearchParams(text.startsWith('?') ? text.slice(1) : text))
}

export function stringifySearch(search: Record<string, unknown>): string {
  const parts = Object.entries(search)
    .filter(([, value]) => value !== undefined && value !== null && value !== '')
    .map(([name, value]) => `${encodeURIComponent(name)}=${encodeURIComponent(String(value)).replace(/%3A/gi, ':')}`)
  return parts.length ? `?${parts.join('&')}` : ''
}

interface RouterContext {
  queryClient: QueryClient
}

const rootRoute = createRootRouteWithContext<RouterContext>()({
  validateSearch: validateStorySearch,
  // The position carries from page to page unless a link sets it.
  search: { middlewares: [retainSearchParams(['at'])] },
  component: Layout,
  notFoundComponent: NotFound,
})

const homeRoute = createRoute({ getParentRoute: () => rootRoute, path: '/', component: Home })

const universeRoute = createRoute({ getParentRoute: () => rootRoute, path: '/u/$universe', component: UniversePage })

const seriesRoute = createRoute({ getParentRoute: () => rootRoute, path: '/u/$universe/s/$series', component: SeriesPage })

const bookRoute = createRoute({ getParentRoute: () => rootRoute, path: '/u/$universe/b/$book', component: BookPage })

const chapterRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/u/$universe/b/$book/c/$chapter',
  component: ChapterPage,
})

// An entity is named by its id, or by its path while it has none, so the
// rest of the URL is the name, slashes and all.
const entityRoute = createRoute({ getParentRoute: () => rootRoute, path: '/u/$universe/e/$', component: EntityPageView })

/**
 * The search page's params (#25): the words (`q`), and the filters, each
 * left out when unset. A series and a book don't go together, so a book
 * wins.
 */
export interface SearchPageSearch {
  q?: string
  type?: string
  status?: string
  series?: string
  book?: string
  canon?: boolean
}

export function validateSearchPageSearch(search: Record<string, unknown>): SearchPageSearch {
  const text = (name: string) => (typeof search[name] === 'string' ? (search[name] as string).trim() : '') || undefined
  const result: SearchPageSearch = {}
  for (const name of ['q', 'type', 'status', 'book'] as const) {
    const value = text(name)
    if (value) {
      result[name] = value
    }
  }

  const series = text('series')
  if (series && !result.book) {
    result.series = series
  }

  if (search.canon === true || search.canon === 'true') {
    result.canon = true
  }

  return result
}

const searchRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/u/$universe/search',
  validateSearch: validateSearchPageSearch,
  component: SearchPage,
})

const routeTree = rootRoute.addChildren([homeRoute, universeRoute, seriesRoute, bookRoute, chapterRoute, entityRoute, searchRoute])

export function createAppRouter(queryClient: QueryClient, history?: RouterHistory) {
  return createRouter({
    routeTree,
    history,
    context: { queryClient },
    parseSearch,
    stringifySearch,
    defaultPreload: false,
    scrollRestoration: true,
  })
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>
  }
}
