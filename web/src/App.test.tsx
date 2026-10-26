import { afterEach, describe, expect, it, vi } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App } from './App'
import { ApiRequestError, count, createQueryClient, denQuery, getJson, type DenSummary } from './api'

function render(client: QueryClient): string {
  return renderToStaticMarkup(
    <QueryClientProvider client={client}>
      <App />
    </QueryClientProvider>,
  )
}

function withDen(den: DenSummary): string {
  const client = new QueryClient()
  client.setQueryData(denQuery.queryKey, den)
  return render(client)
}

describe('App', () => {
  it('shows the den name and its universes', () => {
    const html = withDen({
      version: 1,
      name: 'Dracula',
      pinnedUniverse: null,
      universes: [{ key: 'dracula', name: 'Dracula', id: null, series: 0, books: 1, entities: 50 }],
    })

    expect(html).toContain('<h1>Dracula</h1>')
    expect(html).toContain('<code>dracula</code>: 0 series, 1 book, 50 notes')
  })

  it('says when the den has no universes', () => {
    expect(withDen({ version: 1, name: 'Empty', pinnedUniverse: null, universes: [] })).toContain('no universes yet')
  })

  it('says it is loading before the den arrives', () => {
    expect(render(new QueryClient())).toContain('Loading the den')
  })

  it("shows the API's message when the den can't be read", async () => {
    // Rendered once, as the page shows it after the failed fetch, without fetching again on mount.
    const client = new QueryClient({ defaultOptions: { queries: { retryOnMount: false } } })
    const message = "den.yaml doesn't load, so nothing in the den can be read. Run 'wolfden check' to see why."
    await client
      .fetchQuery({ queryKey: denQuery.queryKey, queryFn: () => Promise.reject(new ApiRequestError('den_not_loaded', message)), retry: false })
      .catch(() => undefined)

    const html = render(client)

    expect(html).toContain('role="alert"')
    expect(html).toContain('Run &#x27;wolfden check&#x27; to see why.')
  })
})

describe('getJson', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  function answer(status: number, body: string) {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(body, { status, headers: { 'Content-Type': 'application/json' } })))
  }

  it('returns the JSON of a good answer', async () => {
    answer(200, '{"name":"Dracula"}')
    await expect(getJson('/api/den')).resolves.toEqual({ name: 'Dracula' })
  })

  it("throws the API's error code and message", async () => {
    answer(503, '{"code":"den_not_loaded","message":"Run wolfden check."}')
    await expect(getJson('/api/den')).rejects.toMatchObject({ code: 'den_not_loaded', message: 'Run wolfden check.' })
  })

  it('throws for an error answer that is not JSON', async () => {
    answer(502, '<html>Bad gateway</html>')
    await expect(getJson('/api/den')).rejects.toMatchObject({ code: 'http_error', message: 'The server answered 502.' })
  })

  it('throws for a good answer that is not JSON, or is null', async () => {
    answer(200, '<!doctype html>')
    await expect(getJson('/api/den')).rejects.toMatchObject({ code: 'bad_response' })
    answer(200, 'null')
    await expect(getJson('/api/den')).rejects.toMatchObject({ code: 'bad_response' })
  })
})

describe('createQueryClient', () => {
  it("doesn't retry an error the API answered with, but retries a failed connection", () => {
    const retry = createQueryClient().getDefaultOptions().queries?.retry as (failures: number, error: Error) => boolean

    expect(retry(0, new ApiRequestError('den_not_loaded', 'x'))).toBe(false)
    expect(retry(0, new TypeError('Failed to fetch'))).toBe(true)
    expect(retry(3, new TypeError('Failed to fetch'))).toBe(false)
  })
})

describe('count', () => {
  it('uses the singular for one', () => {
    expect(count(1, 'book', 'books')).toBe('1 book')
    expect(count(2, 'book', 'books')).toBe('2 books')
  })
})
