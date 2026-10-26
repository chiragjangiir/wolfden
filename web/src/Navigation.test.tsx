// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import type { EntityPageResult, LinkView } from './api'
import { YEAR_PATH, atOf, entities, page, renderApp, stubApi } from './test/harness'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const byKey = (key: string) => entities.find((e) => e.key === key)!

async function main() {
  return screen.findByRole('main')
}

describe('SC-WEB-12: the peek closes when the list changes', () => {
  it('on the chapter page, moving to another chapter starts with its peek closed', async () => {
    stubApi()
    renderApp('/u/dracula/b/dracula/c/5?at=dracula:3')
    const page = await main()

    fireEvent.click(await within(page).findByRole('button', { name: '2 more appear later' }))
    expect(within(page).getByRole('link', { name: 'Lucy Westenra' })).toBeTruthy()

    fireEvent.click(within(page).getByRole('link', { name: /Note ›/ }))
    const peek = await within(page).findByRole('button', { name: '1 more appears later' })
    expect(peek.getAttribute('aria-expanded')).toBe('false')
    expect(within(page).queryByRole('link', { name: 'Quincey Harker' })).toBeNull()
    expect(within(page).queryByRole('link', { name: 'Lucy Westenra' })).toBeNull()
  })

  it('in the tree, a new position starts with the peek closed, even when the tree stays on screen', async () => {
    stubApi()
    // Chapter 4 is visited first, so going back to it later is cached and the tree isn't replaced while it loads.
    const { history } = renderApp('/u/dracula?at=dracula:4')
    const slider = await screen.findByRole('slider', { name: 'Story position' })
    await within(await screen.findByRole('navigation', { name: 'Contents' })).findByRole('button', { name: '3 more appear later' })
    fireEvent.keyDown(slider, { key: 'ArrowLeft' })
    await waitFor(() => expect(atOf(history)).toBe('dracula:3'))
    const contents = await screen.findByRole('navigation', { name: 'Contents' })

    fireEvent.click(await within(contents).findByRole('button', { name: '3 more appear later' }))
    expect(within(contents).getByRole('link', { name: 'Quincey P. Morris' })).toBeTruthy()
    fireEvent.keyDown(slider, { key: 'ArrowRight' })
    await waitFor(() => expect(atOf(history)).toBe('dracula:4'))

    expect(screen.getByRole('navigation', { name: 'Contents' })).toBe(contents)
    const peek = await within(contents).findByRole('button', { name: '3 more appear later' })
    expect(peek.getAttribute('aria-expanded')).toBe('false')
    expect(within(contents).queryByRole('link', { name: 'Quincey P. Morris' })).toBeNull()
  })
})

describe('SC-WEB-16: the chapter page', () => {
  it('names the chapter, lists the notes that enter there, and reads the den as of it', async () => {
    stubApi()
    const { history } = renderApp('/u/dracula/b/dracula/c/5')
    const page = await main()

    expect(await within(page).findByRole('heading', { level: 1, name: 'Letters—Lucy and Mina' })).toBeTruthy()
    expect(within(page).getByText('Chapter 5')).toBeTruthy()
    expect(await within(page).findByRole('link', { name: 'Lucy Westenra' })).toBeTruthy()
    expect(within(page).getByRole('link', { name: 'Quincey P. Morris' })).toBeTruthy()
    expect(within(page).queryByRole('link', { name: 'Jonathan Harker' })).toBeNull()
    expect(document.title).toBe('Letters—Lucy and Mina · Dracula · Wolfden')

    fireEvent.click(within(page).getByRole('button', { name: 'Read the den as of this chapter' }))
    await waitFor(() => expect(atOf(history)).toBe('dracula:5'))
    expect(await within(page).findByText('You are reading as of this chapter')).toBeTruthy()
  })

  it('says when a chapter has no such key', async () => {
    stubApi()
    renderApp('/u/dracula/b/dracula/c/99')

    expect(await within(await main()).findByText("Dracula has no chapter '99'.")).toBeTruthy()
  })
})

describe('SC-WEB-17: a note named by its path', () => {
  it('opens from its URL, slashes and encoded characters and all, and the tree links to it', async () => {
    const { calls } = stubApi()
    const { history } = renderApp(`/u/dracula/e/${YEAR_PATH.split('/').map(encodeURIComponent).join('/')}`)

    expect(await screen.findByRole('heading', { level: 1, name: 'The year #1893' })).toBeTruthy()
    expect(calls).toContain(`/api/entity?${new URLSearchParams({ entity: YEAR_PATH, universe: 'dracula' })}`)

    const contents = await screen.findByRole('navigation', { name: 'Contents' })
    const link = within(contents).getByRole('link', { name: 'The year #1893' })
    expect(link.getAttribute('href')).toBe('/u/dracula/e/universes/dracula/notes/the%20year%20%231893.md')

    fireEvent.click(within(contents).getByRole('link', { name: 'Jonathan Harker' }))
    await screen.findByRole('heading', { level: 1, name: 'Jonathan Harker' })
    fireEvent.click(within(contents).getByRole('link', { name: 'The year #1893' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'The year #1893' })).toBeTruthy()
    expect(history.location.hash).toBe('')
  })
})

describe('SC-WEB-11: each position is cached on its own', () => {
  it('never shows one position’s page at another, and going back to everything shows the cached page', async () => {
    let release: () => void = () => {}
    const held = new Promise<void>((resolve) => (release = resolve))
    const jonathan = (body: string): EntityPageResult => ({
      version: 1,
      view: 'full',
      position: null,
      inView: true,
      note: null,
      entity: page(byKey('E-JONATHAN'), {
        sections: [{ name: 'Overview', level: 2, headingPath: ['Overview'], status: 'canon', pinned: false, from: null, until: null, line: 9, body, bookkeeping: false, links: [] }],
      }),
    })
    const { calls } = stubApi({
      override: async (path, params) => {
        if (path !== '/api/entity') {
          return undefined
        }

        if (params.get('asOf') === 'dracula:1') {
          await held
          return jonathan('Only what chapter 1 shows.')
        }

        return jonathan('Everything the author knows.')
      },
    })
    renderApp('/u/dracula/e/E-JONATHAN')
    const pageEl = await main()
    expect(await within(pageEl).findByText('Everything the author knows.')).toBeTruthy()

    fireEvent.keyDown(screen.getByRole('slider', { name: 'Story position' }), { key: 'Home' })
    expect(await within(pageEl).findByText('Loading the note…')).toBeTruthy()
    expect(within(pageEl).queryByText('Everything the author knows.')).toBeNull()

    await act(async () => release())
    expect(await within(pageEl).findByText('Only what chapter 1 shows.')).toBeTruthy()

    const before = calls.filter((c) => c.startsWith('/api/entity?')).length
    fireEvent.keyDown(screen.getByRole('slider', { name: 'Story position' }), { key: 'End' })
    expect(await within(pageEl).findByText('Everything the author knows.')).toBeTruthy()
    expect(within(pageEl).queryByText('Only what chapter 1 shows.')).toBeNull()
    expect(calls.filter((c) => c.startsWith('/api/entity?')).length).toBeGreaterThanOrEqual(before)
  })
})

describe('SC-WEB-11: a blank position', () => {
  it('is an error with a way back to everything, never everything quietly', async () => {
    const { calls } = stubApi()
    const { history } = renderApp('/u/dracula?at=')

    const alert = await within(await main()).findByRole('alert')
    expect(alert.textContent).toContain("'asOf' is empty.")
    expect(calls).toContain('/api/hierarchy?universe=dracula&asOf=')
    expect(screen.getByRole('slider', { name: 'Story position' }).getAttribute('aria-valuetext')).toBe('an empty position, which isn’t one in this story')

    fireEvent.click(within(alert).getByRole('button', { name: 'Show everything' }))
    await waitFor(() => expect(atOf(history)).toBeNull())
    expect(await screen.findByText(/notes in all/)).toBeTruthy()
  })
})

function link(text: string, extra: Partial<LinkView>): LinkView {
  return {
    text,
    target: 'mina-harker',
    heading: null,
    label: 'Mina',
    line: 1,
    column: 1,
    outcome: 'resolved',
    entity: { key: 'E-MINA', id: 'E-MINA', name: 'Mina Harker', type: 'character' },
    section: null,
    matches: null,
    problem: null,
    inView: null,
    ...extra,
  }
}

function stubBody(body: string, links: LinkView[]) {
  return stubApi({
    entityPage: (): EntityPageResult => ({
      version: 1,
      view: 'full',
      position: null,
      inView: true,
      note: null,
      entity: page(byKey('E-JONATHAN'), {
        sections: [{ name: 'Overview', level: 2, headingPath: ['Overview'], status: 'canon', pinned: false, from: null, until: null, line: 9, body, bookkeeping: false, links }],
      }),
    }),
  })
}

describe('SC-WEB-14: repeated links, code spans, and link look-alikes', () => {
  it('gives each of two links with the same text its own resolution, in order, and leaves code alone', async () => {
    stubBody('[[mina-harker|Mina]] first, then [[mina-harker|Mina]] again, and `[[mina-harker|Mina]]` as code.', [
      link('[[mina-harker|Mina]]', {}),
      link('[[mina-harker|Mina]]', { outcome: 'broken', entity: null, problem: "Nothing is called 'mina-harker' here." }),
    ])
    renderApp('/u/dracula/e/E-JONATHAN')
    const section = (await screen.findByRole('heading', { name: 'Overview' })).closest('section')!

    const anchors = within(section).getAllByRole('link', { name: 'Mina' })
    expect(anchors).toHaveLength(1)
    expect(anchors[0].getAttribute('href')).toBe('/u/dracula/e/E-MINA')
    const spans = within(section).getAllByText('Mina').filter((el) => el.tagName === 'SPAN')
    expect(spans).toHaveLength(1)
    expect(spans[0].className).toContain('wikilink-broken')
    expect(section.querySelector('code')!.textContent).toBe('[[mina-harker|Mina]]')
  })

  it('doesn’t let a link written in a note pass for an in-app link', async () => {
    stubBody('A [look-alike](wolfden-link:0) and [another](wolfden-00:0).', [link('[[mina-harker|Mina]]', {})])
    renderApp('/u/dracula/e/E-JONATHAN')
    const section = (await screen.findByRole('heading', { name: 'Overview' })).closest('section')!

    expect(within(section).queryByRole('link')).toBeNull()
    expect(section.textContent).toContain('A look-alike and another.')
  })
})

describe('Page titles name only what the reader has met', () => {
  it('names a note in view, and not one met later', async () => {
    stubApi()
    renderApp('/u/dracula/e/E-QUINCEY?at=dracula:3')

    await screen.findByText(/Readers first meet/)
    expect(document.title).toBe('Not met yet · Wolfden')
    fireEvent.click(within(await main()).getByRole('button', { name: 'Show everything' }))
    await screen.findByRole('heading', { level: 1, name: 'Quincey P. Morris' })
    await waitFor(() => expect(document.title).toBe('Quincey P. Morris · Wolfden'))
  })
})

describe('The tree on a narrow screen', () => {
  it('takes focus when opened, and Escape closes it and returns focus to the button', async () => {
    stubApi()
    renderApp('/u/dracula')
    const toggle = await screen.findByRole('button', { name: 'Contents' })
    const sidebar = document.getElementById('sidebar')!

    fireEvent.click(toggle)
    expect(sidebar.getAttribute('data-open')).toBe('true')
    expect(document.activeElement).toBe(sidebar)

    fireEvent.keyDown(document, { key: 'Escape' })
    expect(sidebar.getAttribute('data-open')).toBe('false')
    expect(document.activeElement).toBe(toggle)
    expect(toggle.getAttribute('aria-expanded')).toBe('false')
  })
})
