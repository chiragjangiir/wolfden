import { describe, expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App } from './App'
import { count, denQuery, type DenSummary } from './api'

function render(den: DenSummary): string {
  const client = new QueryClient()
  client.setQueryData(denQuery.queryKey, den)
  return renderToStaticMarkup(
    <QueryClientProvider client={client}>
      <App />
    </QueryClientProvider>,
  )
}

describe('App', () => {
  it('shows the den name and its universes', () => {
    const html = render({
      version: 1,
      name: 'Dracula',
      pinnedUniverse: null,
      universes: [{ key: 'dracula', name: 'Dracula', id: null, series: 0, books: 1, entities: 50 }],
    })

    expect(html).toContain('<h1>Dracula</h1>')
    expect(html).toContain('<code>dracula</code>: 0 series, 1 book, 50 notes')
  })

  it('says when the den has no universes', () => {
    expect(render({ version: 1, name: 'Empty', pinnedUniverse: null, universes: [] })).toContain('no universes yet')
  })
})

describe('count', () => {
  it('uses the singular for one', () => {
    expect(count(1, 'book', 'books')).toBe('1 book')
    expect(count(2, 'book', 'books')).toBe('2 books')
  })
})
