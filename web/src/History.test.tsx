// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { pairChanges, parsePatch, wordDiff } from './diff'
import { FileDiffView } from './components/DiffView'
import { chooseSelection, relativeTime } from './pages/HistoryPage'
import { validateEntitySearch } from './entitySearch'
import type { FileDiff, HistoryDiffResult, HistoryEntry, HistoryResult } from './historyApi'
import { entities, renderApp, stubApi } from './test/harness'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const mina = entities.find((e) => e.key === 'E-MINA')!

const PATCH = [
  'diff --git a/notes/mina.md b/notes/mina.md',
  'index 1111111..2222222 100644',
  '--- a/notes/mina.md',
  '+++ b/notes/mina.md',
  '@@ -1,4 +1,4 @@ title',
  ' ---',
  '-status: draft',
  '+status: canon',
  ' ---',
  ' ',
  '@@ -10 +10,2 @@',
  '-Last line',
  '\\ No newline at end of file',
  '+Last line',
  '+--- a new line that looks like a header',
  '\\ No newline at end of file',
  '',
].join('\n')

describe('SC-WEB-16: a note’s diff, read as a unified diff', () => {
  it('parses hunks into numbered context, removed, and added lines', () => {
    const [part] = parsePatch(PATCH)
    expect(part.headers).toEqual(['diff --git a/notes/mina.md b/notes/mina.md', 'index 1111111..2222222 100644', '--- a/notes/mina.md', '+++ b/notes/mina.md'])
    expect(part.hunks).toHaveLength(2)

    const [first, second] = part.hunks
    expect(first.header).toBe('@@ -1,4 +1,4 @@ title')
    expect(first.lines.map((l) => [l.kind, l.text, l.oldNumber, l.newNumber])).toEqual([
      ['context', '---', 1, 1],
      ['removed', 'status: draft', 2, null],
      ['added', 'status: canon', null, 2],
      ['context', '---', 3, 3],
      ['context', '', 4, 4],
    ])

    // A count of one is left out of the header; a "---" line inside a hunk is text, not a header.
    expect(second.lines.map((l) => [l.kind, l.text, l.oldNumber, l.newNumber, l.noNewline])).toEqual([
      ['removed', 'Last line', 10, null, true],
      ['added', 'Last line', null, 10, false],
      ['added', '--- a new line that looks like a header', null, 11, true],
    ])
  })

  it('keeps a carriage return in its line, and shows it', () => {
    const patch = 'diff --git a/n.md b/n.md\n--- a/n.md\n+++ b/n.md\n@@ -1,2 +1,2 @@\n one\r\n-two\r\n+tw\ro\r\n'
    const lines = parsePatch(patch)[0].hunks[0].lines
    expect(lines.map((l) => l.text)).toEqual(['one\r', 'two\r', 'tw\ro\r'])

    const { container } = render(<FileDiffView file={{ change: { kind: 'modified', path: 'n.md', oldPath: null }, patch }} />)
    const marks = container.querySelectorAll('.diff-cr')
    // One on each line's CRLF, and the lone one in the middle of "tw\ro".
    expect(marks).toHaveLength(4)
    expect(marks[0].textContent).toBe('␍')
  })

  it('renders line numbers, colours, hunk headers, and "No newline at end of file"', () => {
    const { container } = render(<FileDiffView file={{ change: { kind: 'modified', path: 'notes/mina.md', oldPath: null }, patch: PATCH }} />)

    expect([...container.querySelectorAll('.diff-hunk')].map((r) => r.textContent)).toEqual(['@@ -1,4 +1,4 @@ title', '@@ -10 +10,2 @@'])
    expect(container.querySelectorAll('.diff-added')).toHaveLength(3)
    expect(container.querySelectorAll('.diff-removed')).toHaveLength(2)
    expect(container.querySelectorAll('.diff-context')).toHaveLength(3)
    expect(container.querySelectorAll('.diff-eof')).toHaveLength(2)
    expect(screen.getAllByText('No newline at end of file')).toHaveLength(2)

    const removed = container.querySelector('.diff-removed')!
    expect([...removed.querySelectorAll('.diff-number')].map((c) => c.textContent)).toEqual(['2', ''])
    // The headers the file's heading says aren't repeated.
    expect(container.textContent).not.toContain('index 1111111')
    expect(screen.getByRole('heading', { level: 3 }).textContent).toBe('Changed notes/mina.md')
  })

  it('highlights the words that changed within a changed line', () => {
    expect(wordDiff('status: draft', 'status: canon')).toEqual({
      removed: [
        { text: 'status: ', changed: false },
        { text: 'draft', changed: true },
      ],
      added: [
        { text: 'status: ', changed: false },
        { text: 'canon', changed: true },
      ],
    })
    // Nothing but spaces in common: the whole line is marked instead.
    expect(wordDiff('alpha beta', 'gamma delta')).toBeNull()
    // Too long to compare cheaply.
    expect(wordDiff('a '.repeat(300), 'b '.repeat(300))).toBeNull()

    const lines = parsePatch(PATCH)[0].hunks[0].lines
    const pairs = pairChanges(lines)
    expect(pairs.get(lines[1])?.find((s) => s.changed)?.text).toBe('draft')

    const { container } = render(<FileDiffView file={{ change: { kind: 'modified', path: 'notes/mina.md', oldPath: null }, patch: PATCH }} />)
    expect([...container.querySelectorAll('.diff-word')].map((w) => w.textContent)).toEqual(['draft', 'canon'])
  })

  it('renders HTML in a note as text, never as HTML', () => {
    const patch = [
      'diff --git a/n.md b/n.md',
      '--- a/n.md',
      '+++ b/n.md',
      '@@ -0,0 +1,2 @@',
      '+<script>window.pwned = true</script>',
      '+<img src=x onerror="window.pwned = true"> and <b>bold</b>',
      '',
    ].join('\n')
    const { container } = render(<FileDiffView file={{ change: { kind: 'added', path: 'n.md', oldPath: null }, patch }} />)

    expect(container.querySelector('script')).toBeNull()
    expect(container.querySelector('img')).toBeNull()
    expect(container.querySelector('b')).toBeNull()
    expect(container.textContent).toContain('<script>window.pwned = true</script>')
    expect(container.textContent).toContain('<img src=x onerror="window.pwned = true"> and <b>bold</b>')
    expect((window as { pwned?: boolean }).pwned).toBeUndefined()
  })

  it('shows a rename with no change to the text, and a binary file’s note', () => {
    render(<FileDiffView file={{ change: { kind: 'renamed', path: 'notes/mina-harker.md', oldPath: 'notes/mina.md' }, patch: '' }} />)
    expect(screen.getByRole('heading', { level: 3 }).textContent).toBe('Renamed from notes/mina.md notes/mina-harker.md')
    expect(screen.getByText('No change to the text.')).toBeTruthy()
    cleanup()

    render(<FileDiffView file={{ change: { kind: 'modified', path: 'n.md', oldPath: null }, patch: 'diff --git a/n.md b/n.md\nBinary files a/n.md and b/n.md differ\n' }} />)
    expect(screen.getByText('Binary files a/n.md and b/n.md differ')).toBeTruthy()
  })
})

// A history of Mina Harker's note: three checkpoints a page, five in all.
const when = (daysAgo: number) => new Date(Date.now() - daysAgo * 24 * 3600 * 1000).toISOString()
const allEntries: HistoryEntry[] = [
  { id: 'c5aaaaaaaa', when: when(1), author: 'A. Writer', message: 'Mina keeps a journal\n\nCo-Authored-By: Claude', changes: [{ kind: 'modified', path: mina.path, oldPath: null }] },
  { id: 'c4bbbbbbbb', when: when(2), author: 'A. Writer', message: 'Rename Mina', changes: [{ kind: 'renamed', path: mina.path, oldPath: 'old/mina.md' }] },
  { id: 'c3cccccccc', when: when(3), author: 'A. Writer', message: 'Fix a typo', changes: [{ kind: 'modified', path: mina.path, oldPath: null }] },
  { id: 'c2dddddddd', when: when(4), author: 'A. Writer', message: 'Mina marries', changes: [{ kind: 'modified', path: mina.path, oldPath: null }] },
  { id: 'c1eeeeeeee', when: when(5), author: 'A. Writer', message: 'Add Mina', changes: [{ kind: 'added', path: mina.path, oldPath: null }] },
]

const patchOf = (text: string) => `diff --git a/${mina.path} b/${mina.path}\n--- a/${mina.path}\n+++ b/${mina.path}\n@@ -1 +1 @@\n-before\n+${text}\n`

function historyApi(options: { uncommitted?: boolean; available?: boolean } = {}) {
  const { uncommitted = true, available = true } = options
  return (path: string, params: URLSearchParams): unknown => {
    if (path === '/api/history') {
      if (!available) {
        return {
          version: 1, entity: mina, backend: 'none', available: false,
          reason: "This den isn't in a git repository, so it has no history.",
          count: 0, truncated: false, nextBefore: null, entries: null,
        } satisfies HistoryResult
      }

      const before = params.get('before')
      const start = before ? allEntries.findIndex((e) => e.id === before) + 1 : 0
      const limit = 3
      const page = allEntries.slice(start, start + limit)
      const truncated = start + limit < allEntries.length
      return {
        version: 1, entity: mina, backend: 'git', available: true, reason: null,
        count: page.length, truncated, nextBefore: truncated ? page[page.length - 1].id : null, entries: page,
      } satisfies HistoryResult
    }

    if (path === '/api/history/diff') {
      const checkpoint = params.get('checkpoint')
      const from = params.get('from')
      const to = params.get('to')
      let files: FileDiff[] = []
      if (checkpoint) {
        files = [{ change: { kind: 'modified', path: mina.path, oldPath: null }, patch: patchOf(`what ${checkpoint} changed`) }]
      } else if (from) {
        files = [{ change: { kind: 'modified', path: mina.path, oldPath: null }, patch: patchOf(`from ${from} to ${to ?? 'now'}`) }]
      } else if (uncommitted) {
        files = [{ change: { kind: 'modified', path: mina.path, oldPath: null }, patch: patchOf('an edit not yet checkpointed') }]
      }

      if (!available) {
        return { version: 1, entity: mina, backend: 'none', available: false, reason: 'No history.', diff: null } satisfies HistoryDiffResult
      }

      return { version: 1, entity: mina, backend: 'git', available: true, reason: null, diff: { from: from ?? checkpoint, to, files } } satisfies HistoryDiffResult
    }

    return undefined
  }
}

const search = (history: { location: { search: string } }) => new URLSearchParams(history.location.search)

describe('SC-WEB-17: I look at a note’s history in the browser', () => {
  it('opens from a History tab on the note’s page, keeping the position', async () => {
    stubApi({ override: historyApi() })
    const { history } = renderApp('/u/dracula/e/E-MINA?at=dracula:3')

    const tabs = await screen.findByRole('navigation', { name: 'Note views' })
    expect(within(tabs).getByText('Note').getAttribute('aria-current')).toBe('page')
    fireEvent.click(within(tabs).getByRole('link', { name: 'History' }))

    await screen.findByRole('heading', { name: 'Checkpoints' })
    expect(history.location.pathname).toBe('/u/dracula/e/E-MINA')
    expect(search(history).get('view')).toBe('history')
    expect(search(history).get('at')).toBe('dracula:3')
    expect(screen.getByText(/whatever the story position/)).toBeTruthy()
  })

  it('works for an entity named by a path with slashes', async () => {
    const { calls } = stubApi({ override: historyApi() })
    renderApp('/u/dracula/e/universes/dracula/books/dracula/mina.md?view=history')

    await screen.findByRole('heading', { name: 'Checkpoints' })
    expect(calls).toContain('/api/history?entity=universes%2Fdracula%2Fbooks%2Fdracula%2Fmina.md&universe=dracula&limit=20')
  })

  it('lists checkpoints with when, who, the message, and the change; pages to older ones', async () => {
    const { calls } = stubApi({ override: historyApi() })
    renderApp('/u/dracula/e/E-MINA?view=history')

    const list = (await screen.findByRole('heading', { name: 'Checkpoints' })).parentElement!
    await within(list).findByRole('button', { name: 'Mina keeps a journal' })
    const first = within(list).getByRole('button', { name: 'Mina keeps a journal' }).closest('li')!
    expect(first.textContent).toContain('A. Writer')
    expect(first.textContent).toContain('c5aaaaa')
    expect(first.textContent).toContain('Co-Authored-By: Claude')
    expect(first.querySelector('time')!.textContent).toBe('yesterday')
    expect(first.querySelector('time')!.getAttribute('title')).toBeTruthy()
    expect(within(list).getByRole('button', { name: 'Rename Mina' }).closest('li')!.querySelector('.badge')!.textContent).toBe('renamed')
    expect(within(list).queryByRole('button', { name: 'Mina marries' })).toBeNull()

    fireEvent.click(within(list).getByRole('button', { name: 'Show older checkpoints' }))
    await within(list).findByRole('button', { name: 'Mina marries' })
    expect(within(list).getByRole('button', { name: 'Add Mina' }).closest('li')!.querySelector('.badge')!.textContent).toBe('added')
    expect(calls).toContain('/api/history?entity=E-MINA&universe=dracula&limit=20&before=c3cccccccc')
    expect(within(list).queryByRole('button', { name: 'Show older checkpoints' })).toBeNull()
  })

  it('puts the uncommitted changes on top, and shows them first', async () => {
    const { calls } = stubApi({ override: historyApi() })
    renderApp('/u/dracula/e/E-MINA?view=history')

    const top = await screen.findByRole('button', { name: 'Uncommitted changes' })
    expect(top.closest('li')!.getAttribute('aria-current')).toBe('true')
    await screen.findByText('an edit not yet checkpointed')
    expect(screen.getByRole('heading', { name: 'Uncommitted changes, since the latest checkpoint' })).toBeTruthy()
    expect(calls).toContain('/api/history/diff?entity=E-MINA&universe=dracula')
  })

  it('without uncommitted changes, shows the latest checkpoint’s own change', async () => {
    stubApi({ override: historyApi({ uncommitted: false }) })
    renderApp('/u/dracula/e/E-MINA?view=history')

    await screen.findByText('what c5aaaaaaaa changed')
    expect(screen.queryByRole('button', { name: 'Uncommitted changes' })).toBeNull()
  })

  it('selects a checkpoint to see what it changed', async () => {
    const { calls } = stubApi({ override: historyApi() })
    const { history } = renderApp('/u/dracula/e/E-MINA?view=history&at=dracula:5')

    fireEvent.click(await screen.findByRole('button', { name: 'Fix a typo' }))
    await screen.findByText('what c3cccccccc changed')
    expect(search(history).get('checkpoint')).toBe('c3cccccccc')
    expect(search(history).get('at')).toBe('dracula:5')
    expect(screen.getByRole('heading', { name: 'What “Fix a typo” changed' })).toBeTruthy()
    expect(calls).toContain('/api/history/diff?entity=E-MINA&universe=dracula&checkpoint=c3cccccccc')
    // History isn't a story view: the history endpoints are never asked as of a position.
    expect(calls.filter((c) => c.startsWith('/api/history')).some((c) => c.includes('asOf'))).toBe(false)
  })

  it('compares two checkpoints, older as from and newer as to', async () => {
    const { calls } = stubApi({ override: historyApi() })
    const { history } = renderApp('/u/dracula/e/E-MINA?view=history')

    // Ticked newer first: the order of ticking doesn't matter.
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Compare Mina keeps a journal' }))
    fireEvent.click(screen.getByRole('checkbox', { name: 'Compare Fix a typo' }))

    await screen.findByText('from c3cccccccc to c5aaaaaaaa')
    expect(search(history).get('from')).toBe('c3cccccccc')
    expect(search(history).get('to')).toBe('c5aaaaaaaa')
    expect(screen.getByRole('heading', { name: 'From “Fix a typo” to “Mina keeps a journal”' })).toBeTruthy()
    expect(calls).toContain('/api/history/diff?entity=E-MINA&universe=dracula&from=c3cccccccc&to=c5aaaaaaaa')
    expect((screen.getByRole('checkbox', { name: 'Compare Fix a typo' }) as HTMLInputElement).checked).toBe(true)

    // Unticking one goes back to the default view.
    fireEvent.click(screen.getByRole('checkbox', { name: 'Compare Fix a typo' }))
    await waitFor(() => expect(search(history).get('from')).toBeNull())
  })

  it('compares a checkpoint with the note as it is now: from alone', async () => {
    stubApi({ override: historyApi() })
    const { history } = renderApp('/u/dracula/e/E-MINA?view=history')

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Compare Rename Mina' }))
    fireEvent.click(screen.getByRole('checkbox', { name: 'Compare the note as it is now' }))
    await screen.findByText('from c4bbbbbbbb to now')
    expect(search(history).get('from')).toBe('c4bbbbbbbb')
    expect(search(history).get('to')).toBeNull()
  })

  it('says kindly when the den has no history, and how to start one', async () => {
    stubApi({ override: historyApi({ available: false }) })
    renderApp('/u/dracula/e/E-MINA?view=history')

    const banner = await screen.findByText(/isn't in a git repository/)
    const box = banner.closest('.banner')!
    expect(box.textContent).toContain('git init')
    expect(box.textContent).toContain('wolfden init --git')
    expect(screen.queryByRole('heading', { name: 'Checkpoints' })).toBeNull()
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('keeps a note the reader hasn’t met hidden, until the writer asks', async () => {
    const { calls } = stubApi({ override: historyApi() })
    renderApp('/u/dracula/e/E-QUINCEY?view=history&at=dracula:3')

    const banner = await screen.findByText(/Readers first meet/)
    expect(banner.textContent).toBe('Readers first meet Quincey P. Morris in Dracula chapter 5.')
    expect(calls.some((c) => c.startsWith('/api/history'))).toBe(false)

    fireEvent.click(screen.getByRole('button', { name: 'Show its history anyway' }))
    await screen.findByRole('heading', { name: 'Checkpoints' })
    expect(calls).toContain('/api/history?entity=E-QUINCEY&universe=dracula&limit=20')
  })
})

describe('history helpers', () => {
  it('reads the history search params, taking to only with from', () => {
    expect(validateEntitySearch({})).toEqual({})
    expect(validateEntitySearch({ checkpoint: 'abc' })).toEqual({})
    expect(validateEntitySearch({ view: 'history', checkpoint: ' abc ' })).toEqual({ view: 'history', checkpoint: 'abc' })
    expect(validateEntitySearch({ view: 'history', to: 'b' })).toEqual({ view: 'history' })
    expect(validateEntitySearch({ view: 'history', from: 'a', to: 'b', checkpoint: 'c' })).toEqual({ view: 'history', from: 'a', to: 'b' })
  })

  it('chooses the uncommitted changes, else the latest checkpoint, unless the URL says', () => {
    expect(chooseSelection({ view: 'history' }, true, allEntries, false)).toEqual({})
    expect(chooseSelection({ view: 'history' }, false, allEntries, false)).toEqual({ checkpoint: 'c5aaaaaaaa' })
    expect(chooseSelection({ view: 'history' }, false, [], false)).toBeNull()
    expect(chooseSelection({ view: 'history' }, false, allEntries, true)).toBeNull()
    expect(chooseSelection({ view: 'history', from: 'a' }, true, allEntries, true)).toEqual({ from: 'a', to: undefined })
  })

  it('says when in words', () => {
    const now = Date.parse('2026-10-26T12:00:00Z')
    expect(relativeTime('2026-10-26T11:59:50Z', now)).toBe('just now')
    expect(relativeTime('2026-10-26T09:00:00Z', now)).toMatch(/3 hours ago/)
    expect(relativeTime('not a date', now)).toBe('not a date')
  })
})
