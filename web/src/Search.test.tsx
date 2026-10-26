// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import type { SearchHit, SearchResult } from './api'
import { entities, renderApp, stubApi } from './test/harness'
import { snippetParts } from './snippet'
import { Snippet } from './pages/SearchPage'
import { isTextEntry } from './components/SearchBox'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const byKey = (key: string) => entities.find((e) => e.key === key)!

function hit(key: string, section: string | null, snippet: string, extra: Partial<SearchHit> = {}): SearchHit {
  const entity = byKey(key)
  return { entity, section, line: section ? 10 : null, snippet, status: entity.status, inCanon: true, inComment: false, ...extra }
}

const bloodHits: SearchHit[] = [
  hit('E-LUCY', 'Illness', '…she had lost a great deal of [blood] in the night…'),
  hit('E-LUCY', null, 'Lucy Westenra, whose [blood] was given…'),
  hit('E-JONATHAN', 'At the castle', '…the Count licked the [blood] from the knife…'),
]

/** Stubs the API, answering /api/search with `answer` (by default, the "blood" hits, or none as of a position). */
function stubSearch(answer?: (params: URLSearchParams) => SearchResult | Response) {
  const searches: URLSearchParams[] = []
  const stub = stubApi({
    override: (path, params) => {
      if (path !== '/api/search') {
        return undefined
      }

      searches.push(params)
      if (answer) {
        return answer(params)
      }

      const hits = params.get('asOf') ? [] : bloodHits
      return { version: 1, query: params.get('query')!, count: hits.length, truncated: false, hits } satisfies SearchResult
    },
  })
  return { ...stub, searches }
}

const searchBox = () => screen.getByRole('searchbox', { name: 'Search notes' }) as HTMLInputElement
const searchOf = (history: { location: { search: string } }) => new URLSearchParams(history.location.search)

describe('SC-WEB-18: I search from the top bar', () => {
  it('searches once typing pauses, keeping the position, and shows the words in the URL', async () => {
    const { searches } = stubSearch()
    const { history } = renderApp('/u/dracula?at=dracula:3')
    await screen.findByRole('navigation', { name: 'Contents' })

    fireEvent.change(searchBox(), { target: { value: 'bl' } })
    fireEvent.change(searchBox(), { target: { value: 'blood' } })
    await waitFor(() => expect(history.location.pathname).toBe('/u/dracula/search'))
    expect(searchOf(history).get('q')).toBe('blood')
    expect(searchOf(history).get('at')).toBe('dracula:3')
    // Only the words typing paused on were searched for.
    await waitFor(() => expect(searches.length).toBeGreaterThan(0))
    expect(searches.map((p) => p.get('query'))).toEqual(['blood'])
  })

  it('searches at once on Enter, and reads the words back from the URL', async () => {
    stubSearch()
    const { history } = renderApp('/u/dracula')
    await screen.findByRole('navigation', { name: 'Contents' })

    fireEvent.change(searchBox(), { target: { value: 'blood' } })
    fireEvent.submit(searchBox().closest('form')!)
    expect(history.location.pathname).toBe('/u/dracula/search')
    expect(await screen.findByRole('heading', { level: 1, name: 'Matches for “blood”' })).toBeTruthy()

    cleanup()
    stubSearch()
    renderApp('/u/dracula/search?q=Renfield')
    await waitFor(() => expect(searchBox().value).toBe('Renfield'))
  })

  it('focuses the box on "/" outside a text box, and leaves "/" typed in one alone', async () => {
    stubSearch()
    renderApp('/u/dracula')
    await screen.findByRole('navigation', { name: 'Contents' })

    const slash = fireEvent.keyDown(document.body, { key: '/' })
    expect(slash).toBe(false) // default prevented, so no "/" lands in the box
    expect(document.activeElement).toBe(searchBox())

    const other = document.createElement('textarea')
    document.body.append(other)
    other.focus()
    expect(fireEvent.keyDown(other, { key: '/' })).toBe(true)
    expect(document.activeElement).toBe(other)
    other.remove()

    // The position slider isn't a text box, so "/" works from it too.
    const slider = screen.getByRole('slider', { name: 'Story position' })
    expect(isTextEntry(slider)).toBe(false)
    expect(isTextEntry(searchBox())).toBe(true)
  })
})

describe('SC-WEB-19: search filters and results', () => {
  it('lists hits by note with type, scope, section links, and marked snippets', async () => {
    stubSearch()
    renderApp('/u/dracula/search?q=blood')
    const main = await screen.findByRole('main')
    await within(main).findByText('3 matches, in 2 notes.')

    const lucy = within(main).getByRole('link', { name: 'Lucy Westenra' }).closest('li')!
    expect(lucy.textContent).toContain('character, in Dracula')
    const illness = within(lucy).getByRole('link', { name: 'Illness' })
    expect(illness.getAttribute('href')).toBe('/u/dracula/e/E-LUCY#section-illness')
    expect(within(lucy).getByRole('link', { name: 'Name or opening' }).getAttribute('href')).toBe('/u/dracula/e/E-LUCY')
    const marks = lucy.querySelectorAll('mark')
    expect([...marks].map((m) => m.textContent)).toEqual(['blood', 'blood'])
    expect(lucy.textContent).not.toContain('[blood]')
  })

  it('keeps the filters in the URL and passes them to the API', async () => {
    const { searches } = stubSearch()
    const { history } = renderApp('/u/dracula/search?q=blood&type=character&status=canon&book=dracula&canon=true')
    const filters = await screen.findByRole('group', { name: 'Filters' })
    await waitFor(() => expect(searches.length).toBe(1))

    const sent = searches[0]
    expect([sent.get('query'), sent.get('universe'), sent.get('type'), sent.get('status'), sent.get('book'), sent.get('canonOnly')]).toEqual([
      'blood',
      'dracula',
      'character',
      'canon',
      'dracula',
      'true',
    ])
    expect(sent.has('asOf')).toBe(false)
    expect(sent.has('series')).toBe(false)

    await waitFor(() => expect((within(filters).getByRole('combobox', { name: 'Type' }) as HTMLSelectElement).value).toBe('character'))
    expect((within(filters).getByRole('combobox', { name: 'Scope' }) as HTMLSelectElement).value).toBe('book:dracula')
    expect((within(filters).getByRole('checkbox', { name: 'Canon text only' }) as HTMLInputElement).checked).toBe(true)

    fireEvent.change(within(filters).getByRole('combobox', { name: 'Type' }), { target: { value: 'location' } })
    fireEvent.change(within(filters).getByRole('combobox', { name: 'Status' }), { target: { value: '' } })
    fireEvent.click(within(filters).getByRole('checkbox', { name: 'Canon text only' }))
    fireEvent.change(within(filters).getByRole('combobox', { name: 'Scope' }), { target: { value: '' } })
    await waitFor(() => expect(searchOf(history).toString()).toBe('q=blood&type=location'))
    await waitFor(() => expect(searches.at(-1)!.toString()).toBe('query=blood&universe=dracula&type=location&limit=20'))
  })

  it('shows more when the API says there are more, asking for a bigger page', async () => {
    const { searches } = stubSearch((params) => {
      const limit = Number(params.get('limit'))
      const hits = Array.from({ length: limit }, (_, i) => hit('E-LUCY', `Part ${i + 1}`, `[blood] ${i + 1}`))
      return { version: 1, query: 'blood', count: limit, truncated: limit < 200, hits }
    })
    renderApp('/u/dracula/search?q=blood')
    const main = await screen.findByRole('main')

    expect(await within(main).findByText('The first 20 matches, in 1 note.')).toBeTruthy()
    fireEvent.click(within(main).getByRole('button', { name: 'Show more matches' }))
    expect(await within(main).findByText('The first 50 matches, in 1 note.')).toBeTruthy()
    expect(searches.map((p) => p.get('limit'))).toEqual(['20', '50'])
  })
})

describe('SC-WEB-20: searching as of a story position', () => {
  it('passes asOf, says it searches as of the position, and says there are no matches there', async () => {
    const { searches } = stubSearch()
    const { history } = renderApp('/u/dracula/search?q=blood&at=dracula:3')
    const main = await screen.findByRole('main')

    expect(await within(main).findByText('No matches as of Dracula chapter 3.')).toBeTruthy()
    expect(searches[0].get('asOf')).toBe('dracula:3')
    expect(within(main).getByText(/Searching as of/).textContent).toContain('Dracula chapter 3')

    fireEvent.click(within(main).getByRole('link', { name: 'Search everything' }))
    await waitFor(() => expect(searchOf(history).has('at')).toBe(false))
    expect(searchOf(history).get('q')).toBe('blood')
    expect(await within(main).findByText('3 matches, in 2 notes.')).toBeTruthy()
    expect(searches.at(-1)!.has('asOf')).toBe(false)
    expect(within(main).queryByText(/Searching as of/)).toBeNull()
  })

  it('never sends an empty asOf', async () => {
    const { searches } = stubSearch()
    renderApp('/u/dracula/search?q=blood&at=')
    await within(await screen.findByRole('main')).findByText('3 matches, in 2 notes.')
    expect(searches.every((p) => !p.has('asOf'))).toBe(true)
  })

  it('says when nothing matches with no position, and suggests dropping filters', async () => {
    stubSearch((params) => ({ version: 1, query: params.get('query')!, count: 0, truncated: false, hits: [] }))
    renderApp('/u/dracula/search?q=scar&type=location')
    expect(await screen.findByText('No matches for “scar”. Try without some of the filters.')).toBeTruthy()
  })

  it("shows the API's error", async () => {
    stubSearch(() => new Response(JSON.stringify({ code: 'unknown_type', message: "'dragon' isn't a type in this den." }), { status: 400 }))
    renderApp('/u/dracula/search?q=blood&type=dragon')
    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain("'dragon' isn't a type in this den.")
  })
})

describe('SC-WEB-21: a snippet holds HTML or brackets of its own', () => {
  it('renders the snippet as text, marking only the matches', () => {
    const snippet = '<img src=x onerror="alert(1)"> the [blood] and [[mina-harker|[Mina]]] <script>alert(2)</script> [note]'
    const { container } = render(
      <p>
        <Snippet text={snippet} q="blood mina" />
      </p>,
    )

    expect(container.querySelector('img')).toBeNull()
    expect(container.querySelector('script')).toBeNull()
    expect([...container.querySelectorAll('mark')].map((m) => m.textContent)).toEqual(['blood', 'Mina'])
    expect(container.textContent).toBe('<img src=x onerror="alert(1)"> the blood and [[mina-harker|Mina]] <script>alert(2)</script> [note]')
  })

  it('matches words ignoring case and accents, and a word* as a prefix', () => {
    expect(snippetParts('[Château] and [Vampires]', 'chateau vamp*')).toEqual([
      { text: 'Château', match: true },
      { text: ' and ', match: false },
      { text: 'Vampires', match: true },
    ])
    expect(snippetParts('see [1] and [ blood ]', 'blood')).toEqual([
      { text: 'see [1] and ', match: false },
      { text: ' blood ', match: true },
    ])
  })
})
