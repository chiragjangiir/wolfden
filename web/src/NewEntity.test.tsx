// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import type { EntityPageResult, EntitySummary } from './api'
import type { WriteResult } from './editApi'
import { entities, hierarchy, page, renderApp, stubApi } from './test/harness'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const NEW_KEY = '01M61E94MCCVS4AW3XFZ8SRY5P'
const NEW_PATH = 'universes/dracula/books/dracula/entities/characters/sister-agatha.md'

const created: EntitySummary = {
  key: NEW_KEY,
  id: NEW_KEY,
  name: 'Sister Agatha',
  type: 'character',
  status: 'draft',
  universe: 'dracula',
  scopeKind: 'book',
  scopeKey: 'dracula',
  path: NEW_PATH,
  from: null,
}

function result(extra: Partial<WriteResult> = {}): WriteResult {
  return {
    version: 1,
    changed: true,
    path: NEW_PATH,
    hash: 'h-new',
    entity: created,
    effects: [`Created ${NEW_PATH} from the 'character' template, with id ${NEW_KEY}.`],
    pinnedCanon: [],
    warnings: [],
    files: [{ path: NEW_PATH, hash: 'h-new' }],
    idAdded: null,
    ...extra,
  }
}

/**
 * Answers POST /api/entity/create with `post`, and the new note's page; records each create's body, and every
 * other call, so a test can see what was asked again.
 */
function stubCreate(post: (body: Record<string, unknown>) => Response | WriteResult = () => result(), withHierarchy = hierarchy) {
  const posted: Record<string, unknown>[] = []
  const api = stubApi({
    override: (path, params, init) => {
      if (path === '/api/entity/create') {
        expect(init?.method).toBe('POST')
        expect(new Headers(init?.headers).get('Content-Type')).toBe('application/json')
        const body = JSON.parse(String(init?.body)) as Record<string, unknown>
        posted.push(body)
        return post(body)
      }

      if (path === '/api/hierarchy') {
        return { ...withHierarchy, position: params.get('asOf') }
      }

      if (path === '/api/entity' && params.get('entity') === NEW_KEY) {
        return { version: 1, view: 'full', position: null, inView: true, note: null, entity: page(created) } satisfies EntityPageResult
      }

      return undefined
    },
  })
  return { ...api, posted }
}

function dialog(): HTMLElement {
  return screen.getByRole('dialog')
}

async function openFromMenu(type: string) {
  const button = await screen.findByRole('button', { name: 'New' })
  fireEvent.keyDown(button, { key: 'ArrowDown' })
  const menu = await screen.findByRole('menu', { name: 'New note' })
  await waitFor(() => expect(within(menu).getByRole('menuitem', { name: 'Character' })).toBeTruthy())
  fireEvent.click(within(menu).getByRole('menuitem', { name: type }))
  return dialog()
}

describe('SC-WEB-67: I make a new note from its type', () => {
  it('the top bar menu lists the types, and the form makes the note where I am and opens it', async () => {
    const { posted, calls } = stubCreate()
    const { history } = renderApp('/u/dracula/b/dracula')
    await screen.findByRole('heading', { name: 'Dracula', level: 1 })

    const form = await openFromMenu('Character')

    // The form: the type chosen, the name to give, and the place, the book I'm on.
    expect(within(form).getByRole('heading', { name: 'New character' })).toBeTruthy()
    expect((within(form).getByLabelText('Type') as HTMLSelectElement).value).toBe('character')
    const name = within(form).getByLabelText('Name') as HTMLInputElement
    await waitFor(() => expect(document.activeElement).toBe(name))
    await waitFor(() => expect((within(form).getByLabelText('Where it goes') as HTMLSelectElement).value).toBe('book:dracula'))
    expect(within(form).getByText("It's canon from the start of Dracula.")).toBeTruthy()
    // No position, so no "Enters the story at".
    expect(within(form).queryByRole('checkbox')).toBeNull()
    // The file isn't shown before it's made.
    expect(form.textContent).not.toContain('.md')

    // A name is required: nothing is sent without one.
    fireEvent.click(within(form).getByRole('button', { name: 'Create' }))
    expect(within(form).getByRole('alert').textContent).toContain('Give the note a name')
    expect(name.getAttribute('aria-invalid')).toBe('true')
    expect(posted).toEqual([])

    fireEvent.change(name, { target: { value: '  Sister Agatha ' } })
    const before = calls.filter((c) => c.startsWith('/api/hierarchy')).length
    fireEvent.click(within(form).getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(history.location.pathname).toBe(`/u/dracula/e/${NEW_KEY}`))
    expect(posted).toEqual([{ type: 'character', name: 'Sister Agatha', universe: 'dracula', book: 'dracula' }])
    expect(screen.queryByRole('dialog')).toBeNull()

    // The new page says which file it is, and takes the focus.
    const notice = await waitFor(() => {
      const shown = document.querySelector<HTMLElement>('.created-notice')
      expect(shown).not.toBeNull()
      return shown!
    })
    expect(notice.getAttribute('role')).toBe('status')
    expect(notice.textContent).toContain(`Created ${NEW_PATH} from the Character template.`)
    await waitFor(() => expect(document.activeElement).toBe(notice))
    expect(await screen.findByRole('heading', { name: 'Sister Agatha', level: 1 })).toBeTruthy()

    // The tree and the counts ask again, so the note shows.
    await waitFor(() => expect(calls.filter((c) => c.startsWith('/api/hierarchy')).length).toBeGreaterThan(before))

    // Leaving the page ends the notice.
    history.push('/u/dracula')
    await waitFor(() => expect(document.querySelector('.created-notice')).toBeNull())
  })

  it('the menu works from the keyboard, and Escape returns to its button', async () => {
    stubCreate()
    renderApp('/u/dracula')
    const button = await screen.findByRole('button', { name: 'New' })
    expect(button.getAttribute('aria-haspopup')).toBe('menu')

    fireEvent.keyDown(button, { key: 'ArrowDown' })
    const menu = await screen.findByRole('menu', { name: 'New note' })
    expect(button.getAttribute('aria-expanded')).toBe('true')
    const items = await within(menu).findAllByRole('menuitem')
    expect(items.map((i) => i.textContent)).toEqual(['Character', 'Event', 'Location', 'Note'])
    await waitFor(() => expect(document.activeElement).toBe(items[0]))
    fireEvent.keyDown(menu, { key: 'ArrowDown' })
    expect(document.activeElement).toBe(items[1])
    fireEvent.keyDown(menu, { key: 'End' })
    expect(document.activeElement).toBe(items[3])
    fireEvent.keyDown(menu, { key: 'ArrowDown' })
    expect(document.activeElement).toBe(items[0])
    fireEvent.keyDown(menu, { key: 'Escape' })
    expect(screen.queryByRole('menu')).toBeNull()
    expect(document.activeElement).toBe(button)

    // Enter on a type opens the form for it, in the universe (I'm on its page).
    fireEvent.keyDown(button, { key: 'ArrowUp' })
    const last = (await within(await screen.findByRole('menu')).findAllByRole('menuitem'))[3]
    await waitFor(() => expect(document.activeElement).toBe(last))
    fireEvent.keyDown(last, { key: 'Enter' })
    const form = dialog()
    expect((within(form).getByLabelText('Type') as HTMLSelectElement).value).toBe('note')
    await waitFor(() => expect((within(form).getByLabelText('Where it goes') as HTMLSelectElement).value).toBe('universe:dracula'))
    expect(within(form).getByText("It's canon from the start of the story.")).toBeTruthy()

    // Escape closes the form, sends nothing, and returns to the menu button.
    fireEvent.keyDown(form, { key: 'Escape' })
    expect(screen.queryByRole('dialog')).toBeNull()
    await waitFor(() => expect(document.activeElement).toBe(button))
  })

  it("a type list's New button fills in the type and the place", async () => {
    const { posted } = stubCreate()
    renderApp('/u/dracula')
    const button = await screen.findByRole('button', { name: 'New Character in Dracula' })
    expect(button.textContent).toBe('New Character')

    fireEvent.click(button)
    const form = dialog()
    expect((within(form).getByLabelText('Type') as HTMLSelectElement).value).toBe('character')
    await waitFor(() => expect((within(form).getByLabelText('Where it goes') as HTMLSelectElement).value).toBe('book:dracula'))

    // The writer can still choose another type and place.
    fireEvent.change(within(form).getByLabelText('Type'), { target: { value: 'location' } })
    fireEvent.change(within(form).getByLabelText('Where it goes'), { target: { value: 'universe:dracula' } })
    fireEvent.change(within(form).getByLabelText('Name'), { target: { value: 'Whitby' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Create' }))
    await waitFor(() => expect(posted).toEqual([{ type: 'location', name: 'Whitby', universe: 'dracula' }]))
  })

  it('on a note’s page, the place is where that note is filed', async () => {
    stubCreate()
    renderApp(`/u/dracula/e/${entities[1].key}`)
    await screen.findByRole('heading', { name: 'Dates and the year', level: 1 })

    const form = await openFromMenu('Event')

    await waitFor(() => expect((within(form).getByLabelText('Where it goes') as HTMLSelectElement).value).toBe('universe:dracula'))
    fireEvent.click(within(form).getByRole('button', { name: 'Cancel' }))
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('at a story position, it offers "Enters the story at", off by default', async () => {
    const { posted } = stubCreate()
    renderApp('/u/dracula/b/dracula?at=dracula:3')
    await screen.findByRole('heading', { name: 'Dracula', level: 1 })

    const form = await openFromMenu('Character')
    const check = (await within(form).findByRole('checkbox', { name: 'Enters the story at Dracula chapter 3' })) as HTMLInputElement
    expect(check.checked).toBe(false)
    fireEvent.change(within(form).getByLabelText('Name'), { target: { value: 'Sister Agatha' } })

    fireEvent.click(check)
    fireEvent.click(within(form).getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(posted).toEqual([{ type: 'character', name: 'Sister Agatha', universe: 'dracula', book: 'dracula', from: 'dracula:3' }]))
  })

  it('offers the position only where the chosen place can see it, and clears it when it can’t', async () => {
    const twoBooks = {
      ...hierarchy,
      universe: {
        ...hierarchy.universe,
        books: [
          ...hierarchy.universe.books,
          { ...hierarchy.universe.books[0], key: 'guest', name: "Dracula's Guest", id: 'B2', path: 'universes/dracula/books/guest', chapters: [] },
        ],
      },
    }
    const { posted } = stubCreate(() => result(), twoBooks)
    renderApp('/u/dracula/b/dracula?at=dracula:3')
    await screen.findByRole('heading', { name: 'Dracula', level: 1 })

    const form = await openFromMenu('Character')
    fireEvent.click(await within(form).findByRole('checkbox', { name: 'Enters the story at Dracula chapter 3' }))

    // Another book can't see Dracula chapter 3: the box goes, and so does the tick.
    const where = within(form).getByLabelText('Where it goes')
    fireEvent.change(where, { target: { value: 'book:guest' } })
    expect(within(form).queryByRole('checkbox')).toBeNull()
    expect(within(form).getByText("It's canon from the start of Dracula's Guest.")).toBeTruthy()

    // The universe can: the box is back, unticked.
    fireEvent.change(where, { target: { value: 'universe:dracula' } })
    expect((within(form).getByRole('checkbox') as HTMLInputElement).checked).toBe(false)

    fireEvent.change(where, { target: { value: 'book:guest' } })
    fireEvent.change(within(form).getByLabelText('Name'), { target: { value: 'The guest' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Create' }))
    await waitFor(() => expect(posted).toEqual([{ type: 'character', name: 'The guest', universe: 'dracula', book: 'guest' }]))
  })

  it('offers no position the universe doesn’t have', async () => {
    stubCreate()
    renderApp('/u/dracula/b/dracula?at=dracula:99')
    await screen.findAllByText(/Dracula/)

    const form = await openFromMenu('Character')

    await waitFor(() => expect((within(form).getByLabelText('Where it goes') as HTMLSelectElement).value).toBe('book:dracula'))
    expect(within(form).queryByRole('checkbox')).toBeNull()
    expect(form.textContent).not.toContain('dracula:99')
  })

  it('asks for everything again after a create, as every save does', async () => {
    const { calls } = stubCreate()
    renderApp('/u/dracula/b/dracula')
    await screen.findByRole('heading', { name: 'Dracula', level: 1 })
    const form = await openFromMenu('Character')
    fireEvent.change(within(form).getByLabelText('Name'), { target: { value: 'Sister Agatha' } })
    await waitFor(() => expect(calls.some((c) => c.startsWith('/api/positions'))).toBe(true))
    const before = calls.filter((c) => c.startsWith('/api/positions') || c.startsWith('/api/types')).length

    fireEvent.click(within(form).getByRole('button', { name: 'Create' }))

    // Not only the lists: positions and types too, which a narrower refresh would leave cached.
    await waitFor(() => expect(calls.filter((c) => c.startsWith('/api/positions') || c.startsWith('/api/types')).length).toBeGreaterThan(before))
  })
})

describe('SC-WEB-68: New notes are guarded', () => {
  const finalHierarchy = {
    ...hierarchy,
    universe: { ...hierarchy.universe, books: hierarchy.universe.books.map((b) => ({ ...b, status: 'final' as const })) },
  }

  it('a final book can be chosen, but says it takes nothing until it’s unfrozen', async () => {
    const { posted } = stubCreate(() => result(), finalHierarchy)
    renderApp('/u/dracula/b/dracula')
    await screen.findByRole('heading', { name: 'Dracula', level: 1 })

    const form = await openFromMenu('Character')
    const where = within(form).getByLabelText('Where it goes') as HTMLSelectElement
    await waitFor(() => expect(where.value).toBe('book:dracula'))
    expect(where.selectedOptions[0].textContent).toBe('Dracula (book, final)')
    expect(form.textContent).toContain('Dracula is final (frozen), so nothing can be added to it. Unfreeze it on its page first')
    fireEvent.change(within(form).getByLabelText('Name'), { target: { value: 'Someone' } })
    expect((within(form).getByRole('button', { name: 'Create' }) as HTMLButtonElement).disabled).toBe(true)

    fireEvent.change(where, { target: { value: 'universe:dracula' } })
    expect(form.textContent).not.toContain('is final (frozen)')
    expect((within(form).getByRole('button', { name: 'Create' }) as HTMLButtonElement).disabled).toBe(false)
    expect(posted).toEqual([])
  })

  it('a refused create shows why, keeps the form, and writes nothing', async () => {
    const { posted } = stubCreate(
      () =>
        new Response(
          JSON.stringify({ code: 'frozen', message: "Book 'dracula' is final (frozen), so nothing can be added to it until it's unfrozen (F-BOOK-1)." }),
          { status: 409 },
        ),
    )
    const { history } = renderApp('/u/dracula/b/dracula')
    await screen.findByRole('heading', { name: 'Dracula', level: 1 })

    const form = await openFromMenu('Character')
    const name = within(form).getByLabelText('Name')
    fireEvent.change(name, { target: { value: 'Someone' } })
    await waitFor(() => expect((within(form).getByLabelText('Where it goes') as HTMLSelectElement).value).toBe('book:dracula'))
    fireEvent.click(within(form).getByRole('button', { name: 'Create' }))

    const alert = await within(form).findByRole('alert')
    expect(alert.textContent).toContain("Book 'dracula' is final (frozen)")
    expect(posted).toHaveLength(1)
    expect(history.location.pathname).toBe('/u/dracula/b/dracula')
    await waitFor(() => expect(document.activeElement).toBe(name))
  })
})
