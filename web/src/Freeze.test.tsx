// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import type { BookFreeze, FreezeResult } from './freezeApi'
import { renderApp, stubApi } from './test/harness'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const TAG = 'book/dracula/dracula/final'

function freezeStatus(extra: Partial<BookFreeze> = {}): BookFreeze {
  return {
    version: 1,
    universe: 'dracula',
    book: 'dracula',
    name: 'Dracula',
    path: 'universes/dracula/books/dracula/book.md',
    hash: 'h-draft',
    status: 'draft',
    backend: 'git',
    history: true,
    reason: null,
    nextTag: TAG,
    unfinished: false,
    ...extra,
  }
}

function result(extra: Partial<FreezeResult> = {}): FreezeResult {
  return {
    version: 1,
    universe: 'dracula',
    book: 'dracula',
    path: 'universes/dracula/books/dracula/book.md',
    hash: 'h-final',
    status: 'final',
    changed: true,
    backend: 'git',
    snapshot: 'tagged',
    checkpoint: 'c2aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
    tag: TAG,
    message: `Froze book 'Dracula': book.md now says status: final. Checkpointed the den (c2aaaaa). Tagged the snapshot '${TAG}'.`,
    warnings: [],
    idAdded: null,
    ...extra,
  }
}

/** Answers GET /api/book/freeze with `current()`, and the POSTs with `post`; records each POST's path and body. */
function stubFreeze(current: () => BookFreeze, post: (path: string, body: Record<string, unknown>) => Response | FreezeResult) {
  const posted: { path: string; body: Record<string, unknown> }[] = []
  let gets = 0
  const api = stubApi({
    override: (path, _params, init) => {
      if (path !== '/api/book/freeze' && path !== '/api/book/unfreeze') {
        return undefined
      }

      if (init?.method === 'POST') {
        expect(new Headers(init.headers).get('Content-Type')).toBe('application/json')
        const body = JSON.parse(String(init.body)) as Record<string, unknown>
        posted.push({ path, body })
        return post(path, body)
      }

      gets++
      return current()
    },
  })
  return { ...api, posted, gets: () => gets }
}

async function openBook() {
  renderApp('/u/dracula/b/dracula')
  await screen.findByRole('heading', { name: 'Dracula', level: 1 })
}

/** The confirmation's accessible name is what it asks. */
const PROMPT = /^(Freeze|Unfreeze|Finish freezing) Dracula\?/

function confirmPrompt(): HTMLElement {
  return screen.getByRole('group', { name: PROMPT })
}

/** What the freeze or unfreeze did, once it's shown. */
async function outcome(): Promise<HTMLElement> {
  return waitFor(() => {
    const shown = document.querySelector<HTMLElement>('.freeze-control [role="status"]')
    expect(shown).not.toBeNull()
    return shown!
  })
}

describe('SC-WEB-66: I freeze and unfreeze a book on its page', () => {
  it('asks first, says what the freeze will do, then shows what it did', async () => {
    let status = freezeStatus()
    const { posted } = stubFreeze(
      () => status,
      () => {
        status = freezeStatus({ status: 'final', hash: 'h-final', nextTag: null })
        return result()
      },
    )
    await openBook()

    fireEvent.click(await screen.findByRole('button', { name: 'Freeze…' }))
    const prompt = confirmPrompt()
    expect(prompt.textContent).toContain("Freeze Dracula? book.md will say status: final, and its notes, chapters, and outline can't be edited")
    expect(prompt.textContent).toContain(`Wolfden then makes a checkpoint of the whole den and tags it ${TAG}`)
    expect(posted).toEqual([])

    fireEvent.click(within(prompt).getByRole('button', { name: 'Freeze book' }))

    const done = await outcome()
    expect(done.textContent).toContain(`Tagged the snapshot '${TAG}'`)
    expect(posted).toEqual([{ path: '/api/book/freeze', body: { universe: 'dracula', book: 'dracula', expectedHash: 'h-draft' } }])

    // The page catches up: the book is final now, and offers to unfreeze it.
    expect(await screen.findByRole('button', { name: 'Unfreeze…' })).toBeTruthy()
  })

  it('says no snapshot is taken in a den without history', async () => {
    stubFreeze(() => freezeStatus({ history: false, backend: 'none', reason: 'This den has no history.', nextTag: null }), () => result())
    await openBook()

    fireEvent.click(await screen.findByRole('button', { name: 'Freeze…' }))

    expect(confirmPrompt().textContent).toContain('This den has no history, so no snapshot is taken.')
  })

  it('unfreezes a final book after asking', async () => {
    const { posted } = stubFreeze(
      () => freezeStatus({ status: 'final', hash: 'h-final', nextTag: null }),
      () =>
        result({
          status: 'draft',
          snapshot: 'checkpointed',
          tag: null,
          message: "Unfroze book 'Dracula': book.md now says status: draft, so the book can be edited again. Checkpointed the den (c3aaaaa).",
        }),
    )
    await openBook()

    fireEvent.click(await screen.findByRole('button', { name: 'Unfreeze…' }))
    const prompt = confirmPrompt()
    expect(prompt.textContent).toContain('Unfreeze Dracula? Its notes, chapters, and outline can be edited again.')
    expect(prompt.textContent).toContain('Its freeze tags stay where they are.')
    fireEvent.click(within(prompt).getByRole('button', { name: 'Unfreeze book' }))

    expect((await outcome()).textContent).toContain('so the book can be edited again')
    expect(posted).toEqual([{ path: '/api/book/unfreeze', body: { universe: 'dracula', book: 'dracula', expectedHash: 'h-final' } }])
  })

  it('shows a failed checkpoint as a warning and offers to finish the freeze', async () => {
    let status = freezeStatus()
    const message =
      "Book 'Dracula' is final in book.md now, but the checkpoint failed, so nothing was committed or tagged: git refused. Once that's fixed, freeze the book again to take the snapshot."
    const { posted } = stubFreeze(
      () => status,
      () => {
        const failed = posted.length === 1
        status = freezeStatus({ status: 'final', hash: 'h-final', nextTag: null, unfinished: failed })
        return failed ? result({ snapshot: 'checkpoint_failed', checkpoint: null, tag: null, message }) : result({ changed: false })
      },
    )
    await openBook()

    fireEvent.click(await screen.findByRole('button', { name: 'Freeze…' }))
    fireEvent.click(within(confirmPrompt()).getByRole('button', { name: 'Freeze book' }))

    const warning = await outcome()
    expect(warning.textContent).toContain('the checkpoint failed')
    expect(warning.className).toContain('freeze-warning')

    fireEvent.click(await screen.findByRole('button', { name: 'Finish freezing…' }))
    const prompt = confirmPrompt()
    expect(prompt.textContent).toContain('Wolfden makes the checkpoint and adds the tag that are missing')
    fireEvent.click(within(prompt).getByRole('button', { name: 'Finish freezing' }))

    await waitFor(() => expect(posted).toHaveLength(2))
    expect(posted[1]).toEqual({ path: '/api/book/freeze', body: { universe: 'dracula', book: 'dracula', expectedHash: 'h-final' } })
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Finish freezing…' })).toBeNull())
  })

  it('shows a refusal in the prompt, and looks at book.md again', async () => {
    const api = stubFreeze(
      () => freezeStatus(),
      () =>
        new Response(JSON.stringify({ code: 'stale_file', message: 'universes/dracula/books/dracula/book.md has changed since it was read.' }), {
          status: 409,
        }),
    )
    await openBook()
    fireEvent.click(await screen.findByRole('button', { name: 'Freeze…' }))
    const before = api.gets()

    fireEvent.click(within(confirmPrompt()).getByRole('button', { name: 'Freeze book' }))

    expect((await screen.findByRole('alert')).textContent).toContain('has changed since it was read')
    await waitFor(() => expect(api.gets()).toBeGreaterThan(before))
    expect(confirmPrompt()).toBeTruthy()
  })

  it('Escape or Cancel closes the prompt without freezing', async () => {
    const { posted } = stubFreeze(() => freezeStatus(), () => result())
    await openBook()

    fireEvent.click(await screen.findByRole('button', { name: 'Freeze…' }))
    fireEvent.keyDown(confirmPrompt(), { key: 'Escape' })
    expect(screen.queryByRole('group', { name: PROMPT })).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: 'Freeze…' }))
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(screen.queryByRole('group', { name: PROMPT })).toBeNull()
    expect(posted).toEqual([])
  })
})
