// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient } from '@tanstack/react-query'
import { createBrowserHistory } from '@tanstack/react-router'
import { App } from './App'
import type { EntityPageResult, PageSection } from './api'
import type { EntitySource, WriteResult } from './editApi'
import { editorViewIn } from './components/MarkdownEditor'
import { differingLines } from './components/Editing'
import { entities, page, renderApp, stubApi } from './test/harness'

// Editing a note in place (#28): the lead and sections in CodeMirror, Edit
// file, saving with the hash, the conflict view, and the guards around it.

beforeAll(() => {
  // jsdom lays nothing out; CodeMirror measures text ranges.
  const rect = { x: 0, y: 0, width: 0, height: 0, top: 0, left: 0, right: 0, bottom: 0, toJSON: () => ({}) } as DOMRect
  Range.prototype.getBoundingClientRect = () => rect
  Range.prototype.getClientRects = () => ({ length: 0, item: () => null, [Symbol.iterator]: [][Symbol.iterator] }) as unknown as DOMRectList
  // In a shadow root, CodeMirror reads the selection through execCommand where getSelection isn't there.
  document.execCommand ??= () => false
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const mina = entities.find((e) => e.key === 'E-MINA')!
const swales = { ...mina, key: 'E-SWALES', id: 'E-SWALES', name: 'Mr. Swales' }

function section(name: string, line: number, body: string, level = 2): PageSection {
  return { name, level, headingPath: [name], status: 'canon', pinned: false, from: null, until: null, line, body, bookkeeping: null, links: [] }
}

const sections = [section('Overview', 9, 'Old text.'), section('Arc', 12, 'Later.'), section('Late arc', 16, 'Much later.', 3)]

function source(hash = 'h1', overview = 'Old text.'): EntitySource {
  return {
    version: 1,
    entity: mina,
    hash,
    text: `---\nname: Mina Harker\n---\n\n# Mina Harker\n\nA schoolmistress.\n\n## Overview\n${overview}\n\n## Arc\n<!-- wolfden: from=dracula:5 -->\nLater.\n\n### Late arc\nMuch later.\n`,
    lineEndings: 'lf',
    byteOrderMark: false,
    frozen: null,
    lead: { line: 4, text: '# Mina Harker\n\nA schoolmistress.' },
    sections: [
      { index: 0, name: 'Overview', level: 2, line: 9, comment: null, body: overview, pinned: false, parent: null },
      { index: 1, name: 'Arc', level: 2, line: 12, comment: '<!-- wolfden: from=dracula:5 -->', body: 'Later.', pinned: false, parent: null },
      { index: 2, name: 'Late arc', level: 3, line: 16, comment: null, body: 'Much later.', pinned: false, parent: 1 },
    ],
    standardSections: ['Overview', 'Appearance', 'Arc'],
  }
}

function written(extra: Partial<WriteResult> = {}): WriteResult {
  return {
    version: 1, changed: true, path: mina.path, hash: 'h2', entity: mina, effects: [], pinnedCanon: [], warnings: [], files: null, idAdded: null, ...extra,
  }
}

interface Post {
  path: string
  body: Record<string, unknown>
}

/**
 * The test den's API, with Mina's page as `sections`, her source, and the
 * edit endpoints, which answer `onPost` (by default, a change saved).
 */
function stubEditing(options: { onPost?: (post: Post, n: number) => Response | WriteResult; frozen?: string | null; entityKey?: string } = {}) {
  const pages = (key: string, at: string | null): EntityPageResult => {
    const summary = key === 'E-SWALES' ? swales : entities.find((e) => e.key === key)!
    return {
      version: 1,
      view: at ? 'canon_as_of' : 'full',
      position: at,
      inView: true,
      note: null,
      entity: page(summary, { lead: `# ${summary.name}\n\nA schoolmistress.`, sections, frozen: key === 'E-SWALES' ? (options.frozen ?? null) : null }),
    }
  }

  const stub = stubApi({ entityPage: pages })
  // The file as the API reads it now; a test changes it to play an edit made elsewhere.
  const file = { source: source() }
  const base = stub.fetchMock.getMockImplementation()!
  const posts: Post[] = []
  stub.fetchMock.mockImplementation((async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://127.0.0.1')
    if (url.pathname === '/api/entity/source') {
      stub.calls.push(`${url.pathname}${url.search}`)
      return Response.json(file.source)
    }

    if (url.pathname.startsWith('/api/edit/')) {
      stub.calls.push(`POST ${url.pathname}`)
      expect(init?.method).toBe('POST')
      expect(new Headers(init?.headers).get('Content-Type')).toBe('application/json')
      const post = { path: url.pathname.slice('/api/edit/'.length), body: JSON.parse(String(init?.body)) as Record<string, unknown> }
      posts.push(post)
      const answer = options.onPost?.(post, posts.length) ?? written()
      return answer instanceof Response ? answer : Response.json(answer)
    }

    return base(input)
  }) as typeof fetch)
  return { ...stub, posts, file }
}

async function editorFor(label: RegExp | string) {
  const group = await screen.findByRole('group', { name: label })
  await waitFor(() => expect(editorViewIn(group)).not.toBeNull())
  return { group, view: editorViewIn(group)! }
}

function replaceText(view: NonNullable<ReturnType<typeof editorViewIn>>, text: string) {
  act(() => view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: text } }))
}

async function openSectionEditor(name = 'Overview') {
  fireEvent.click(await screen.findByRole('button', { name: `Edit “${name}”` }))
  return editorFor(`Markdown of the section “${name}”`)
}

describe('SC-WEB-40: editing a section or the opening in place', () => {
  it('opens the section’s source in the editor, saves it with the hash, and refreshes the page', async () => {
    const { posts, calls } = stubEditing({ onPost: () => written({ effects: ['Replaced the text of \'Overview\' (line 9).'] }) })
    renderApp('/u/dracula/e/E-MINA')
    const { view } = await openSectionEditor()

    expect(view.state.doc.toString()).toBe('Old text.')
    expect(view.hasFocus).toBe(true)
    replaceText(view, 'New text with a [[link]].')
    const entityCalls = calls.filter((c) => c.startsWith('/api/entity?')).length
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    const saved = await screen.findByText('Saved the section “Overview”.')
    expect(posts).toEqual([
      { path: 'section', body: { entity: 'E-MINA', universe: 'dracula', expectedHash: 'h1', text: 'New text with a [[link]].', index: 0, name: 'Overview' } },
    ])
    expect(saved.closest('[role="status"]')!.textContent).toContain("Saving doesn't make a checkpoint.")
    expect(screen.queryByRole('group', { name: /Markdown of/ })).toBeNull()
    await waitFor(() => expect(calls.filter((c) => c.startsWith('/api/entity?')).length).toBeGreaterThan(entityCalls))
    expect(document.activeElement?.id).toBe('edit-saved')
  })

  it('SC-WEB-50: a save makes the checkpoint indicator ask again at once', async () => {
    const { calls } = stubEditing()
    renderApp('/u/dracula/e/E-MINA')
    const { view } = await openSectionEditor()
    await waitFor(() => expect(calls).toContain('/api/checkpoint'))
    const before = calls.filter((c) => c === '/api/checkpoint').length
    replaceText(view, 'New text.')
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await screen.findByText('Saved the section “Overview”.')
    await waitFor(() => expect(calls.filter((c) => c === '/api/checkpoint').length).toBeGreaterThan(before))
  })

  it('saves with Ctrl+Enter', async () => {
    const { posts } = stubEditing()
    renderApp('/u/dracula/e/E-MINA')
    const { view } = await openSectionEditor()
    replaceText(view, 'Typed.')

    fireEvent.keyDown(view.contentDOM, { key: 'Enter', ctrlKey: true })

    await screen.findByText('Saved the section “Overview”.')
    expect(posts.map((p) => p.body.text)).toEqual(['Typed.'])
  })

  it('asks before Escape drops unsaved text, and closes at once when nothing changed', async () => {
    const { posts } = stubEditing()
    renderApp('/u/dracula/e/E-MINA')
    const { view } = await openSectionEditor()
    replaceText(view, 'Not thrown away yet.')

    fireEvent.keyDown(view.contentDOM, { key: 'Escape' })
    const ask = await screen.findByRole('alertdialog', { name: "Discard the changes you haven't saved?" })
    expect(document.activeElement).toBe(within(ask).getByRole('button', { name: 'Keep editing' }))
    fireEvent.click(within(ask).getByRole('button', { name: 'Keep editing' }))
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull())
    expect(view.state.doc.toString()).toBe('Not thrown away yet.')
    await waitFor(() => expect(view.hasFocus).toBe(true))

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    fireEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Discard changes' }))
    await waitFor(() => expect(screen.queryByRole('group', { name: /Markdown of/ })).toBeNull())
    expect(posts).toEqual([])
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Edit “Overview”' }))
    expect(screen.getByText('Old text.')).toBeTruthy()

    // Unchanged, Escape closes without asking.
    const again = await openSectionEditor()
    fireEvent.keyDown(again.view.contentDOM, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('group', { name: /Markdown of/ })).toBeNull())
    expect(screen.queryByRole('alertdialog')).toBeNull()
  })

  it('sends nothing when the text is unchanged: Save is off, and Ctrl+Enter just closes', async () => {
    const { posts } = stubEditing()
    renderApp('/u/dracula/e/E-MINA')
    const { view } = await openSectionEditor()

    expect((screen.getByRole('button', { name: 'Save' }) as HTMLButtonElement).disabled).toBe(true)
    fireEvent.keyDown(view.contentDOM, { key: 'Enter', ctrlKey: true })

    await waitFor(() => expect(screen.queryByRole('group', { name: /Markdown of/ })).toBeNull())
    expect(posts).toEqual([])
  })

  it('opens on the file as it is now, not on an answer cached from an earlier edit', async () => {
    const { file } = stubEditing()
    renderApp('/u/dracula/e/E-MINA')
    const first = await openSectionEditor()
    expect(first.view.state.doc.toString()).toBe('Old text.')
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('group', { name: /Markdown of/ })).toBeNull())

    // Changed elsewhere meanwhile.
    file.source = source('h5', 'Changed in another editor.')
    const second = await openSectionEditor()
    await waitFor(() => expect(second.view.state.doc.toString()).toBe('Changed in another editor.'))
  })

  it('edits the opening, title heading included, and shows a preview with the page’s renderer', async () => {
    const { posts } = stubEditing()
    renderApp('/u/dracula/e/E-MINA')
    fireEvent.click(await screen.findByRole('button', { name: 'Edit the opening, the text before the sections' }))
    const { view, group } = await editorFor('Markdown of the opening')
    expect(view.state.doc.toString()).toBe('# Mina Harker\n\nA schoolmistress.')
    replaceText(view, '# Mina Harker\n\nA *teacher*.')

    fireEvent.click(screen.getByRole('button', { name: 'Preview' }))
    const preview = screen.getByLabelText('Preview of the opening')
    expect(within(preview).getByText('teacher').tagName).toBe('EM')
    expect(group.closest('[hidden]')).not.toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Preview' }))
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await screen.findByText('Saved the opening.')
    expect(posts).toEqual([{ path: 'lead', body: { entity: 'E-MINA', universe: 'dracula', expectedHash: 'h1', text: '# Mina Harker\n\nA *teacher*.' } }])
  })

  it('keeps the section comment aside, and says how to change it', async () => {
    stubEditing()
    renderApp('/u/dracula/e/E-MINA')
    const { view } = await openSectionEditor('Arc')

    expect(view.state.doc.toString()).toBe('Later.')
    expect(screen.getByText('<!-- wolfden: from=dracula:5 -->').closest('p')!.textContent).toContain('change it with Edit file')
  })

  it('shows everything while editing, with a badge, and the position view again after', async () => {
    const { calls } = stubEditing()
    renderApp('/u/dracula/e/E-MINA?at=dracula:3')
    await screen.findByRole('heading', { level: 1, name: 'Mina Harker' })
    expect(screen.queryByText('Editing shows everything')).toBeNull()

    await openSectionEditor()
    expect(screen.getByText('Editing shows everything')).toBeTruthy()
    await waitFor(() => expect(calls).toContain('/api/entity?entity=E-MINA&universe=dracula'))

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByText('Editing shows everything')).toBeNull())
    expect(calls.filter((c) => c === '/api/entity?entity=E-MINA&universe=dracula&asOf=dracula%3A3').length).toBeGreaterThan(0)
  })

  it('shows what the save did: effects, pinned canon, and warnings', async () => {
    stubEditing({
      onPost: () =>
        written({
          effects: ["Replaced the text of 'Overview' (line 9)."],
          pinnedCanon: ["section 'Overview' (line 9) is pinned canon, and the edit changed it"],
          warnings: [{ path: mina.path, line: 10, severity: 'warning', rule: 'F-LINK-7', message: "'[[link]]' doesn't reach an entity." }],
        }),
    })
    renderApp('/u/dracula/e/E-MINA')
    const { view } = await openSectionEditor()
    replaceText(view, 'Changed.')
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    const result = (await screen.findByText('Saved the section “Overview”.')).closest('[role="status"]') as HTMLElement
    expect(within(result).getByText("section 'Overview' (line 9) is pinned canon, and the edit changed it")).toBeTruthy()
    expect(within(result).getByText("Replaced the text of 'Overview' (line 9).")).toBeTruthy()
    expect(result.textContent).toContain("Line 10: '[[link]]' doesn't reach an entity.")
  })

  it('shows an edit the den would reject, and keeps the editor open', async () => {
    stubEditing({ onPost: () => Response.json({ code: 'invalid_edit', message: 'The edit would leave errors: F-YAML-1.' }, { status: 422 }) })
    renderApp('/u/dracula/e/E-MINA')
    const { view } = await openSectionEditor()
    replaceText(view, 'Bad.')
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect((await screen.findByRole('alert')).textContent).toContain('the note would have errors: The edit would leave errors: F-YAML-1.')
    expect(screen.getByRole('group', { name: 'Markdown of the section “Overview”' })).toBeTruthy()
  })
})

describe('SC-WEB-41: adding, renaming, and removing sections', () => {
  it('renames a section inline', async () => {
    const { posts } = stubEditing()
    renderApp('/u/dracula/e/E-MINA')
    fireEvent.click(await screen.findByRole('button', { name: 'Rename “Arc”' }))
    const input = await screen.findByLabelText('New name for the section “Arc”')
    await waitFor(() => expect(document.activeElement).toBe(input))
    fireEvent.change(input, { target: { value: 'Story' } })
    await waitFor(() => expect((screen.getByRole('button', { name: 'Rename' }) as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(screen.getByRole('button', { name: 'Rename' }))

    await screen.findByText('Saved the section “Arc”.')
    expect(posts).toEqual([{ path: 'section/rename', body: { entity: 'E-MINA', universe: 'dracula', expectedHash: 'h1', index: 1, name: 'Arc', newName: 'Story' } }])
  })

  it('asks in the page before removing a section and its subsections, with no browser dialog', async () => {
    const confirm = vi.fn(() => true)
    vi.stubGlobal('confirm', confirm)
    const { posts } = stubEditing()
    renderApp('/u/dracula/e/E-MINA')
    fireEvent.click(await screen.findByRole('button', { name: 'Remove “Arc”' }))

    const ask = await screen.findByRole('group', { name: /Remove the section “Arc” and its 1 subsection\?/ })
    const keep = within(ask).getByRole('button', { name: 'Keep it' })
    expect(document.activeElement).toBe(keep)
    fireEvent.click(keep)
    expect(posts).toEqual([])

    fireEvent.click(screen.getByRole('button', { name: 'Remove “Arc”' }))
    const again = await screen.findByRole('group', { name: /Remove the section “Arc”/ })
    await waitFor(() => expect((within(again).getByRole('button', { name: 'Remove section' }) as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(within(again).getByRole('button', { name: 'Remove section' }))

    await screen.findByText(/Saved the note \(removed “Arc”\)/)
    expect(posts).toEqual([{ path: 'section/remove', body: { entity: 'E-MINA', universe: 'dracula', expectedHash: 'h1', index: 1, name: 'Arc' } }])
    expect(confirm).not.toHaveBeenCalled()
  })

  it('adds a section before another, with its text', async () => {
    const { posts } = stubEditing()
    renderApp('/u/dracula/e/E-MINA')
    fireEvent.click(await screen.findByRole('button', { name: 'Add a section' }))
    const form = await screen.findByRole('form', { name: 'Add a section' })
    fireEvent.change(within(form).getByLabelText('Section name'), { target: { value: 'Appearance' } })
    fireEvent.change(within(form).getByLabelText('Where'), { target: { value: '1' } })
    const { view } = await editorFor('Markdown of the new section')
    replaceText(view, 'Dark hair.')
    fireEvent.click(within(form).getByRole('button', { name: 'Add section' }))

    await screen.findByText('Saved the new section “Appearance”.')
    expect(posts).toEqual([
      { path: 'section/add', body: { entity: 'E-MINA', universe: 'dracula', expectedHash: 'h1', name: 'Appearance', text: 'Dark hair.', beforeIndex: 1, beforeName: 'Arc' } },
    ])
  })
})

describe('SC-WEB-42: Edit file', () => {
  it('opens the whole source, frontmatter included, and saves it', async () => {
    const { posts } = stubEditing()
    renderApp('/u/dracula/e/E-MINA')
    fireEvent.click(await screen.findByRole('button', { name: "Edit file: the note's whole source, frontmatter included" }))
    const { view } = await editorFor('Markdown of the whole file')

    expect(view.state.doc.toString()).toBe(source().text)
    expect(screen.queryByRole('button', { name: 'Edit “Overview”' })).toBeNull()
    replaceText(view, source().text.replace('Old text.', 'Edited in the file.'))
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await screen.findByText('Saved the whole file.')
    expect(posts[0].path).toBe('file')
    expect(posts[0].body.text).toContain('Edited in the file.')
  })
})

describe('SC-WEB-43: when the file changed meanwhile', () => {
  const stale = () =>
    Response.json({ code: 'stale_file', message: 'It changed.', current: source('h9', 'An agent wrote this.') }, { status: 409 })

  it('shows both versions, marking the lines that differ, and keeps mine on the new hash', async () => {
    const { posts } = stubEditing({ onPost: (_, n) => (n === 1 ? stale() : written()) })
    renderApp('/u/dracula/e/E-MINA')
    const { view } = await openSectionEditor()
    replaceText(view, 'My text.')
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    const conflict = await screen.findByRole('region', { name: 'The note changed while you were editing the section “Overview”' })
    expect(document.activeElement).toBe(within(conflict).getByRole('heading', { level: 3 }))
    expect(within(conflict).getByText('My text.').className).toBe('differs')
    expect(within(conflict).getByText('An agent wrote this.').className).toBe('differs')

    fireEvent.click(within(conflict).getByRole('button', { name: 'Keep mine' }))
    await screen.findByText('Saved the section “Overview”.')
    expect(posts.map((p) => [p.body.expectedHash, p.body.text])).toEqual([
      ['h1', 'My text.'],
      ['h9', 'My text.'],
    ])
  })

  it('takes theirs, dropping my edit', async () => {
    const { posts } = stubEditing({ onPost: () => stale() })
    renderApp('/u/dracula/e/E-MINA')
    const { view } = await openSectionEditor()
    replaceText(view, 'My text.')
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    fireEvent.click(await screen.findByRole('button', { name: 'Take theirs' }))
    await waitFor(() => expect(screen.queryByRole('region', { name: /The note changed/ })).toBeNull())
    expect(screen.queryByRole('group', { name: /Markdown of/ })).toBeNull()
    expect(posts).toHaveLength(1)
  })

  it('copies mine and reloads the editor with the file as it is', async () => {
    const writeText = vi.fn(async () => {})
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } })
    stubEditing({ onPost: () => stale() })
    renderApp('/u/dracula/e/E-MINA')
    const { view } = await openSectionEditor()
    replaceText(view, 'My text.')
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    fireEvent.click(await screen.findByRole('button', { name: 'Copy mine and reload' }))
    const reopened = await editorFor('Markdown of the section “Overview”')
    expect(writeText).toHaveBeenCalledWith('My text.')
    await waitFor(() => expect(reopened.view.state.doc.toString()).toBe('An agent wrote this.'))
    expect(screen.getByText(/Your version is copied/)).toBeTruthy()
  })

  it('keeps mine in the same section when the new file has two of that name', async () => {
    const current = source('h9', 'An agent wrote this.')
    current.sections = [current.sections[0], { ...current.sections[0], index: 1, line: 11, body: 'A second Overview.' }, ...current.sections.slice(1).map((x) => ({ ...x, index: x.index + 1 }))]
    const { posts } = stubEditing({
      onPost: (_, n) => (n === 1 ? Response.json({ code: 'stale_file', message: 'It changed.', current }, { status: 409 }) : written()),
    })
    renderApp('/u/dracula/e/E-MINA')
    const { view } = await openSectionEditor()
    replaceText(view, 'My text.')
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    const conflict = await screen.findByRole('region', { name: /The note changed/ })
    expect(within(conflict).getByText('An agent wrote this.')).toBeTruthy()
    fireEvent.click(within(conflict).getByRole('button', { name: 'Keep mine' }))
    await screen.findByText('Saved the section “Overview”.')
    expect(posts[1].body).toMatchObject({ expectedHash: 'h9', index: 0, name: 'Overview', text: 'My text.' })
  })

  it('marks the lines only one version has', () => {
    expect(differingLines('a\nb\nc', 'a\nx\nc')).toEqual([
      [false, true, false],
      [false, true, false],
    ])
  })
})

describe('SC-WEB-44: guards around editing', () => {
  it('warns before leaving the page with unsaved changes, in the app and when the tab closes', async () => {
    stubEditing()
    const { history } = renderApp('/u/dracula/e/E-MINA')
    const { view } = await openSectionEditor()

    // Nothing changed yet: closing the tab isn't stopped.
    const clean = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(clean)
    expect(clean.defaultPrevented).toBe(false)

    replaceText(view, 'Unsaved.')
    const dirty = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(dirty)
    expect(dirty.defaultPrevented).toBe(true)

    const contents = await screen.findByRole('navigation', { name: 'Contents' })
    fireEvent.click(within(contents).getByRole('link', { name: 'Jonathan Harker' }))
    const prompt = await screen.findByRole('alertdialog')
    expect(prompt.textContent).toContain("You have changes that aren't saved.")
    expect(history.location.pathname).toBe('/u/dracula/e/E-MINA')

    fireEvent.click(within(prompt).getByRole('button', { name: 'Keep editing' }))
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull())
    expect(view.state.doc.toString()).toBe('Unsaved.')

    fireEvent.click(within(contents).getByRole('link', { name: 'Jonathan Harker' }))
    fireEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Leave without saving' }))
    await waitFor(() => expect(history.location.pathname).toBe('/u/dracula/e/E-JONATHAN'))
  })

  it('asks before Back leaves with unsaved changes', async () => {
    stubEditing()
    // The browser's own Back: a memory history doesn't block it, so this uses the browser history.
    window.scrollTo = () => {}
    window.history.replaceState(null, '', '/u/dracula')
    const history = createBrowserHistory()
    render(<App queryClient={new QueryClient({ defaultOptions: { queries: { retry: false } } })} history={history} />)
    const contents = await screen.findByRole('navigation', { name: 'Contents' })
    fireEvent.click(within(contents).getByRole('link', { name: 'Mina Harker' }))
    await waitFor(() => expect(history.location.pathname).toBe('/u/dracula/e/E-MINA'))
    const { view } = await openSectionEditor()
    replaceText(view, 'Unsaved.')

    act(() => window.history.back())
    const prompt = await screen.findByRole('alertdialog')
    expect(prompt.textContent).toContain("You have changes that aren't saved.")
    fireEvent.click(within(prompt).getByRole('button', { name: 'Leave without saving' }))
    await waitFor(() => expect(history.location.pathname).toBe('/u/dracula'))
  })

  it('asks before the position bar leaves the edit, and leaving closes the editor', async () => {
    stubEditing()
    const { history } = renderApp('/u/dracula/e/E-MINA?at=dracula:3')
    const { view } = await openSectionEditor()
    replaceText(view, 'Unsaved.')

    fireEvent.keyDown(screen.getByRole('slider', { name: 'Story position' }), { key: 'ArrowLeft' })
    const prompt = await screen.findByRole('alertdialog')
    fireEvent.click(within(prompt).getByRole('button', { name: 'Keep editing' }))
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull())
    expect(new URLSearchParams(history.location.search).get('at')).toBe('dracula:3')
    expect(screen.getByRole('group', { name: 'Markdown of the section “Overview”' })).toBeTruthy()

    fireEvent.keyDown(screen.getByRole('slider', { name: 'Story position' }), { key: 'ArrowLeft' })
    fireEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Leave without saving' }))
    await waitFor(() => expect(new URLSearchParams(history.location.search).get('at')).toBe('dracula:2'))
    await waitFor(() => expect(screen.queryByRole('group', { name: /Markdown of/ })).toBeNull())
    expect(screen.queryByText('Editing shows everything')).toBeNull()
    expect(screen.queryByRole('alertdialog')).toBeNull()
  })

  it('turns the Edit controls off on a frozen note, and says why', async () => {
    stubEditing({ frozen: "universes/dracula/x.md is in book 'dracula', which is final (frozen), so it can't be edited until the book is unfrozen (F-BOOK-1)." })
    renderApp('/u/dracula/e/E-SWALES')
    const edit = (await screen.findByRole('button', { name: 'Edit “Overview”' })) as HTMLButtonElement

    expect(edit.disabled).toBe(true)
    for (const name of ['Rename “Overview”', 'Remove “Overview”', 'Add a section', "Edit file: the note's whole source, frontmatter included"]) {
      expect((screen.getByRole('button', { name }) as HTMLButtonElement).disabled).toBe(true)
    }

    const reason = document.getElementById(edit.getAttribute('aria-describedby')!)!
    expect(reason.textContent).toContain('which is final (frozen)')
  })

  it('edits one piece at a time', async () => {
    stubEditing()
    renderApp('/u/dracula/e/E-MINA')
    await openSectionEditor()

    expect((screen.getByRole('button', { name: 'Edit “Arc”' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: 'Edit the opening, the text before the sections' }) as HTMLButtonElement).disabled).toBe(true)
  })
})
