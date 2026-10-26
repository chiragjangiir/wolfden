// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import type { HierarchyResult, SearchHit, SearchResult } from './api'
import { entities, hierarchy, renderApp, stubApi } from './test/harness'
import { snippetParts, type TextMatch } from './snippet'
import { Snippet } from './pages/SearchPage'
import { SEARCH_DEBOUNCE_MS, isTextEntry } from './components/SearchBox'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const byKey = (key: string) => entities.find((e) => e.key === key)!

/** The plain text and match ranges of a test snippet written with brackets around its matches. */
function unbracket(snippet: string): Pick<SearchHit, 'text' | 'matches'> {
  let text = ''
  const matches: TextMatch[] = []
  for (const part of snippet.split(/(\[[^[\]]+\])/)) {
    if (/^\[[^[\]]+\]$/.test(part)) {
      matches.push({ start: text.length, length: part.length - 2 })
      text += part.slice(1, -1)
    } else {
      text += part
    }
  }

  return { text, matches }
}

function hit(key: string, section: string | null, snippet: string, extra: Partial<SearchHit> = {}): SearchHit {
  const entity = byKey(key)
  return { entity, section, line: section ? 10 : null, snippet, ...unbracket(snippet), status: entity.status, inCanon: true, inComment: false, ...extra }
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

      if (params.get('asOf') === '') {
        // The harness answers a blank asOf as the API does: bad_argument.
        searches.push(params)
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

describe('SC-WEB-18: a search waiting on a pause in typing', () => {
  afterEach(() => vi.useRealTimers())

  it('is dropped by Enter, so a filter changed after it stays', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    stubSearch()
    const { history } = renderApp('/u/dracula')
    await screen.findByRole('navigation', { name: 'Contents' })

    fireEvent.change(searchBox(), { target: { value: 'blood' } })
    fireEvent.submit(searchBox().closest('form')!)
    const filters = await screen.findByRole('group', { name: 'Filters' })
    fireEvent.click(within(filters).getByRole('checkbox', { name: 'Canon text only' }))
    await waitFor(() => expect(searchOf(history).get('canon')).toBe('true'))

    await act(() => vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS * 3))
    expect(history.location.pathname).toBe('/u/dracula/search')
    expect(searchOf(history).toString()).toBe('q=blood&canon=true')
  })

  it('is dropped by going to another page, rather than pulling the writer back to search', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    stubSearch()
    const { history } = renderApp('/u/dracula')
    const contents = await screen.findByRole('navigation', { name: 'Contents' })

    fireEvent.change(searchBox(), { target: { value: 'bl' } })
    fireEvent.click(await within(contents).findByRole('link', { name: 'Mina Harker' }))
    await waitFor(() => expect(history.location.pathname).toBe('/u/dracula/e/E-MINA'))

    await act(() => vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS * 3))
    expect(history.location.pathname).toBe('/u/dracula/e/E-MINA')
  })

  it('is dropped by opening a hit from the search page', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    stubSearch()
    const { history } = renderApp('/u/dracula/search?q=blood')
    const main = await screen.findByRole('main')
    await within(main).findByText('3 matches, in 2 notes.')
    await waitFor(() => expect(searchBox().value).toBe('blood'))

    fireEvent.change(searchBox(), { target: { value: 'blood x' } })
    fireEvent.click(within(main).getByRole('link', { name: 'Illness' }))
    await waitFor(() => expect(history.location.pathname).toBe('/u/dracula/e/E-LUCY'))

    await act(() => vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS * 3))
    expect(history.location.pathname).toBe('/u/dracula/e/E-LUCY')
    expect(searchOf(history).has('q')).toBe(false)
  })

  it('runs at the new position when the position moves before typing pauses', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const { searches } = stubSearch()
    const { history } = renderApp('/u/dracula')
    await screen.findByRole('navigation', { name: 'Contents' })

    fireEvent.change(searchBox(), { target: { value: 'blood' } })
    fireEvent.change(await screen.findByRole('combobox', { name: 'Book' }), { target: { value: 'dracula' } })
    await waitFor(() => expect(searchOf(history).get('at')).toBe('dracula:1'))
    expect(history.location.pathname).toBe('/u/dracula')

    await act(() => vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS * 3))
    await waitFor(() => expect(history.location.pathname).toBe('/u/dracula/search'))
    expect([searchOf(history).get('at'), searchOf(history).get('q')]).toEqual(['dracula:1', 'blood'])
    await waitFor(() => expect(searches.at(-1)?.get('asOf')).toBe('dracula:1'))
  })

  it('runs with the filters as they are when it fires, and the box empties on leaving the page', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    stubSearch()
    const { history } = renderApp('/u/dracula/search?q=blood')
    const filters = await screen.findByRole('group', { name: 'Filters' })
    await waitFor(() => expect(searchBox().value).toBe('blood'))

    fireEvent.change(searchBox(), { target: { value: 'blood scar' } })
    fireEvent.change(within(filters).getByRole('combobox', { name: 'Status' }), { target: { value: 'canon' } })
    await act(() => vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS * 3))
    await waitFor(() => expect(searchOf(history).toString()).toBe('q=blood+scar&status=canon'))

    const contents = screen.getByRole('navigation', { name: 'Contents' })
    fireEvent.click(await within(contents).findByRole('link', { name: 'Mina Harker' }))
    await waitFor(() => expect(history.location.pathname).not.toBe('/u/dracula/search'))
    await waitFor(() => expect(searchBox().value).toBe(''))
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

  it('moves focus to the end of the list when "Show more" has nothing more to show', async () => {
    stubSearch((params) => {
      const limit = Number(params.get('limit'))
      const n = Math.min(limit, 30)
      const hits = Array.from({ length: n }, (_, i) => hit('E-LUCY', `Part ${i + 1}`, `[blood] ${i + 1}`))
      return { version: 1, query: 'blood', count: n, truncated: limit < 30, hits }
    })
    renderApp('/u/dracula/search?q=blood')
    const main = await screen.findByRole('main')

    await within(main).findByText('The first 20 matches, in 1 note.')
    const button = within(main).getByRole('button', { name: 'Show more matches' })
    button.focus()
    fireEvent.click(button)
    const end = await within(main).findByText("That's every match.")
    await waitFor(() => expect(document.activeElement).toBe(end))
    expect(within(main).queryByRole('button', { name: 'Show more matches' })).toBeNull()
  })

  it.each([
    ['canon=false', false, (p: URLSearchParams) => expect(p.has('canonOnly')).toBe(false)],
    ['series=x&book=dracula', true, (p: URLSearchParams) => expect([p.get('book'), p.has('series')]).toEqual(['dracula', false])],
    ['type=&status=%20', false, (p: URLSearchParams) => expect([p.has('type'), p.has('status')]).toEqual([false, false])],
  ])('drops a filter the page doesn’t take as written: %s', async (query, filtered, check) => {
    const { searches } = stubSearch((params) => ({ version: 1, query: params.get('query')!, count: 0, truncated: false, hits: [] }))
    renderApp(`/u/dracula/search?q=scar&${query}`)
    // A blank or dropped filter isn't counted as one in effect.
    const words = 'No matches for “scar”.' + (filtered ? ' Try without some of the filters.' : '')
    expect(await screen.findByText(words)).toBeTruthy()
    expect(searches.length).toBe(1)
    check(searches[0])
    expect((screen.getByRole('checkbox', { name: 'Canon text only' }) as HTMLInputElement).checked).toBe(false)
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

  it.each([
    ['at=', 'blank'],
    ['at=%20%20', 'spaces only'],
  ])('fails closed on a position that is %s (%s), offering to show everything', async (at) => {
    const { searches } = stubSearch()
    const { history } = renderApp(`/u/dracula/search?q=blood&${at}`)
    const main = await screen.findByRole('main')

    const alert = await within(main).findByRole('alert')
    expect(alert.textContent).toContain("'asOf' is empty")
    expect(searches.every((p) => p.get('asOf') === '')).toBe(true)
    expect(within(main).queryByText(/Searching as of/)).toBeNull()
    expect(within(main).queryByRole('link', { name: 'Lucy Westenra' })).toBeNull()

    fireEvent.click(within(alert).getByRole('button', { name: 'Show everything' }))
    await waitFor(() => expect(searchOf(history).has('at')).toBe(false))
    expect(await within(main).findByText('3 matches, in 2 notes.')).toBeTruthy()
  })

  it('fails closed on a blank position before any words are typed', async () => {
    stubSearch()
    renderApp('/u/dracula/search?at=')
    const alert = await within(await screen.findByRole('main')).findByRole('alert')
    expect(alert.textContent).toContain('empty')
    expect(within(alert).getByRole('button', { name: 'Show everything' })).toBeTruthy()
  })

  it('never shows results from the last position while the new one loads', async () => {
    let release: () => void = () => {}
    const held = new Promise<void>((resolve) => (release = resolve))
    const { searches } = stubSearch()
    stubApi({
      override: async (path, params) => {
        if (path !== '/api/search') {
          return undefined
        }

        searches.push(params)
        const asOf = params.get('asOf')
        if (asOf) {
          await held
        }

        const hits = asOf ? [] : bloodHits
        return { version: 1, query: params.get('query')!, count: hits.length, truncated: false, hits } satisfies SearchResult
      },
    })
    renderApp('/u/dracula/search?q=blood')
    const main = await screen.findByRole('main')
    await within(main).findByText('3 matches, in 2 notes.')

    fireEvent.change(await screen.findByRole('combobox', { name: 'Book' }), { target: { value: 'dracula' } })
    await waitFor(() => expect(searches.some((p) => p.get('asOf') === 'dracula:1')).toBe(true))
    expect(within(main).getByText('Loading matches…')).toBeTruthy()
    expect(within(main).queryByRole('link', { name: 'Lucy Westenra' })).toBeNull()
    expect(main.querySelector('mark')).toBeNull()

    release()
    expect(await within(main).findByText('No matches as of Dracula chapter 1.')).toBeTruthy()
  })

  it('lists in the Type filter only the types the reader has met, keeping the chosen one', async () => {
    stubSearch()
    stubApi({
      override: (path, params) => {
        if (path === '/api/hierarchy' && params.get('asOf') === 'dracula:3') {
          const u = hierarchy.universe
          return {
            ...hierarchy,
            position: 'dracula:3',
            universe: {
              ...u,
              entities: { total: 1, byType: { location: 1 } },
              books: u.books.map((b) => ({ ...b, entities: { total: 2, byType: { character: 2 } } })),
            },
          } satisfies HierarchyResult
        }

        if (path === '/api/search') {
          return { version: 1, query: 'blood', count: 0, truncated: false, hits: [] } satisfies SearchResult
        }

        return undefined
      },
    })
    renderApp('/u/dracula/search?q=blood&at=dracula:3&type=event')
    const type = (await screen.findByRole('combobox', { name: 'Type' })) as HTMLSelectElement
    await waitFor(() => expect([...type.options].map((o) => o.value)).toEqual(['', 'character', 'location', 'event']))
    expect(type.value).toBe('event')
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
  it('renders the snippet as text, marking only the ranges the API gives', () => {
    // The API marks each matching word, inside a link's target too, by range: the note's own brackets are just text.
    const text = '<img src=x onerror="alert(1)"> the blood and [[mina-harker|Mina]] <script>alert(2)</script> [blood]'
    const at = (word: string, from = 0) => ({ start: text.indexOf(word, from), length: word.length })
    const { container } = render(
      <p>
        <Snippet text={text} matches={[at('blood'), at('mina'), at('Mina')]} />
      </p>,
    )

    expect(container.querySelector('img')).toBeNull()
    expect(container.querySelector('script')).toBeNull()
    expect([...container.querySelectorAll('mark')].map((m) => m.textContent)).toEqual(['blood', 'mina', 'Mina'])
    expect(container.textContent).toBe(text)
  })

  it('splits text by its match ranges, whatever their order, clipping any that overlap or run past the end', () => {
    expect(snippetParts('Château and Vampires', [{ start: 12, length: 8 }, { start: 0, length: 7 }])).toEqual([
      { text: 'Château', match: true },
      { text: ' and ', match: false },
      { text: 'Vampires', match: true },
    ])
    expect(snippetParts('[[x]] y', [{ start: 2, length: 1 }, { start: 2, length: 3 }, { start: 6, length: 9 }])).toEqual([
      { text: '[[', match: false },
      { text: 'x', match: true },
      { text: ']]', match: true },
      { text: ' ', match: false },
      { text: 'y', match: true },
    ])
    expect(snippetParts('no marks', [])).toEqual([{ text: 'no marks', match: false }])
  })
})

describe('SC-WEB-24: I search one field', () => {
  const homeHit = hit('E-LUCY', null, '[Hillingham]', { field: 'home', line: 7 })

  it('picks a field from those notes have, keeps it in the URL, and sends it to the API', async () => {
    const { searches } = stubSearch((params) => {
      const hits = params.get('field') === 'home' ? [homeHit] : bloodHits
      return { version: 1, query: params.get('query')!, count: hits.length, truncated: false, hits }
    })
    const { history } = renderApp('/u/dracula/search?q=hillingham')
    const filters = await screen.findByRole('group', { name: 'Filters' })
    const field = within(filters).getByRole('combobox', { name: 'Field' }) as HTMLSelectElement
    await waitFor(() => expect([...field.options].map((o) => o.value)).toEqual(['', 'home', 'occupation']))
    expect(field.value).toBe('')
    await waitFor(() => expect(searches.length).toBe(1))
    expect(searches[0].has('field')).toBe(false)

    fireEvent.change(field, { target: { value: 'home' } })
    await waitFor(() => expect(searchOf(history).toString()).toBe('q=hillingham&field=home'))
    await waitFor(() => expect(searches.at(-1)!.get('field')).toBe('home'))

    // The hit says which field matched, and links to the note.
    const main = screen.getByRole('main')
    const place = await within(main).findByRole('link', { name: 'Field: home' })
    expect(place.getAttribute('href')).toBe('/u/dracula/e/E-LUCY')
    expect([...main.querySelectorAll('mark')].map((m) => m.textContent)).toEqual(['Hillingham'])
  })
})

describe('SC-WEB-25: I search a field as of a story position', () => {
  it('lists only the fields the reader has met, keeping the chosen one', async () => {
    const { calls } = stubSearch((params) => ({ version: 1, query: params.get('query')!, count: 0, truncated: false, hits: [] }))
    renderApp('/u/dracula/search?q=hillingham&at=dracula:3&field=home')
    const field = (await screen.findByRole('combobox', { name: 'Field' })) as HTMLSelectElement

    await waitFor(() => expect([...field.options].map((o) => o.value)).toEqual(['', 'occupation', 'home']))
    expect(field.value).toBe('home')
    expect(calls.filter((c) => c.startsWith('/api/fields'))).toEqual(['/api/fields?universe=dracula&asOf=dracula%3A3'])
    expect(await screen.findByText('No matches as of Dracula chapter 3. Try without some of the filters.')).toBeTruthy()
  })
})
