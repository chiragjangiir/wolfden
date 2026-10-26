// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import type { EntityPageResult, LinkView, PageSection } from './api'
import { atOf, entities, page, renderApp, stubApi } from './test/harness'
import { splitByView } from './story'
import { positions } from './test/harness'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const byKey = (key: string) => entities.find((e) => e.key === key)!

async function tree() {
  return screen.findByRole('navigation', { name: 'Contents' })
}

describe('SC-WEB-12: the tree shows what the reader has met, with a peek at the rest', () => {
  it('shows every note at no position, with no peek', async () => {
    stubApi()
    renderApp('/u/dracula')
    const contents = await tree()

    expect(within(contents).getByRole('link', { name: 'Quincey P. Morris' })).toBeTruthy()
    expect(within(contents).getByRole('link', { name: 'A scene at the inn' })).toBeTruthy()
    expect(within(contents).queryByRole('button', { name: /more/ })).toBeNull()
  })

  it('as of a chapter, hides notes met later behind "N more appear later", greyed with where they enter', async () => {
    stubApi()
    renderApp('/u/dracula?at=dracula:3')
    const contents = await tree()
    await within(contents).findByRole('link', { name: 'Jonathan Harker' })

    expect(within(contents).getByRole('link', { name: 'Mina Harker' })).toBeTruthy()
    expect(within(contents).queryByRole('link', { name: 'Quincey P. Morris' })).toBeNull()
    expect(within(contents).queryByRole('link', { name: 'Lucy Westenra' })).toBeNull()

    const peek = within(contents).getByRole('button', { name: '2 more appear later' })
    expect(peek.getAttribute('aria-expanded')).toBe('false')
    fireEvent.click(peek)

    const quincey = within(contents).getByRole('link', { name: 'Quincey P. Morris' })
    const row = quincey.closest('li')!
    expect(row.className).toBe('later')
    expect(row.textContent).toContain('enters at Dracula chapter 5')
    expect(within(contents).getByRole('button', { name: 'Hide the 2 not met yet' }).getAttribute('aria-expanded')).toBe('true')
  })

  it('says a note out of canon for its status is not in view, rather than later', async () => {
    stubApi()
    renderApp('/u/dracula?at=dracula:3')
    const contents = await tree()

    fireEvent.click(await within(contents).findByRole('button', { name: '1 more not in view here' }))
    expect(within(contents).getByRole('link', { name: 'A scene at the inn' }).closest('li')!.textContent).toContain('not in canon: idea')
  })

  it('compares the unfiltered list with the list as of the position', () => {
    const inView = new Set(['E-CASTLE', 'E-JONATHAN'])
    const { shown, hidden } = splitByView([byKey('E-CASTLE'), byKey('E-JONATHAN'), byKey('E-LUCY')], inView, 'dracula:2', positions)

    expect(shown.map((e) => e.key)).toEqual(['E-CASTLE', 'E-JONATHAN'])
    expect(hidden).toEqual([{ entity: byKey('E-LUCY'), entry: 'dracula:5', entersAt: 'Dracula chapter 5', later: true }])
    expect(splitByView([byKey('E-LUCY')], null, undefined, positions)).toEqual({ shown: [byKey('E-LUCY')], hidden: [] })
  })
})

describe('SC-WEB-13: opening a note the reader hasn’t met', () => {
  it('shows a banner saying where readers first meet it, and nothing else of it', async () => {
    const { calls } = stubApi()
    const { history } = renderApp('/u/dracula/e/E-QUINCEY?at=dracula:3')

    const banner = await within(await screen.findByRole('main')).findByText(/Readers first meet/)
    expect(banner.textContent).toBe('Readers first meet Quincey P. Morris in Dracula chapter 5.')
    expect(screen.queryByRole('heading', { name: 'Linked from' })).toBeNull()
    expect(screen.queryByRole('heading', { level: 1 })).toBeNull()
    expect(calls).toContain('/api/entity?entity=E-QUINCEY&universe=dracula&asOf=dracula%3A3')

    fireEvent.click(screen.getByRole('button', { name: 'Go to Dracula chapter 5' }))
    await waitFor(() => expect(atOf(history)).toBe('dracula:5'))
    expect(await screen.findByRole('heading', { level: 1, name: 'Quincey P. Morris' })).toBeTruthy()
  })

  it('shows the page itself once the reader has met it', async () => {
    stubApi()
    renderApp('/u/dracula/e/E-QUINCEY?at=dracula:5')

    expect(await screen.findByRole('heading', { level: 1, name: 'Quincey P. Morris' })).toBeTruthy()
    expect(screen.getByText('Enters at Dracula chapter 5')).toBeTruthy()
    expect(screen.queryByText(/Readers first meet/)).toBeNull()
  })
})

function link(text: string, extra: Partial<LinkView>): LinkView {
  return {
    text,
    target: text.slice(2, -2).split('|')[0],
    heading: null,
    label: text.includes('|') ? text.slice(2, -2).split('|')[1] : null,
    line: 1,
    column: 1,
    outcome: 'resolved',
    entity: null,
    section: null,
    matches: null,
    problem: null,
    inView: null,
    ...extra,
  }
}

function section(name: string, body: string, links: LinkView[] = []): PageSection {
  return { name, level: 2, headingPath: [name], status: 'canon', pinned: false, from: null, until: null, line: 10, body, bookkeeping: false, links }
}

function stubPage(extra: Parameters<typeof page>[1]) {
  return stubApi({
    entityPage: (key): EntityPageResult => ({
      version: 1,
      view: 'full',
      position: null,
      inView: true,
      note: null,
      entity: page(byKey(key), extra),
    }),
  })
}

describe('SC-WEB-14: links on a note’s page', () => {
  const mina = { key: 'E-MINA', id: 'E-MINA', name: 'Mina Harker', type: 'character' }
  const lucy = { key: 'E-LUCY', id: 'E-LUCY', name: 'Lucy Westenra', type: 'character' }
  const links = [
    link('[[mina-harker|Mina]]', { entity: mina }),
    link('[[harker]]', { outcome: 'ambiguous', matches: [mina, { ...mina, key: 'E-JONATHAN', id: 'E-JONATHAN', name: 'Jonathan Harker' }] }),
    link('[[the-demeter]]', { outcome: 'broken', problem: "Nothing is called 'the-demeter'." }),
    link('[[lucy-westenra]]', { entity: lucy }),
  ]

  it('renders resolved links as in-app links, broken ones as broken, and ambiguous ones with their matches', async () => {
    stubPage({
      sections: [section('Overview', 'He marries [[mina-harker|Mina]]. See [[harker]] and [[the-demeter]], and [[lucy-westenra]].', links)],
    })
    renderApp('/u/dracula/e/E-JONATHAN')
    const main = await screen.findByRole('main')
    await within(main).findByRole('heading', { name: 'Overview' })

    const resolved = within(main).getByRole('link', { name: 'Mina' })
    expect(resolved.getAttribute('href')).toBe('/u/dracula/e/E-MINA')
    expect(within(main).getByRole('link', { name: 'Lucy Westenra' }).getAttribute('href')).toBe('/u/dracula/e/E-LUCY')

    const broken = within(main).getByText('the-demeter')
    expect(broken.tagName).toBe('SPAN')
    expect(broken.className).toContain('wikilink-broken')
    expect(broken.getAttribute('title')).toBe("Broken link: Nothing is called 'the-demeter'.")

    const ambiguous = within(main).getByText('harker')
    expect(ambiguous.className).toContain('wikilink-ambiguous')
    expect(ambiguous.getAttribute('title')).toBe('Ambiguous link: it matches Mina Harker (character), Jonathan Harker (character).')
  })

  it('renders links in field values, lists included', async () => {
    stubPage({
      fields: [
        { name: 'spouse', value: '[[mina-harker|Mina]]', kind: 'plain', status: 'canon', pinned: false, from: null, until: null, line: 5, entryIndex: null, live: true, links: [links[0]] },
        { name: 'friends', value: ['[[lucy-westenra]]', '[[the-demeter]]'], kind: 'plain', status: 'canon', pinned: false, from: null, until: null, line: 6, entryIndex: null, live: true, links: [links[3], links[2]] },
      ],
    })
    renderApp('/u/dracula/e/E-JONATHAN')
    const main = await screen.findByRole('main')

    expect((await within(main).findByRole('link', { name: 'Mina' })).getAttribute('href')).toBe('/u/dracula/e/E-MINA')
    expect(within(main).getByRole('link', { name: 'Lucy Westenra' })).toBeTruthy()
    expect(within(main).getByText('the-demeter').className).toContain('wikilink-broken')
  })

  it('as of a position, a link to a note the reader hasn’t met is plain text that doesn’t say what it reaches', async () => {
    stubPage({ sections: [section('Overview', 'Later, [[quincey-morris|a Texan]] arrives.', [link('[[quincey-morris|a Texan]]', { inView: false })])] })
    renderApp('/u/dracula/e/E-JONATHAN')
    const main = await screen.findByRole('main')

    const later = await within(main).findByText('a Texan')
    expect(later.tagName).toBe('SPAN')
    expect(later.className).toContain('wikilink-later')
    expect(later.closest('a')).toBeNull()
  })

  it('groups backlinks by the note they come from', async () => {
    stubPage({
      backlinks: [
        { source: mina, container: null, site: 'entity_section', path: 'm.md', line: 3, column: 1, text: '[[jonathan-harker]]', field: null, section: 'Overview', chapter: null, outlineRow: null, status: 'canon', bookkeeping: false },
        { source: mina, container: null, site: 'entity_field', path: 'm.md', line: 9, column: 1, text: '[[jonathan-harker]]', field: 'spouse', section: null, chapter: null, outlineRow: null, status: 'canon', bookkeeping: false },
        { source: null, container: { kind: 'book', key: 'dracula', name: 'Dracula', id: 'B1', path: 'b' }, site: 'chapter_field', path: 'book.md', line: 40, column: 1, text: '[[jonathan-harker]]', field: 'pov', section: null, chapter: 'dracula:1', outlineRow: null, status: null, bookkeeping: false },
      ],
    })
    renderApp('/u/dracula/e/E-JONATHAN')
    const backlinks = (await screen.findByRole('heading', { name: 'Linked from' })).closest('section')!

    const groups = within(backlinks).getAllByRole('listitem').filter((li) => li.parentElement?.parentElement === backlinks)
    expect(groups).toHaveLength(2)
    expect(groups[0].textContent).toContain('Mina Harker')
    expect(groups[0].textContent).toContain('in “Overview”')
    expect(groups[0].textContent).toContain('in spouse')
    expect(groups[1].textContent).toContain('pov of Dracula chapter 1')
  })
})

describe('SC-WEB-15: a note’s markdown is rendered safely', () => {
  it('never renders or runs HTML or a script link from a note', async () => {
    const win = window as unknown as { pwned?: boolean }
    stubPage({
      lead: '# Jonathan Harker\n\n<script>window.pwned = true</script>\n\nA solicitor <img src="x" onerror="window.pwned = true"> from Exeter.',
      sections: [section('Overview', '<script>window.pwned = true</script>\n\n**Bold** and [a link](javascript:window.pwned=true) and <b onclick="x">tags</b>.')],
    })
    renderApp('/u/dracula/e/E-JONATHAN')
    const main = await screen.findByRole('main')
    await within(main).findByRole('heading', { name: 'Overview' })

    const html = main.innerHTML
    expect(html).not.toContain('<script')
    expect(html).not.toContain('<img')
    expect(html).not.toContain('onerror')
    expect(html).not.toContain('onclick')
    expect(html).not.toContain('javascript:')
    expect(html).toContain('<strong>Bold</strong>')
    expect(within(main).getByText(/A solicitor/).textContent).toContain('from Exeter.')
    expect(win.pwned).toBeUndefined()
  })

  it('doesn’t repeat the lead’s title, which the page shows already', async () => {
    stubPage({ lead: '# Jonathan Harker\n\nA solicitor from Exeter.' })
    renderApp('/u/dracula/e/E-JONATHAN')

    expect(await screen.findByText('A solicitor from Exeter.')).toBeTruthy()
    expect(screen.getAllByRole('heading', { name: 'Jonathan Harker' })).toHaveLength(1)
  })
})
