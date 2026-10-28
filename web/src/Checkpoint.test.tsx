// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import {
  CHECKPOINT_STATUS_KEY,
  groupChanges,
  invalidateCheckpointStatus,
  type CheckpointMade,
  type CheckpointStatus,
  type HistoryTurnedOn,
  type PendingChange,
  type TurnOnRequest,
  type TurnOnView,
} from './checkpointApi'
import { renderApp, stubApi } from './test/harness'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const MINA = 'universes/dracula/books/dracula/entities/characters/mina-harker.md'
const LUCY = 'universes/dracula/books/dracula/entities/characters/lucy-westenra.md'
const KRONE = 'universes/dracula/entities/locations/golden-krone.md'

function change(path: string, kind: PendingChange['kind'] = 'modified', savedHere = false): PendingChange {
  return { kind, path, oldPath: null, universe: path.startsWith('universes/') ? path.split('/')[1] : null, savedHere }
}

function status(extra: Partial<CheckpointStatus> = {}): CheckpointStatus {
  const changes = extra.changes ?? [change(MINA, 'modified', true), change(LUCY), change(KRONE, 'added')]
  return {
    version: 1,
    backend: 'git',
    available: true,
    reason: null,
    enable: null,
    latest: { id: 'c1aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', when: '2026-10-26T20:00:00Z' },
    inProgress: null,
    count: changes.length + (extra.otherUniverses ?? 0),
    changes,
    otherUniverses: 0,
    autoCheckpoint: { every: null, problem: null, runs: true },
    ...extra,
  }
}

const NO_HISTORY: CheckpointStatus = {
  ...status({ changes: [] }),
  backend: 'none',
  available: false,
  reason: "This den has no history, so a checkpoint isn't available. Your edits are still saved in the files.",
  enable: "To keep history, turn it on: with Turn on history in the checkpoint dialog, or by running 'wolfden init --git' in the den's folder.",
  latest: null,
  count: 0,
}

/** Answers /api/checkpoint with `current` for GET, and `post` for POST; records POST bodies. */
function stubCheckpoints(current: () => CheckpointStatus, post?: (body: { message: string }) => Response | CheckpointMade) {
  const posted: { message: string }[] = []
  const api = stubApi({
    override: (path, _params, init) => {
      if (path !== '/api/checkpoint') {
        return undefined
      }

      if (init?.method === 'POST') {
        expect(new Headers(init.headers).get('Content-Type')).toBe('application/json')
        const body = JSON.parse(String(init.body)) as { message: string }
        posted.push(body)
        return post?.(body)
      }

      return current()
    },
  })
  return { ...api, posted }
}

describe('SC-WEB-50: the top bar says what changed since the last checkpoint', () => {
  it('is hidden when nothing has changed', async () => {
    const { calls } = stubApi()
    renderApp('/u/dracula')
    await screen.findByRole('heading', { name: 'Dracula', level: 1 })
    await waitFor(() => expect(calls).toContain('/api/checkpoint'))
    expect(screen.queryByRole('button', { name: /checkpoint/i })).toBeNull()
  })

  it('counts the changed notes and opens a dialog that groups them for review', async () => {
    stubCheckpoints(() => status())
    renderApp('/u/dracula')

    const indicator = await screen.findByRole('button', { name: /3 notes changed since the last checkpoint · Checkpoint…/ })
    expect(indicator.getAttribute('aria-haspopup')).toBe('dialog')
    fireEvent.click(indicator)

    const dialog = await screen.findByRole('dialog', { name: 'Make a checkpoint' })
    expect(dialog.getAttribute('aria-modal')).toBe('true')

    // Grouped by universe, then by the book the notes are filed under, paths shortened within the group.
    const groups = within(dialog).getAllByRole('heading', { level: 3 }).map((h) => h.textContent)
    expect(groups).toEqual(['Universe dracula', 'Universe dracula · Book dracula'])
    expect(within(dialog).getByText('entities/locations/golden-krone.md')).toBeTruthy()
    expect(within(dialog).getByText('entities/characters/mina-harker.md')).toBeTruthy()

    // A note saved through Wolfden is marked; the others changed another way.
    const mina = within(dialog).getByText('entities/characters/mina-harker.md').closest('li')!
    expect(within(mina).getByText('saved in Wolfden')).toBeTruthy()
    const lucy = within(dialog).getByText('entities/characters/lucy-westenra.md').closest('li')!
    expect(within(lucy).queryByText('saved in Wolfden')).toBeNull()
    expect(within(dialog).getByText(/changed another way/)).toBeTruthy()

    // The message box has focus, and no message is made up for the writer.
    const box = within(dialog).getByLabelText('Name this checkpoint') as HTMLTextAreaElement
    expect(document.activeElement).toBe(box)
    expect(box.value).toBe('')
  })

  it('says how many notes are new before the first checkpoint', async () => {
    stubCheckpoints(() => status({ latest: null }))
    renderApp('/u/dracula')
    expect(await screen.findByRole('button', { name: /3 notes not checkpointed yet/ })).toBeTruthy()
  })

  it('counts, but doesn’t name, changes in universes a pinned server doesn’t show', async () => {
    stubCheckpoints(() => status({ changes: [change(MINA)], otherUniverses: 2 }))
    renderApp('/u/dracula')
    fireEvent.click(await screen.findByRole('button', { name: /3 notes changed/ }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/And 2 files in other universes/)).toBeTruthy()
  })

  it('looks again when the editor says it saved, and when the window gets focus back', async () => {
    let current = status({ changes: [] })
    const { calls } = stubCheckpoints(() => current)
    const { queryClient } = renderApp('/u/dracula')
    await waitFor(() => expect(calls.filter((c) => c === '/api/checkpoint')).toHaveLength(1))
    expect(screen.queryByRole('button', { name: /changed since/ })).toBeNull()

    current = status({ changes: [change(MINA)] })
    await invalidateCheckpointStatus(queryClient)
    expect(await screen.findByRole('button', { name: /1 note changed since the last checkpoint/ })).toBeTruthy()
    expect(queryClient.getQueryState(CHECKPOINT_STATUS_KEY)?.data).toBeDefined()
  })
})

describe('SC-WEB-52: I name a checkpoint after a notes pass', () => {
  it('needs a name, then makes the checkpoint and says so', async () => {
    let current = status()
    const { posted, calls } = stubCheckpoints(
      () => current,
      (body) => {
        const made = { id: 'c2bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', when: '2026-10-27T09:00:00Z', author: 'Test Writer', message: body.message, files: 3 }
        current = status({ changes: [], latest: { id: made.id, when: made.when } })
        return {
          version: 1,
          backend: 'git',
          recorded: true,
          checkpoint: made,
          inProgress: null,
          changes: [change(MINA), change(LUCY), change(KRONE, 'added')],
          otherUniverses: 0,
        }
      },
    )
    renderApp('/u/dracula')
    fireEvent.click(await screen.findByRole('button', { name: /3 notes changed/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Make a checkpoint' })

    // An empty name is refused here, before anything is sent.
    fireEvent.click(within(dialog).getByRole('button', { name: 'Checkpoint' }))
    expect(within(dialog).getByRole('alert').textContent).toMatch(/Give the checkpoint a name/)
    const box = within(dialog).getByLabelText('Name this checkpoint')
    expect(box.getAttribute('aria-invalid')).toBe('true')
    expect(posted).toEqual([])

    fireEvent.change(box, { target: { value: '  Notes pass after drafting ch 12 ' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Checkpoint' }))

    const done = await screen.findByRole('dialog', { name: 'Checkpoint made' })
    expect(posted).toEqual([{ message: 'Notes pass after drafting ch 12' }])
    expect(within(done).getByRole('status').textContent).toMatch(/Recorded “Notes pass after drafting ch 12”, with 3 notes/)
    expect(document.activeElement).toBe(within(done).getByRole('button', { name: 'Close' }))

    // The indicator asked again, and is hidden now that nothing has changed.
    await waitFor(() => expect(calls.filter((c) => c === '/api/checkpoint').length).toBeGreaterThanOrEqual(3))
    fireEvent.click(within(done).getByRole('button', { name: 'Close' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(screen.queryByRole('button', { name: /changed since/ })).toBeNull()
  })

  it('shows why git refused, and keeps the name to try again', async () => {
    stubCheckpoints(
      () => status(),
      () =>
        new Response(
          JSON.stringify({
            code: 'history_failed',
            message: 'Git refused the checkpoint, so nothing was recorded (spell check failed). A pre-commit or commit-msg hook can do this.',
          }),
          { status: 500, headers: { 'Content-Type': 'application/json' } },
        ),
    )
    renderApp('/u/dracula')
    fireEvent.click(await screen.findByRole('button', { name: /3 notes changed/ }))
    const dialog = await screen.findByRole('dialog')
    const box = within(dialog).getByLabelText('Name this checkpoint') as HTMLTextAreaElement
    fireEvent.change(box, { target: { value: 'Planning session: harbour subplot' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Checkpoint' }))

    const alert = await within(dialog).findByRole('alert')
    expect(alert.textContent).toMatch(/Git refused the checkpoint.*hook/)
    expect(box.value).toBe('Planning session: harbour subplot')
    expect(within(dialog).getByRole('button', { name: 'Checkpoint' })).toBeTruthy()
  })

  it('says when there was nothing to record', async () => {
    stubCheckpoints(
      () => status(),
      () => ({ version: 1, backend: 'git', recorded: false, checkpoint: null, inProgress: null, changes: [], otherUniverses: 0 }),
    )
    renderApp('/u/dracula')
    fireEvent.click(await screen.findByRole('button', { name: /3 notes changed/ }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.change(within(dialog).getByLabelText('Name this checkpoint'), { target: { value: 'Again' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Checkpoint' }))
    expect((await within(await screen.findByRole('dialog', { name: 'Nothing to checkpoint' })).findByRole('status')).textContent).toMatch(/Nothing had changed since the last checkpoint/)
  })

  it('closes on Escape and gives focus back to the indicator', async () => {
    stubCheckpoints(() => status())
    renderApp('/u/dracula')
    const indicator = await screen.findByRole('button', { name: /3 notes changed/ })
    fireEvent.click(indicator)
    const dialog = await screen.findByRole('dialog')
    fireEvent.keyDown(dialog, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    await waitFor(() => expect(document.activeElement).toBe(indicator))
  })

  it('keeps focus in the dialog while the checkpoint is made, and gives it back to the box after an error', async () => {
    let answer: (r: Response) => void = () => {}
    stubCheckpoints(
      () => status(),
      () => new Promise<Response>((resolve) => (answer = resolve)) as unknown as Response,
    )
    renderApp('/u/dracula')
    fireEvent.click(await screen.findByRole('button', { name: /3 notes changed/ }))
    const dialog = await screen.findByRole('dialog')
    const box = within(dialog).getByLabelText('Name this checkpoint')
    fireEvent.change(box, { target: { value: 'Notes pass after drafting ch 12' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Checkpoint' }))

    await within(dialog).findByRole('button', { name: 'Making the checkpoint…' })
    await waitFor(() => expect(document.activeElement).toBe(dialog))
    answer(new Response(JSON.stringify({ code: 'history_failed', message: 'Git hasn’t finished the checkpoint after 2 minutes.' }), { status: 500 }))

    await within(dialog).findByRole('alert')
    await waitFor(() => expect(document.activeElement).toBe(box))
  })

  it('asks before a click outside drops a typed name, and closes at once without one', async () => {
    stubCheckpoints(() => status())
    renderApp('/u/dracula')
    fireEvent.click(await screen.findByRole('button', { name: /3 notes changed/ }))
    let dialog = await screen.findByRole('dialog')
    const backdrop = dialog.parentElement!
    fireEvent.change(within(dialog).getByLabelText('Name this checkpoint'), { target: { value: 'Planning session: harbour subplot' } })

    fireEvent.mouseDown(backdrop)
    const ask = within(dialog).getByRole('alertdialog')
    expect(ask.textContent).toMatch(/Close without making the checkpoint/)
    fireEvent.click(within(ask).getByRole('button', { name: 'Keep it open' }))
    expect((within(dialog).getByLabelText('Name this checkpoint') as HTMLTextAreaElement).value).toBe('Planning session: harbour subplot')

    fireEvent.mouseDown(backdrop)
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close without a checkpoint' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())

    fireEvent.click(screen.getByRole('button', { name: /3 notes changed/ }))
    dialog = await screen.findByRole('dialog')
    fireEvent.mouseDown(dialog.parentElement!)
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  })

  it('warns when git is in the middle of something', async () => {
    stubCheckpoints(() => status({ inProgress: 'a rebase' }))
    renderApp('/u/dracula')
    fireEvent.click(await screen.findByRole('button', { name: /3 notes changed/ }))
    expect((await screen.findByRole('dialog')).textContent).toMatch(/Git is in the middle of a rebase/)
  })

  it('mentions automatic checkpoints when they’re on', async () => {
    stubCheckpoints(() => status({ autoCheckpoint: { every: '30m', problem: null, runs: true } }))
    renderApp('/u/dracula')
    fireEvent.click(await screen.findByRole('button', { name: /3 notes changed/ }))
    expect((await screen.findByRole('dialog')).textContent).toMatch(/Automatic checkpoints are on: Wolfden makes one after 30m without changes/)
  })
})

describe('SC-WEB-51: without history, the indicator says how to turn it on', () => {
  it('on a server that can’t turn it on, says how to from the command line instead of offering a checkpoint', async () => {
    const { posted } = stubCheckpoints(() => NO_HISTORY)
    renderApp('/u/dracula')
    fireEvent.click(await screen.findByRole('button', { name: /No history · Turn on…/ }))

    const dialog = await screen.findByRole('dialog', { name: 'History is off' })
    expect(dialog.textContent).toMatch(/wolfden init --git/)
    expect(within(dialog).queryByRole('button', { name: 'Turn on history' })).toBeNull()
    expect(dialog.textContent).toMatch(/still saved in the files/)
    expect(within(dialog).queryByLabelText('Name this checkpoint')).toBeNull()
    expect(within(dialog).queryByRole('button', { name: 'Checkpoint' })).toBeNull()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(posted).toEqual([])
  })
})

const TURNED_ON: HistoryTurnedOn = {
  version: 1,
  backend: 'git',
  steps: [
    "Made the den's folder a git repository.",
    ".gitignore already leaves out .wolfden/.",
    'Made the first checkpoint, "Start of history", of 42 files.',
  ],
  repositoryMade: true,
  gitIgnore: 'present',
  identitySaved: [],
  checkpoint: { id: 'c0ffee0000000000000000000000000000000000', when: '2026-10-28T09:00:00Z', author: 'Test Writer', message: 'Start of history', files: 42 },
}

/** A den without history on a server that can turn it on; `turnOn` overrides what git still needs. */
function noHistory(turnOn: Partial<TurnOnView> = {}): CheckpointStatus {
  return { ...NO_HISTORY, turnOn: { possible: true, reason: null, needsName: false, needsEmail: false, ...turnOn } }
}

/** Answers /api/checkpoint with `current`, and /api/history/turn-on with `answer`; records what was sent to turn it on. */
function stubTurnOn(current: () => CheckpointStatus, answer: (body: TurnOnRequest) => Response | HistoryTurnedOn = () => TURNED_ON) {
  const sent: TurnOnRequest[] = []
  const api = stubApi({
    override: (path, _params, init) => {
      if (path === '/api/history/turn-on') {
        expect(init?.method).toBe('POST')
        expect(new Headers(init?.headers).get('Content-Type')).toBe('application/json')
        const body = JSON.parse(String(init?.body)) as TurnOnRequest
        sent.push(body)
        return answer(body)
      }

      return path === '/api/checkpoint' ? current() : undefined
    },
  })
  return { ...api, sent }
}

async function openTurnOn() {
  renderApp('/u/dracula')
  fireEvent.click(await screen.findByRole('button', { name: /No history · Turn on…/ }))
  return screen.findByRole('dialog', { name: 'History is off' })
}

describe('SC-WEB-55: I turn on history from the checkpoint dialog', () => {
  it('turns it on with a first checkpoint, says what it did, and the indicator catches up', async () => {
    let current = noHistory()
    const { sent, calls } = stubTurnOn(
      () => current,
      () => {
        current = status({ changes: [], latest: { id: TURNED_ON.checkpoint!.id, when: TURNED_ON.checkpoint!.when } })
        return TURNED_ON
      },
    )
    const dialog = await openTurnOn()

    expect(dialog.textContent).toMatch(/synced folder/)
    expect(within(dialog).queryByLabelText('Name')).toBeNull()
    expect(within(dialog).queryByLabelText('Email')).toBeNull()
    const first = within(dialog).getByLabelText(/Make a first checkpoint, “Start of history”/) as HTMLInputElement
    expect(first.checked).toBe(true)
    const statusCalls = calls.filter((c) => c === '/api/checkpoint').length
    fireEvent.click(within(dialog).getByRole('button', { name: 'Turn on history' }))

    const done = await screen.findByRole('dialog', { name: 'History is on' })
    expect(sent).toEqual([{ firstCheckpoint: true }])
    expect(within(done).getByText('Made the first checkpoint, "Start of history", of 42 files.')).toBeTruthy()
    await waitFor(() => expect(calls.filter((c) => c === '/api/checkpoint').length).toBeGreaterThan(statusCalls))
    fireEvent.click(within(done).getByRole('button', { name: 'Close' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    await waitFor(() => expect(screen.queryByRole('button', { name: /No history/ })).toBeNull())
  })

  it('leaves out the first checkpoint when it’s unticked', async () => {
    const { sent } = stubTurnOn(() => noHistory())
    const dialog = await openTurnOn()

    fireEvent.click(within(dialog).getByLabelText(/Make a first checkpoint/))
    fireEvent.click(within(dialog).getByRole('button', { name: 'Turn on history' }))

    await screen.findByRole('dialog', { name: 'History is on' })
    expect(sent).toEqual([{ firstCheckpoint: false }])
  })

  it('asks for a name and email when git has none, and sends only those', async () => {
    const { sent } = stubTurnOn(() => noHistory({ needsName: true, needsEmail: true }))
    const dialog = await openTurnOn()

    expect(within(dialog).getByText(/a pen name included/)).toBeTruthy()
    expect(within(dialog).getByText(/can be a placeholder/)).toBeTruthy()
    const name = within(dialog).getByLabelText('Name') as HTMLInputElement
    const email = within(dialog).getByLabelText('Email') as HTMLInputElement
    expect(document.activeElement).toBe(name)

    // Nothing given: asked again, and nothing is sent.
    fireEvent.click(within(dialog).getByRole('button', { name: 'Turn on history' }))
    expect((await within(dialog).findByRole('alert')).textContent).toMatch(/Give a name/)
    expect(name.getAttribute('aria-invalid')).toBe('true')
    expect(sent).toEqual([])

    fireEvent.change(name, { target: { value: '  Ash Calloway ' } })
    fireEvent.change(email, { target: { value: 'ash@example.invalid' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Turn on history' }))

    await screen.findByRole('dialog', { name: 'History is on' })
    expect(sent).toEqual([{ firstCheckpoint: true, authorName: 'Ash Calloway', authorEmail: 'ash@example.invalid' }])
  })

  it('asks only for what’s missing', async () => {
    stubTurnOn(() => noHistory({ needsEmail: true }))
    const dialog = await openTurnOn()
    expect(within(dialog).queryByLabelText('Name')).toBeNull()
    expect(within(dialog).getByLabelText('Email')).toBeTruthy()
  })

  it('shows the server’s refusal, and asks for a name and email when git turns out to need them', async () => {
    const { sent } = stubTurnOn(
      () => noHistory(),
      () =>
        new Response(
          JSON.stringify({ code: 'identity_needed', message: 'Git records who made each checkpoint, and it has no name or email for this den.' }),
          { status: 400 },
        ),
    )
    const dialog = await openTurnOn()

    fireEvent.click(within(dialog).getByRole('button', { name: 'Turn on history' }))

    expect((await within(dialog).findByRole('alert')).textContent).toMatch(/has no name or email for this den/)
    expect(within(dialog).getByLabelText('Name')).toBeTruthy()
    expect(within(dialog).getByLabelText('Email')).toBeTruthy()
    expect(sent).toHaveLength(1)
  })

  it('says why when it can’t be turned on here, and offers no button', async () => {
    stubTurnOn(() =>
      noHistory({ possible: false, reason: "Git isn't installed, or Wolfden can't find it on the PATH, so history can't be turned on." }),
    )
    const dialog = await openTurnOn()

    expect(dialog.textContent).toMatch(/Git isn't installed/)
    expect(within(dialog).queryByRole('button', { name: 'Turn on history' })).toBeNull()
  })
})

describe('SC-WEB-50: when the status can’t be read', () => {
  it('shows a quiet "History unavailable" with the reason, rather than hiding', async () => {
    stubCheckpoints(
      () =>
        new Response(JSON.stringify({ code: 'history_failed', message: 'git diff failed: fatal: bad object' }), { status: 500 }) as unknown as CheckpointStatus,
    )
    renderApp('/u/dracula')
    fireEvent.click(await screen.findByRole('button', { name: 'History unavailable' }))
    const dialog = await screen.findByRole('dialog', { name: 'History unavailable' })
    expect(dialog.textContent).toMatch(/fatal: bad object/)
    fireEvent.keyDown(dialog, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  })
})

describe('grouping changes for review', () => {
  it('puts den-level files first, then each universe’s scopes in path order', () => {
    const groups = groupChanges([
      change('universes/starfall/series/long-dark/books/ember/outline.md'),
      change('universes/starfall/series/long-dark/entities/characters/mara-voss.md'),
      change('den.yaml'),
      change('universes/starfall/books/a-quiet-moon/book.md'),
      change('universes/starfall/universe.md'),
    ])
    expect(groups.map((g) => [g.title, g.changes.map((c) => c.shown)])).toEqual([
      ['Den settings and packs', ['den.yaml']],
      ['Universe starfall', ['universe.md']],
      ['Universe starfall · Book a-quiet-moon', ['book.md']],
      ['Universe starfall · Series long-dark', ['entities/characters/mara-voss.md']],
      ['Universe starfall · Series long-dark · Book ember', ['outline.md']],
    ])
  })
})
