// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { describeHeaders, pairChanges, parsePatch, wordDiff } from './diff'
import { FileDiffView } from './components/DiffView'
import { chooseSelection, relativeTime, resolveSelection, selectionTitle } from './pages/HistoryPage'
import { validateEntitySearch } from './entitySearch'
import { historyDiffQuery, isCommitId, type FileDiff, type HistoryDiffResult, type HistoryEntry, type HistoryResult } from './historyApi'
import { entities, page, renderApp, stubApi } from './test/harness'

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

describe('SC-WEB-22: a note’s diff, read as a unified diff', () => {
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

  it('names added and removed lines for a screen reader', () => {
    const { container } = render(<FileDiffView file={{ change: { kind: 'modified', path: 'notes/mina.md', oldPath: null }, patch: PATCH }} />)
    const removed = container.querySelector('.diff-removed .diff-mark')!
    const added = container.querySelector('.diff-added .diff-mark')!
    expect(removed.getAttribute('aria-hidden')).toBeNull()
    expect(removed.querySelector('.visually-hidden')!.textContent).toBe('Removed')
    expect(added.querySelector('.visually-hidden')!.textContent).toBe('Added')
    expect(added.querySelector('[aria-hidden="true"]')!.textContent).toBe('+')
    expect(container.querySelector('.diff-context .diff-mark')!.textContent).toBe('')
  })

  it('keeps a "\\" line with no line before it, rather than dropping it', () => {
    const patch = 'diff --git a/n.md b/n.md\n--- a/n.md\n+++ b/n.md\n@@ -1 +1 @@\n\\ No newline at end of file\n-a\n+b\n'
    const lines = parsePatch(patch)[0].hunks[0].lines
    expect(lines.map((l) => [l.kind, l.text])).toEqual([
      ['note', '\\ No newline at end of file'],
      ['removed', 'a'],
      ['added', 'b'],
    ])

    render(<FileDiffView file={{ change: { kind: 'modified', path: 'n.md', oldPath: null }, patch }} />)
    expect(screen.getByText('\\ No newline at end of file')).toBeTruthy()
  })

  it('shows NEL, line separators, and paragraph separators as marks', () => {
    const [nel, ls, ps] = [0x85, 0x2028, 0x2029].map((c) => String.fromCodePoint(c))
    const patch = `diff --git a/n.md b/n.md\n--- a/n.md\n+++ b/n.md\n@@ -0,0 +1 @@\n+a${nel}b${ls}c${ps}d\n`
    const { container } = render(<FileDiffView file={{ change: { kind: 'added', path: 'n.md', oldPath: null }, patch }} />)
    const marks = [...container.querySelectorAll('.diff-cr')]
    expect(marks.map((m) => m.textContent)).toEqual(['NEL', 'LS', 'PS'])
    expect(marks.map((m) => m.getAttribute('aria-label'))).toEqual(['next line (U+0085)', 'line separator (U+2028)', 'paragraph separator (U+2029)'])
    expect(container.querySelector('.diff-added .diff-text')!.textContent).toBe('aNELbLScPSd')
  })

  it('puts git’s mode and empty-file headers into words', () => {
    const typeChange = (from: string, to: string) => [
      { headers: ['diff --git a/n.md b/n.md', `deleted file mode ${from}`], hunks: [] },
      { headers: ['diff --git a/n.md b/n.md', `new file mode ${to}`], hunks: [] },
    ]
    expect(describeHeaders(typeChange('100644', '120000'))).toEqual(['Became a link'])
    expect(describeHeaders(typeChange('120000', '100644'))).toEqual(['Was a link'])
    expect(describeHeaders(parsePatch('diff --git a/n.md b/n.md\nold mode 100644\nnew mode 100755\n'))).toEqual(['Permissions changed'])
    expect(describeHeaders(parsePatch('diff --git a/n.md b/n.md\nnew file mode 100644\nindex 0000000..e69de29\n'))).toEqual(['Empty file'])
    expect(describeHeaders(parsePatch('diff --git a/n.md b/n.md\nnew file mode 120000\nindex 0000000..1111111\n--- /dev/null\n+++ b/n.md\n@@ -0,0 +1 @@\n+target.md\n\\ No newline at end of file\n'))).toEqual(['A link'])

    render(<FileDiffView file={{ change: { kind: 'added', path: 'n.md', oldPath: null }, patch: 'diff --git a/n.md b/n.md\nnew file mode 100644\nindex 0000000..e69de29\n' }} />)
    expect(screen.getByText('Empty file')).toBeTruthy()
    expect(screen.queryByText(/new file mode/)).toBeNull()
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
      // As git resolves a revision: a full or short id, or HEAD~1.
      const resolve = (rev: string) => (rev === 'HEAD~1' ? allEntries[1].id : allEntries.find((e) => e.id.startsWith(rev))?.id)
      const refused = [checkpoint, from, to].find((rev) => rev !== null && resolve(rev) === undefined)
      if (refused !== undefined) {
        return new Response(JSON.stringify({ code: 'unknown_checkpoint', message: 'There is no such checkpoint.' }), { status: 404 })
      }

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

      const resolved = checkpoint
        ? { from: allEntries[allEntries.findIndex((e) => e.id === resolve(checkpoint)) + 1]?.id ?? null, to: resolve(checkpoint)! }
        : { from: from ? resolve(from)! : allEntries[0].id, to: to ? resolve(to)! : null }
      return { version: 1, entity: mina, backend: 'git', available: true, reason: null, diff: { ...resolved, files } } satisfies HistoryDiffResult
    }

    return undefined
  }
}

const search = (history: { location: { search: string } }) => new URLSearchParams(history.location.search)

describe('SC-WEB-23: I look at a note’s history in the browser', () => {
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
    const { calls } = stubApi({
      override: historyApi(),
      entityPage: (key, at) => ({ version: 1, view: at ? 'canon_as_of' : 'full', position: at, inView: true, note: null, entity: page({ ...mina, key, id: null, path: key }) }),
    })
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
    expect(first.querySelector('time')!.getAttribute('title')).toMatch(/^Written /)
    expect(list.textContent).toContain('Dates are when each change was written.')
    expect(list.textContent).not.toMatch(/author date|git's/)
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
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Compare Mina keeps a journal (c5aaaaa)' }))
    fireEvent.click(screen.getByRole('checkbox', { name: 'Compare Fix a typo (c3ccccc)' }))

    await screen.findByText('from c3cccccccc to c5aaaaaaaa')
    expect(search(history).get('from')).toBe('c3cccccccc')
    expect(search(history).get('to')).toBe('c5aaaaaaaa')
    expect(screen.getByRole('heading', { name: 'From “Fix a typo” to “Mina keeps a journal”' })).toBeTruthy()
    expect(calls).toContain('/api/history/diff?entity=E-MINA&universe=dracula&from=c3cccccccc&to=c5aaaaaaaa')
    expect((screen.getByRole('checkbox', { name: 'Compare Fix a typo (c3ccccc)' }) as HTMLInputElement).checked).toBe(true)

    // Unticking one goes back to the default view.
    fireEvent.click(screen.getByRole('checkbox', { name: 'Compare Fix a typo (c3ccccc)' }))
    await waitFor(() => expect(search(history).get('from')).toBeNull())
  })

  it('compares a checkpoint with the note as it is now: from alone', async () => {
    stubApi({ override: historyApi() })
    const { history } = renderApp('/u/dracula/e/E-MINA?view=history')

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Compare Rename Mina (c4bbbbb)' }))
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

    fireEvent.click(screen.getByRole('button', { name: 'Show it anyway' }))
    const heading = await screen.findByRole('heading', { name: 'Checkpoints' })
    expect(calls).toContain('/api/history?entity=E-QUINCEY&universe=dracula&limit=20')
    // Greyed as a note shown anyway is, with the breadcrumb and tabs from the full view.
    const revealed = heading.closest('.revealed')!
    expect(revealed).toBeTruthy()
    expect(revealed.querySelector('.page-body')).toBeNull()
    await within(revealed as HTMLElement).findByRole('navigation', { name: 'Where this is filed' })
  })

  it('shares "Show it anyway" between the Note and History tabs', async () => {
    stubApi({ override: historyApi() })
    const { history } = renderApp('/u/dracula/e/E-QUINCEY?at=dracula:3')

    fireEvent.click(await screen.findByRole('button', { name: 'Show it anyway' }))
    const tabs = await screen.findByRole('navigation', { name: 'Note views' })
    fireEvent.click(within(tabs).getByRole('link', { name: 'History' }))

    await screen.findByRole('heading', { name: 'Checkpoints' })
    expect(search(history).get('view')).toBe('history')
    expect(screen.getByText(/Readers first meet/)).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Show it anyway' })).toBeNull()

    // And back: the note is still shown anyway.
    fireEvent.click(within(await screen.findByRole('navigation', { name: 'Note views' })).getByRole('link', { name: 'Note' }))
    await screen.findByRole('heading', { level: 1, name: 'Quincey P. Morris' })
    expect(screen.queryByRole('button', { name: 'Show it anyway' })).toBeNull()
  })

  it('hides a revealed history again after leaving the note and coming Back, or changing the position', async () => {
    const { calls } = stubApi({ override: historyApi() })
    const { history } = renderApp('/u/dracula/e/E-QUINCEY?view=history&at=dracula:3')

    fireEvent.click(await screen.findByRole('button', { name: 'Show it anyway' }))
    await screen.findByRole('heading', { name: 'Checkpoints' })

    history.push('/u/dracula/e/E-LUCY?view=history&at=dracula:3')
    await screen.findByText('Lucy Westenra')
    expect(screen.queryByRole('heading', { name: 'Checkpoints' })).toBeNull()

    history.back()
    await screen.findByText('Quincey P. Morris')
    expect(screen.queryByRole('heading', { name: 'Checkpoints' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Show it anyway' })).toBeTruthy()

    fireEvent.click(screen.getByRole('button', { name: 'Show it anyway' }))
    await screen.findByRole('heading', { name: 'Checkpoints' })
    history.replace('/u/dracula/e/E-QUINCEY?view=history&at=dracula:4')
    await screen.findByRole('button', { name: 'Show it anyway' })
    expect(screen.queryByRole('heading', { name: 'Checkpoints' })).toBeNull()
    expect(calls.filter((c) => c.startsWith('/api/history?entity=E-LUCY'))).toEqual([])
  })

  it('doesn’t carry a compare tick over to another note', async () => {
    stubApi({ override: historyApi() })
    const { history } = renderApp('/u/dracula/e/E-MINA?view=history')

    const box = await screen.findByRole('checkbox', { name: 'Compare Fix a typo (c3ccccc)' })
    fireEvent.click(box)
    expect((box as HTMLInputElement).checked).toBe(true)

    history.push('/u/dracula/e/E-JONATHAN?view=history')
    // Jonathan's page has its own breadcrumb trail; wait for it, then for his list.
    await screen.findByRole('heading', { level: 1, name: 'Mina Harker' })
    await waitFor(() => expect(document.title).toContain('Jonathan Harker'))
    const other = await screen.findByRole('checkbox', { name: 'Compare Fix a typo (c3ccccc)' })
    expect((other as HTMLInputElement).checked).toBe(false)
    expect(screen.getAllByRole('checkbox').some((b) => (b as HTMLInputElement).checked)).toBe(false)
  })

  it('finds the rows a short id or HEAD~1 in the URL names, after a reload', async () => {
    stubApi({ override: historyApi() })
    renderApp('/u/dracula/e/E-MINA?view=history&checkpoint=c3cc')

    await screen.findByText('what c3cc changed')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Fix a typo' }).closest('li')!.getAttribute('aria-current')).toBe('true'))
    expect(screen.getByRole('heading', { name: 'What “Fix a typo” changed' })).toBeTruthy()
    cleanup()

    renderApp('/u/dracula/e/E-MINA?view=history&from=c3cc&to=HEAD~1')
    await screen.findByText('from c3cc to HEAD~1')
    await waitFor(() => expect((screen.getByRole('checkbox', { name: 'Compare Rename Mina (c4bbbbb)' }) as HTMLInputElement).checked).toBe(true))
    expect((screen.getByRole('checkbox', { name: 'Compare Fix a typo (c3ccccc)' }) as HTMLInputElement).checked).toBe(true)
    expect(screen.getByRole('heading', { name: 'From “Fix a typo” to “Rename Mina”' })).toBeTruthy()
  })

  it('titles a checkpoint the API refuses "This change", never echoing the URL', async () => {
    stubApi({ override: historyApi() })
    renderApp('/u/dracula/e/E-MINA?view=history&checkpoint=%3Cimg%20src%3Dx%3Enasty')

    await screen.findByRole('alert')
    expect(screen.getByRole('heading', { level: 2, name: 'This change' })).toBeTruthy()
    expect(document.querySelector('img')).toBeNull()
  })

  it('announces only a short status line, not the whole diff', async () => {
    stubApi({ override: historyApi() })
    const { container } = renderApp('/u/dracula/e/E-MINA?view=history')

    await screen.findByText('an edit not yet checkpointed')
    const pane = container.querySelector('.diff-pane')!
    expect(pane.closest('[aria-live]')).toBeNull()
    const live = [...pane.querySelectorAll('[aria-live]')]
    expect(live.map((l) => l.textContent)).toEqual(['Showing: Uncommitted changes, since the latest checkpoint'])
    expect(live[0].querySelector('table')).toBeNull()
  })

  it('asks for the uncommitted changes once, and a checkpoint’s diff only once', async () => {
    const { calls } = stubApi({ override: historyApi() })
    renderApp('/u/dracula/e/E-MINA?view=history')

    await screen.findByText('an edit not yet checkpointed')
    expect(calls.filter((c) => c === '/api/history/diff?entity=E-MINA&universe=dracula')).toHaveLength(1)

    const full = 'c3cccccccc'.padEnd(40, '0')
    expect(historyDiffQuery('dracula', 'E-MINA', { checkpoint: full }).staleTime).toBe(Infinity)
    expect(historyDiffQuery('dracula', 'E-MINA', { from: full, to: full }).staleTime).toBe(Infinity)
    expect(historyDiffQuery('dracula', 'E-MINA', { checkpoint: 'HEAD~1' }).staleTime).not.toBe(Infinity)
    expect(historyDiffQuery('dracula', 'E-MINA', { from: full }).staleTime).not.toBe(Infinity)
    expect(historyDiffQuery('dracula', 'E-MINA', {}).staleTime).toBeGreaterThan(0)
    expect(isCommitId(full)).toBe(true)
    expect(isCommitId('c3ccccc')).toBe(false)
  })

  it('keeps the breadcrumb and tabs in the same place on both tabs', async () => {
    stubApi({ override: historyApi() })
    renderApp('/u/dracula/e/E-MINA')
    const order = async () => {
      const crumb = await screen.findByRole('navigation', { name: 'Where this is filed' })
      const tabs = await screen.findByRole('navigation', { name: 'Note views' })
      const h1 = await screen.findByRole('heading', { level: 1 })
      return [crumb.compareDocumentPosition(tabs), tabs.compareDocumentPosition(h1)]
    }

    const onNote = await order()
    fireEvent.click(within(screen.getByRole('navigation', { name: 'Note views' })).getByRole('link', { name: 'History' }))
    await screen.findByRole('heading', { name: 'Checkpoints' })
    expect(await order()).toEqual(onNote)
    expect(onNote).toEqual([Node.DOCUMENT_POSITION_FOLLOWING, Node.DOCUMENT_POSITION_FOLLOWING])
  })
})

describe('SC-WEB-23: history helpers', () => {
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

  it('names a selection by the ids the API resolved, never by the URL', () => {
    const result = (from: string | null, to: string | null): HistoryDiffResult => ({
      version: 1, entity: mina, backend: 'git', available: true, reason: null, diff: { from, to, files: [] },
    })
    expect(resolveSelection({ checkpoint: 'c3cc' }, result('c2dddddddd', 'c3cccccccc'))).toEqual({ checkpoint: 'c3cccccccc' })
    expect(resolveSelection({ from: 'c3cc' }, result('c3cccccccc', null))).toEqual({ from: 'c3cccccccc', to: null })
    expect(resolveSelection({ checkpoint: 'c3cc' }, undefined)).toEqual({})

    expect(selectionTitle({ checkpoint: 'zz' }, allEntries, {}, false)).toBe('This change')
    expect(selectionTitle({ checkpoint: 'zz' }, allEntries, {}, true)).toBe('This change')
    expect(selectionTitle({ checkpoint: 'tag' }, allEntries, { checkpoint: 'f00dfeed99' }, false)).toBe('What f00dfee changed')
    expect(selectionTitle({ from: 'c3cc' }, allEntries, { from: 'c3cccccccc', to: null }, false)).toBe('Since “Fix a typo”, up to now')
    expect(selectionTitle({}, allEntries, {}, false)).toBe('Uncommitted changes, since the latest checkpoint')
  })

  it('says when in words', () => {
    const now = Date.parse('2026-10-26T12:00:00Z')
    expect(relativeTime('2026-10-26T11:59:50Z', now)).toBe('just now')
    expect(relativeTime('2026-10-26T09:00:00Z', now)).toMatch(/3 hours ago/)
    expect(relativeTime('not a date', now)).toBe('not a date')
  })
})
