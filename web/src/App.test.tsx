// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, screen, waitFor, within } from '@testing-library/react'
import { ApiRequestError, count, createQueryClient, getJson } from './api'
import { den, renderApp, stubApi } from './test/harness'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('App', () => {
  it("goes straight to the overview of a den's only universe", async () => {
    stubApi()
    const { history } = renderApp('/')

    expect(await screen.findByRole('heading', { level: 1, name: 'Dracula' })).toBeTruthy()
    await waitFor(() => expect(history.location.pathname).toBe('/u/dracula'))
    expect(screen.getByText(/7 notes in all/)).toBeTruthy()
  })

  it('lists the universes of a den with several, with a switcher', async () => {
    stubApi({
      den: {
        ...den,
        name: 'Gothic',
        universes: [...den.universes, { key: 'carmilla', name: 'Carmilla', id: null, series: 0, books: 1, entities: 1 }],
      },
    })
    renderApp('/')
    const main = await screen.findByRole('main')

    expect(await within(main).findByRole('heading', { name: 'Gothic' })).toBeTruthy()
    expect(main.textContent).toContain('carmilla: 0 series, 1 book, 1 note')
    expect(screen.getByRole('combobox', { name: 'Universe' })).toBeTruthy()
  })

  it('says when the den has no universes', async () => {
    stubApi({ den: { ...den, universes: [] } })
    renderApp('/')

    expect(await screen.findByText('This den has no universes yet.')).toBeTruthy()
  })

  it('says it is loading before the den arrives', async () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})))
    renderApp('/')

    expect(await screen.findByText('Loading the den…')).toBeTruthy()
  })

  it("shows the API's message when the den can't be read", async () => {
    const message = "den.yaml doesn't load, so nothing in the den can be read. Run 'wolfden check' to see why."
    stubApi({ override: () => new Response(JSON.stringify({ code: 'den_not_loaded', message }), { status: 503 }) })
    renderApp('/')

    const alert = await within(await screen.findByRole('main')).findByRole('alert')
    expect(alert.textContent).toBe(message)
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
