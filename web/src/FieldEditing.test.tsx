// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor, within, configure } from '@testing-library/react'
import type { EntityPageResult, PageField, SearchResult } from './api'
import type { EntitySource, SourceFieldType, WriteResult } from './editApi'
import { fieldChanges, fieldDraftOf, fieldProblems, linkFor, nameMatches, newEntry, newItem, type FieldDraft, type Json } from './fieldModel'
import { entities, page, renderApp, stubApi } from './test/harness'

// Editing a note's fields in place (#28, part b): the changes worked out from
// the form, the controls for each field type, and saving one field.

// The first test of a file renders the app cold, which can take over a second
// when the machine is busy (the whole suite runs files in parallel).
configure({ asyncUtilTimeout: 5000 })
vi.setConfig({ testTimeout: 15_000 })

beforeAll(() => {
  // jsdom lays nothing out; CodeMirror (Edit file) measures text ranges.
  const rect = { x: 0, y: 0, width: 0, height: 0, top: 0, left: 0, right: 0, bottom: 0, toJSON: () => ({}) } as DOMRect
  Range.prototype.getBoundingClientRect = () => rect
  Range.prototype.getClientRects = () => ({ length: 0, item: () => null, [Symbol.iterator]: [][Symbol.iterator] }) as unknown as DOMRectList
  document.execCommand ??= () => false
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function type(name: string, t: SourceFieldType['type'], extra: Partial<SourceFieldType> = {}): SourceFieldType {
  return { name, type: t, list: false, temporal: false, required: false, values: [], to: null, description: null, default: null, ...extra }
}

const types = {
  label: type('label', 'string'),
  notes: type('notes', 'text'),
  weight: type('weight', 'number'),
  preserved: type('preserved', 'bool'),
  found: type('found', 'date'),
  state: type('state', 'enum', { values: ['fresh', 'dried', 'lost'] }),
  finder: type('finder', 'ref', { to: ['character'] }),
  seen: type('seen', 'position', { list: true }),
  tags: type('tags', 'string', { list: true }),
  keeper: type('keeper', 'ref', { to: ['character'], temporal: true }),
}

function draft(t: SourceFieldType, raw: Json | undefined): FieldDraft {
  const d = fieldDraftOf(t, raw)
  if (typeof d === 'string') {
    throw new Error(d)
  }

  return d
}

describe('SC-WEB-61: the changes a field edit sends', () => {
  it('sends nothing for an untouched field, and keeps an enum as written until it is changed', () => {
    const d = draft(types.state, 'Dried')
    expect(d.entries[0].value).toMatchObject({ kind: 'one', draft: { text: 'dried' } })
    expect(fieldChanges(types.state, 'Dried', d)).toEqual([])

    const v = d.entries[0].value as Extract<FieldDraft['entries'][0]['value'], { kind: 'one' }>
    v.draft = { ...v.draft, text: 'lost' }
    expect(fieldChanges(types.state, 'Dried', d)).toEqual([{ op: 'set', path: [], value: 'lost' }])
  })

  it('removes, adds, and moves list items by where they came from', () => {
    const before = ['soil', 'box', 'chapel']
    const d = draft(types.tags, before)
    const list = d.entries[0].value as Extract<FieldDraft['entries'][0]['value'], { kind: 'list' }>
    const [soil, box, chapel] = list.items

    // Chapel to the front, box out, a new item at the end, soil changed.
    const added = newItem()
    added.draft.text = 'Carfax'
    list.items = [chapel, { ...soil, draft: { ...soil.draft, text: 'earth' } }, added]
    expect(fieldChanges(types.tags, before, d)).toEqual([
      { op: 'remove', path: [1] },
      { op: 'remove', path: [1] },
      { op: 'add', path: [], value: 'chapel', index: 0 },
      { op: 'add', path: [], value: 'Carfax' },
      { op: 'set', path: [1], value: 'earth' },
    ])

    // Every item out: each is removed, so the file follows format.md F-RT-6.
    list.items = []
    expect(fieldChanges(types.tags, before, d)).toEqual([
      { op: 'remove', path: [2] },
      { op: 'remove', path: [1] },
      { op: 'remove', path: [0] },
    ])
    expect(fieldProblems(types.tags, d, 'x').map((p) => p.message)).toEqual(['Add an item to tags, or use Clear field to take it out of the note.'])
    void box
  })

  it('ends, unpins, adds, and removes dated entries, changing only their own keys', () => {
    const before: Json = [
      { value: '[[count-dracula]]', from: 'dracula:1' },
      { value: '[[jonathan-harker]]', from: 'dracula:7', pin: true },
    ]
    const d = draft(types.keeper, before)
    const [count, jonathan] = d.entries
    const later = newEntry(types.keeper)
    ;(later.value as { kind: 'one'; draft: { text: string } }).draft.text = '[[abraham-van-helsing]]'
    later.from = 'dracula:20'
    d.entries = [{ ...count, until: 'dracula:5' }, { ...jonathan, pin: false, status: 'candidate' }, later]

    expect(fieldChanges(types.keeper, before, d)).toEqual([
      { op: 'add', path: [], value: { value: '[[abraham-van-helsing]]', from: 'dracula:20' } },
      { op: 'set', path: [0, 'until'], value: 'dracula:5' },
      { op: 'set', path: [1, 'status'], value: 'candidate' },
      { op: 'remove', path: [1, 'pin'] },
    ])

    d.entries = [count]
    expect(fieldChanges(types.keeper, before, d)).toEqual([{ op: 'remove', path: [1] }])
  })

  it('writes a plain value that gains a date as a dated list, and a cleared field as a removal', () => {
    const d = draft(types.keeper, '[[count-dracula]]')
    expect(d.shape).toBe('plain')
    const later = newEntry(types.keeper)
    ;(later.value as { kind: 'one'; draft: { text: string } }).draft.text = '[[mina-harker]]'
    later.from = 'dracula:9'
    d.entries = [d.entries[0], later]
    expect(fieldChanges(types.keeper, '[[count-dracula]]', d)).toEqual([
      { op: 'set', path: [], value: [{ value: '[[count-dracula]]' }, { value: '[[mina-harker]]', from: 'dracula:9' }] },
    ])

    // A pin makes a plain value an entry.
    const pinned = draft(types.label, 'Earth box')
    pinned.entries[0].pin = true
    expect(fieldChanges(types.label, 'Earth box', pinned)).toEqual([{ op: 'set', path: [], value: { value: 'Earth box', pin: true } }])

    d.entries = []
    expect(fieldChanges(types.keeper, '[[count-dracula]]', d)).toEqual([{ op: 'remove', path: [] }])
  })

  it('reads numbers, dates with a sort key, and booleans, and changes a date mapping key by key', () => {
    // Numbers come and go as their text, so 2.50 stays 2.50 and a long one keeps every digit.
    const w = draft(types.weight, { $number: '12.50' })
    expect(w.entries[0].value).toMatchObject({ draft: { text: '12.50' } })
    expect(fieldChanges(types.weight, { $number: '12.50' }, w)).toEqual([])
    ;(w.entries[0].value as { kind: 'one'; draft: { text: string } }).draft.text = '3.250'
    expect(fieldChanges(types.weight, { $number: '12.50' }, w)).toEqual([{ op: 'set', path: [], value: { $number: '3.250' } }])
    const serial = draft(types.label, { $number: '12345678901234567890' })
    expect(serial.entries[0].value).toMatchObject({ draft: { text: '12345678901234567890' } })

    const f = draft(types.found, '1893-08-08')
    const fv = f.entries[0].value as { kind: 'one'; draft: { text: string; sort: string } }
    fv.draft.text = 'early August'
    fv.draft.sort = '1893, 8'
    expect(fieldChanges(types.found, '1893-08-08', f)).toEqual([
      { op: 'set', path: [], value: { text: 'early August', sort: [{ $number: '1893' }, { $number: '8' }] } },
    ])

    const before: Json = { text: 'early August', sort: [{ $number: '1893' }, { $number: '8' }] }
    const m = draft(types.found, before)
    ;(m.entries[0].value as { kind: 'one'; draft: { text: string } }).draft.text = 'August'
    expect(fieldChanges(types.found, before, m)).toEqual([{ op: 'set', path: ['text'], value: 'August' }])

    const b = draft(types.preserved, false)
    expect(b.entries[0].value).toMatchObject({ draft: { text: 'false' } })
  })

  it('says what to fix: a number, a link, a sort key, an empty value', () => {
    const problems = (t: SourceFieldType, text: string, sort = '') => {
      const d = draft(t, undefined)
      const v = d.entries[0].value as { kind: 'one'; draft: { text: string; sort: string } }
      v.draft.text = text
      v.draft.sort = sort
      return fieldProblems(t, d, 'x').map((p) => p.message)
    }

    expect(problems(types.weight, 'heavy')).toEqual(['weight is a number, such as 140 or 2.5.'])
    expect(problems(types.weight, '1e3')).toEqual([])
    expect(problems(types.finder, 'Mina')).toEqual(['finder is a link to one note, such as [[mina-harker]]: pick one from the list as you type.'])
    expect(problems(types.found, '1893', 'August')).toEqual(['A sort key is whole numbers separated by commas, such as 1893, 8, 10.'])
    expect(problems(types.label, '')).toEqual(['Give label a value, or use Clear field to take it out of the note.'])
    expect(problems(types.label, 'two\nlines')).toEqual(['label is one line of text.'])
  })

  it("says why a value the form can't show is edited in the file", () => {
    expect(fieldDraftOf(types.finder, [['velari']])).toMatch(/F-FLD-6/)
    expect(fieldDraftOf(types.keeper, [{ value: 'a' }, 'b'])).toMatch(/F-TMP-1/)
    expect(fieldDraftOf(types.found, { text: 'x', calendar: 'reach' })).toMatch(/can't show/)
    expect(fieldDraftOf(types.label, { x: 1 })).toMatch(/can't show/)
  })

  it('writes a link by the file name where that reaches only the note, else by its id', () => {
    const minaPath = 'universes/dracula/books/dracula/entities/characters/mina-harker.md'
    const pick = { path: minaPath, name: 'Mina Harker', id: '01M4TF5F0EKGGWD5FNVBAEY8X1' }
    expect(linkFor(pick, [{ path: minaPath }, { path: 'universes/dracula/entities/notes/whitby.md' }])).toBe('[[mina-harker]]')

    // Another note with the same file name could take the link, or make it ambiguous (F-LINK-7): the id reaches only this one.
    expect(linkFor(pick, [{ path: minaPath }, { path: 'universes/dracula/series/later/entities/characters/mina-harker.md' }])).toBe('[[01M4TF5F0EKGGWD5FNVBAEY8X1]]')
    // Not knowing the other notes, the id too.
    expect(linkFor(pick, null)).toBe('[[01M4TF5F0EKGGWD5FNVBAEY8X1]]')
    // A file name that isn't a slug: the id, never a name with | # or ] in it.
    const odd = { path: 'universes/dracula/notes/The Year #1893.md', name: 'The Year #1893 | draft', id: '01M4TF5F0EKGGWD5FNVBAEY8X2' }
    expect(linkFor(odd, [])).toBe('[[01M4TF5F0EKGGWD5FNVBAEY8X2]]')
    expect(linkFor({ ...odd, id: null }, [])).toBe('[[The Year 1893 draft]]')
  })

  it('offers notes by their name or slug, not ones that only mention the words', () => {
    const jon = { name: 'Jonathan Harker', path: 'x/jonathan-harker.md' }
    expect(nameMatches(jon, 'Jon')).toBe(true)
    expect(nameMatches(jon, 'har')).toBe(true)
    expect(nameMatches(jon, 'jon har')).toBe(true)
    expect(nameMatches(jon, 'jonathan-h')).toBe(true)
    expect(nameMatches(jon, 'mina')).toBe(false)
    expect(nameMatches({ name: 'Castle Dracula', path: 'x/castle-dracula.md' }, 'Jon')).toBe(false)
  })
})

describe('B1: an untouched form sends nothing, whatever the value', () => {
  const n = (t: string) => ({ $number: t })
  const scalars: Json[] = ['text', 'Dried', n('2.50'), n('12345678901234567890'), true, false, '[[mina-harker]]', 'dracula:5', '1893-08-08', '']
  const lists: Json[] = [[], ['a', 'b'], [n('1'), 'x'], ['[[a]]']]
  const dates: Json[] = [{ text: 'late spring' }, { sort: n('1893') }, { text: 'x', sort: [n('1893'), n('8')] }]
  const values: Json[] = [null, ...scalars, ...lists, ...dates]
  const shapes: Json[] = [
    ...values,
    ...values.map((v): Json => ({ value: v })),
    ...values.map((v): Json => ({ value: v, from: 'b:5', pin: true, status: 'candidate', note: 'kept' })),
    ...values.map((v): Json => [{ value: v, from: 'b:1' }, { value: null, from: 'b:5' }, { value: v, until: 'b:9', pin: false }]),
    [{ value: ['a'], from: 'b:1' }, { value: null, from: 'b:5' }],
    [{ value: '~', from: '~' }],
  ]
  const allTypes = Object.values(types).flatMap((t) => [t, { ...t, list: !t.list }, { ...t, temporal: !t.temporal }])

  it('for every value shape the server can send, and every field type', () => {
    let checked = 0
    for (const t of allTypes) {
      for (const raw of shapes) {
        const d = fieldDraftOf(t, raw)
        if (typeof d === 'string') {
          continue
        }

        checked++
        expect([t.name, t.list, raw, fieldChanges(t, raw, d)]).toEqual([t.name, t.list, raw, []])
      }
    }

    expect(checked).toBeGreaterThan(200)
  })

  it('keeps an entry whose value is null when another entry changes', () => {
    const t = { ...types.tags, temporal: true }
    const before: Json = [{ value: ['a'], from: 'b:1' }, { value: null, from: 'b:5' }]
    const d = draft(t, before)
    d.entries = [{ ...d.entries[0], until: 'b:3' }, d.entries[1]]
    expect(fieldChanges(t, before, d)).toEqual([{ op: 'set', path: [0, 'until'], value: 'b:3' }])
  })
})

// --------------------------------------------------------------- the page

const mina = entities.find((e) => e.key === 'E-MINA')!
const jonathan = entities.find((e) => e.key === 'E-JONATHAN')!

function pageField(name: string, value: unknown, line: number, extra: Partial<PageField> = {}): PageField {
  return { name, value, kind: 'plain', status: null, pinned: false, from: null, until: null, line, entryIndex: null, live: null, links: [], ...extra }
}

const pageFields: PageField[] = [
  pageField('role', 'A schoolmistress', 6, { kind: 'temporal', from: null, until: null, entryIndex: 0 }),
  pageField('role', 'A wife', 7, { kind: 'temporal', from: 'dracula:3', entryIndex: 1 }),
  pageField('occupation', ['teacher', 'secretary', 'typist'], 8),
  pageField('state', 'Dried', 9),
  pageField('weight', 12, 10),
  pageField('x_odd', 'kept', 11),
  pageField('species', [['velari']], 12),
]

const sourceTypes: SourceFieldType[] = [
  type('role', 'text', { temporal: true, description: 'Their part in the story.' }),
  type('occupation', 'text', { list: true, temporal: true }),
  type('state', 'enum', { values: ['fresh', 'dried', 'lost'] }),
  type('weight', 'number'),
  type('species', 'text'),
  type('husband', 'ref', { to: ['character'] }),
  type('born', 'date'),
  type('placeholder', 'bool'),
]

function source(hash = 'h1', fields: EntitySource['fields'] | null = null): EntitySource {
  return {
    version: 1,
    entity: mina,
    hash,
    text: '---\nname: Mina Harker\n---\n',
    lineEndings: 'lf',
    byteOrderMark: false,
    frozen: null,
    lead: { line: 4, text: '# Mina Harker' },
    sections: [],
    standardSections: [],
    fields: fields ?? [
      { name: 'role', line: 6, value: [{ value: 'A schoolmistress' }, { value: 'A wife', from: 'dracula:3' }] },
      { name: 'occupation', line: 8, value: ['teacher', 'secretary', 'typist'] },
      { name: 'state', line: 9, value: 'Dried' },
      { name: 'weight', line: 10, value: { $number: '12' } },
      { name: 'x_odd', line: 11, value: 'kept' },
      { name: 'species', line: 12, value: [['velari']] },
    ],
    fieldTypes: sourceTypes,
  }
}

function written(extra: Partial<WriteResult> = {}): WriteResult {
  return { version: 1, changed: true, path: mina.path, hash: 'h2', entity: mina, effects: [], pinnedCanon: [], warnings: [], files: null, idAdded: null, ...extra }
}

interface Post {
  path: string
  body: Record<string, unknown>
}

function stubFields(options: { onPost?: (post: Post, n: number) => Response | WriteResult } = {}) {
  const pages = (key: string, at: string | null): EntityPageResult => ({
    version: 1,
    view: at ? 'canon_as_of' : 'full',
    position: at,
    inView: true,
    note: null,
    entity: page(entities.find((e) => e.key === key)!, { fields: pageFields }),
  })
  const stub = stubApi({ entityPage: pages })
  const file = { source: source() }
  const base = stub.fetchMock.getMockImplementation()!
  const posts: Post[] = []
  stub.fetchMock.mockImplementation((async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://127.0.0.1')
    if (url.pathname === '/api/entity/source') {
      stub.calls.push(`${url.pathname}${url.search}`)
      return Response.json(file.source)
    }

    if (url.pathname === '/api/search') {
      stub.calls.push(`${url.pathname}${url.search}`)
      const hit = (e: typeof mina) => ({ entity: e, section: null, line: null, snippet: e.name, status: 'canon', inCanon: true, inComment: false })
      return Response.json({ version: 1, query: url.searchParams.get('query')!, count: 2, truncated: false, hits: [hit(jonathan), hit(mina)] } satisfies SearchResult)
    }

    if (url.pathname.startsWith('/api/edit/')) {
      const post = { path: url.pathname.slice('/api/edit/'.length), body: JSON.parse(String(init?.body)) as Record<string, unknown> }
      posts.push(post)
      const answer = options.onPost?.(post, posts.length) ?? written()
      return answer instanceof Response ? answer : Response.json(answer)
    }

    return base(input)
  }) as typeof fetch)
  return { ...stub, posts, file }
}

async function openField(name: string) {
  fireEvent.click(await screen.findByRole('button', { name: `Edit ${name}` }))
  return screen.findByRole('form', { name: `Edit ${name}` })
}

describe('SC-WEB-60: editing a field with a control for its type', () => {
  it('edits an enum with a dropdown of its values, and saves only that field', async () => {
    const { posts } = stubFields()
    renderApp('/u/dracula/e/E-MINA')
    const form = await openField('state')

    const select = within(form).getByRole('combobox', { name: 'Value' })
    await waitFor(() => expect(document.activeElement).toBe(select))
    expect((select as HTMLSelectElement).value).toBe('dried')
    expect(within(select).getAllByRole('option').map((o) => o.textContent)).toEqual(['fresh', 'dried', 'lost'])
    expect((within(form).getByRole('button', { name: 'Save' }) as HTMLButtonElement).disabled).toBe(true)
    fireEvent.change(select, { target: { value: 'lost' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('Saved the field state.')).toBeTruthy()
    expect(posts).toEqual([
      { path: 'field', body: { entity: 'E-MINA', universe: 'dracula', expectedHash: 'h1', field: 'state', changes: [{ op: 'set', path: [], value: 'lost' }] } },
    ])
    expect(document.activeElement?.id).toBe('edit-saved')
  })

  it('checks a number before sending it, and points at what to fix', async () => {
    const { posts } = stubFields()
    renderApp('/u/dracula/e/E-MINA')
    const form = await openField('weight')

    const input = within(form).getByRole('textbox', { name: 'Value' })
    fireEvent.change(input, { target: { value: 'heavy' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))

    expect(await within(form).findByRole('alert')).toHaveProperty('textContent', 'weight is a number, such as 140 or 2.5.')
    expect(document.activeElement).toBe(input)
    expect(input.getAttribute('aria-invalid')).toBe('true')
    expect(posts).toEqual([])

    fireEvent.change(input, { target: { value: '12.75' } })
    fireEvent.keyDown(input, { key: 'Enter', ctrlKey: true })
    await waitFor(() => expect(posts).toHaveLength(1))
    expect(posts[0].body.changes).toEqual([{ op: 'set', path: [], value: { $number: '12.75' } }])
  })

  it('moves, removes, and adds list items with the keyboard-reachable buttons', async () => {
    const { posts } = stubFields()
    renderApp('/u/dracula/e/E-MINA')
    const form = await openField('occupation')

    // The plain list of a field that can change over the story is its first entry.
    const entry = within(form).getByRole('group', { name: /Entry 1 of 1/ })
    fireEvent.click(within(entry).getByRole('button', { name: 'Move item 3 up' }))
    await waitFor(() => expect(document.activeElement?.getAttribute('aria-label')).toBe('Move item 2 up'))
    fireEvent.click(within(entry).getByRole('button', { name: 'Remove item 1' }))
    fireEvent.click(within(entry).getByRole('button', { name: 'Add item' }))
    const items = within(entry).getAllByRole('textbox')
    await waitFor(() => expect(document.activeElement).toBe(items[2]))
    fireEvent.change(items[2], { target: { value: 'author' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(posts).toHaveLength(1))
    expect(posts[0].body.changes).toEqual([
      { op: 'remove', path: [0] },
      { op: 'remove', path: [1] },
      { op: 'add', path: [], value: 'typist', index: 0 },
      { op: 'add', path: [], value: 'author' },
    ])
  })

  it('gives each dated entry a From and an Until picked from the books and chapters, and adds, ends, and removes entries', async () => {
    const { posts } = stubFields()
    renderApp('/u/dracula/e/E-MINA')
    const form = await openField('role')

    expect(within(form).getByText(/which can change over the story/)).toBeTruthy()
    const second = within(form).getByRole('group', { name: 'Entry 2 of 2: holds from Dracula chapter 3' })
    const until = within(second).getByRole('group', { name: 'Until' })
    fireEvent.change(within(until).getByRole('combobox', { name: 'Book' }), { target: { value: 'dracula' } })
    fireEvent.change(within(until).getByRole('combobox', { name: 'Chapter' }), { target: { value: 'dracula:5' } })
    expect(within(form).getByRole('group', { name: 'Entry 2 of 2: holds from Dracula chapter 3 until Dracula chapter 5' })).toBeTruthy()

    // A position the book doesn't list yet can be typed.
    fireEvent.click(within(form).getByRole('button', { name: 'Add a dated entry' }))
    const third = within(form).getByRole('group', { name: /Entry 3 of 3/ })
    fireEvent.change(within(third).getByRole('textbox', { name: 'Value' }), { target: { value: 'A widow' } })
    const from = within(third).getByRole('group', { name: 'From' })
    fireEvent.change(within(from).getByRole('combobox', { name: 'Book' }), { target: { value: '*' } })
    fireEvent.change(within(from).getByRole('textbox', { name: 'Position' }), { target: { value: 'dracula:27' } })
    fireEvent.click(within(within(form).getByRole('group', { name: /Entry 1 of 3/ })).getByRole('checkbox', { name: 'Pinned (hard canon)' }))
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(posts).toHaveLength(1))
    expect(posts[0].body.changes).toEqual([
      { op: 'add', path: [], value: { value: 'A widow', from: 'dracula:27' } },
      { op: 'set', path: [0, 'pin'], value: true },
      { op: 'set', path: [1, 'until'], value: 'dracula:5' },
    ])
  })

  it('removes a dated entry', async () => {
    const { posts } = stubFields()
    renderApp('/u/dracula/e/E-MINA')
    const form = await openField('role')

    fireEvent.click(within(form).getByRole('button', { name: 'Remove entry 1' }))
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(posts).toHaveLength(1))
    expect(posts[0].body.changes).toEqual([{ op: 'remove', path: [0] }])
  })

  it('offers a link from the search as letters are typed, picked with the arrow keys and Enter', async () => {
    const { posts, file } = stubFields()
    file.source = { ...source(), fields: [...source().fields, { name: 'husband', line: 13, value: null }] }
    renderApp('/u/dracula/e/E-MINA')
    fireEvent.click(await screen.findByRole('button', { name: 'Add a field' }))
    const add = await screen.findByRole('form', { name: 'Add a field' })
    // The type's fields the note doesn't set, or sets to nothing (husband), in the type's order.
    expect(within(add).getAllByRole('option').map((o) => (o as HTMLOptionElement).value)).toEqual(['', 'husband', 'born', 'placeholder'])
    fireEvent.change(within(add).getByRole('combobox', { name: 'Field' }), { target: { value: 'husband' } })
    fireEvent.click(within(add).getByRole('button', { name: 'Add field' }))

    const form = await screen.findByRole('form', { name: 'Edit husband' })
    const input = within(form).getByRole('combobox', { name: 'Value' })
    fireEvent.change(input, { target: { value: 'Jon' } })
    const list = await within(form).findByRole('listbox')
    expect(within(list).getAllByRole('option').map((o) => o.textContent)).toEqual(['Jonathan Harker character'])
    fireEvent.keyDown(input, { key: 'ArrowDown' })
    expect(input.getAttribute('aria-activedescendant')).toMatch(/-0$/)
    fireEvent.keyDown(input, { key: 'ArrowUp' })
    fireEvent.keyDown(input, { key: 'Enter' })
    expect((input as HTMLInputElement).value).toBe('[[jonathan-harker]]')
    expect(within(form).queryByRole('listbox')).toBeNull()

    // Escape closes an open list, not the editor.
    fireEvent.change(input, { target: { value: 'Min' } })
    await within(form).findByRole('listbox')
    fireEvent.keyDown(input, { key: 'Escape' })
    expect(within(form).queryByRole('listbox')).toBeNull()
    expect(screen.getByRole('form', { name: 'Edit husband' })).toBeTruthy()
    fireEvent.change(input, { target: { value: '[[jonathan-harker]]' } })

    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(posts).toHaveLength(1))
    expect(posts[0].body.changes).toEqual([{ op: 'set', path: [], value: '[[jonathan-harker]]' }])
  })
})

describe('SC-WEB-63: adding and clearing fields, and what only the file can change', () => {
  it('adds a field from the ones the type has and the note doesn’t set', async () => {
    const { posts } = stubFields()
    renderApp('/u/dracula/e/E-MINA')
    fireEvent.click(await screen.findByRole('button', { name: 'Add a field' }))
    const add = await screen.findByRole('form', { name: 'Add a field' })
    fireEvent.change(within(add).getByRole('combobox', { name: 'Field' }), { target: { value: 'born' } })
    fireEvent.click(within(add).getByRole('button', { name: 'Add field' }))

    const form = await screen.findByRole('form', { name: 'Edit born' })
    const date = within(form).getByRole('group', { name: 'Value' })
    fireEvent.change(within(date).getByRole('textbox', { name: 'Date' }), { target: { value: 'the year of the storm' } })
    fireEvent.change(within(date).getByRole('textbox', { name: 'Sort key (optional)' }), { target: { value: '1870' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(posts).toHaveLength(1))
    expect(posts[0].body).toMatchObject({ field: 'born', changes: [{ op: 'set', path: [], value: { text: 'the year of the storm', sort: { $number: '1870' } } }] })
  })

  it('clears a field after asking in the page', async () => {
    const { posts } = stubFields()
    renderApp('/u/dracula/e/E-MINA')
    const form = await openField('weight')

    fireEvent.click(within(form).getByRole('button', { name: 'Clear field' }))
    const ask = within(form).getByRole('group', { name: /Clear weight\?/ })
    expect(document.activeElement).toBe(within(ask).getByRole('button', { name: 'Keep it' }))
    fireEvent.click(within(ask).getByRole('button', { name: 'Clear field' }))

    await waitFor(() => expect(posts).toHaveLength(1))
    expect(posts[0].body).toMatchObject({ field: 'weight', changes: [{ op: 'remove', path: [] }] })
    expect(await screen.findByText('Saved the note (cleared weight).')).toBeTruthy()
  })

  it('shows a field the type doesn’t define, or a value the form can’t show, with Edit in file', async () => {
    stubFields()
    renderApp('/u/dracula/e/E-MINA')

    const odd = await screen.findByRole('button', { name: "Edit in file: x_odd can only be changed in the note's file" })
    expect(screen.getByText("This note's type doesn't define x_odd.")).toBeTruthy()
    expect(screen.getByText(/F-FLD-6/)).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Edit x_odd' })).toBeNull()
    fireEvent.click(odd)
    expect(await screen.findByText('Editing the whole file')).toBeTruthy()
  })
})

describe('SC-WEB-65: field edits are guarded', () => {
  it('asks before Escape drops unsaved changes, and closes at once when nothing changed', async () => {
    const { posts } = stubFields()
    renderApp('/u/dracula/e/E-MINA')
    let form = await openField('state')
    fireEvent.keyDown(within(form).getByRole('combobox', { name: 'Value' }), { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('form', { name: 'Edit state' })).toBeNull())
    expect(document.activeElement?.id).toBe('edit-field-state')

    form = await openField('state')
    fireEvent.change(within(form).getByRole('combobox', { name: 'Value' }), { target: { value: 'fresh' } })
    fireEvent.keyDown(within(form).getByRole('combobox', { name: 'Value' }), { key: 'Escape' })
    const ask = await screen.findByRole('alertdialog', { name: "Discard the changes you haven't saved?" })
    fireEvent.click(within(ask).getByRole('button', { name: 'Discard changes' }))
    await waitFor(() => expect(screen.queryByRole('form', { name: 'Edit state' })).toBeNull())
    expect(posts).toEqual([])
  })

  it('shows both versions when the note changed meanwhile, and Keep mine saves on the new hash', async () => {
    const { posts, file } = stubFields({
      onPost: (_, n) => (n === 1 ? Response.json({ code: 'stale_file', message: 'The file changed.', current: file.source }, { status: 409 }) : written()),
    })
    renderApp('/u/dracula/e/E-MINA')
    const form = await openField('state')
    // An agent changes the field meanwhile.
    file.source = source('h9', source().fields.map((f) => (f.name === 'state' ? { ...f, value: 'fresh' } : f)))
    fireEvent.change(within(form).getByRole('combobox', { name: 'Value' }), { target: { value: 'lost' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))

    const conflict = await screen.findByRole('region', { name: 'The note changed while you were editing the field state' })
    expect(conflict.textContent).toContain('"lost"')
    expect(conflict.textContent).toContain('"fresh"')
    expect(within(conflict).queryByRole('button', { name: 'Copy mine and reload' })).toBeNull()
    fireEvent.click(within(conflict).getByRole('button', { name: 'Keep mine' }))

    await waitFor(() => expect(posts).toHaveLength(2))
    expect(posts[1].body).toMatchObject({ expectedHash: 'h9', changes: [{ op: 'set', path: [], value: 'lost' }] })
  })

  it('says why a field edit was refused, and keeps the form open', async () => {
    stubFields({ onPost: () => Response.json({ code: 'invalid_edit', message: "fields.role.1.from: 'chapter seven' isn't a position." }, { status: 422 }) })
    renderApp('/u/dracula/e/E-MINA')
    const form = await openField('role')
    const second = within(form).getByRole('group', { name: /Entry 2 of 2/ })
    const from = within(second).getByRole('group', { name: 'From' })
    fireEvent.change(within(from).getByRole('combobox', { name: 'Book' }), { target: { value: '*' } })
    fireEvent.change(within(from).getByRole('textbox', { name: 'Position' }), { target: { value: 'chapter seven' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))

    expect(await within(form).findByText(/It wasn't saved, because the note would have errors/)).toBeTruthy()
    expect(screen.getByRole('form', { name: 'Edit role' })).toBeTruthy()
  })

  it('keeps clearing the field after a conflict: Keep mine sends the removal again', async () => {
    const { posts, file } = stubFields({
      onPost: (_, n) => (n === 1 ? Response.json({ code: 'stale_file', message: 'The file changed.', current: file.source }, { status: 409 }) : written()),
    })
    renderApp('/u/dracula/e/E-MINA')
    const form = await openField('weight')
    file.source = source('h9', source().fields.map((f) => (f.name === 'weight' ? { ...f, value: { $number: '14' } } : f)))
    fireEvent.click(within(form).getByRole('button', { name: 'Clear field' }))
    fireEvent.click(within(within(form).getByRole('group', { name: /Clear weight\?/ })).getByRole('button', { name: 'Clear field' }))

    const conflict = await screen.findByRole('region', { name: 'The note changed while you were editing the field weight' })
    expect(conflict.textContent).toContain('(not set)')
    expect(conflict.textContent).toContain('14')
    fireEvent.click(within(conflict).getByRole('button', { name: 'Keep mine' }))

    await waitFor(() => expect(posts).toHaveLength(2))
    expect(posts[1].body).toMatchObject({ expectedHash: 'h9', changes: [{ op: 'remove', path: [] }] })
    expect(await screen.findByText('Saved the note (cleared weight).')).toBeTruthy()
  })

  it('saves again on the new file, without asking, when only another part of the note changed', async () => {
    const { posts, file } = stubFields({
      onPost: (_, n) => (n === 1 ? Response.json({ code: 'stale_file', message: 'The file changed.', current: file.source }, { status: 409 }) : written()),
    })
    renderApp('/u/dracula/e/E-MINA')
    const form = await openField('state')
    // An agent changes another field meanwhile; state is as it was.
    file.source = source('h9', source().fields.map((f) => (f.name === 'weight' ? { ...f, value: { $number: '14' } } : f)))
    fireEvent.change(within(form).getByRole('combobox', { name: 'Value' }), { target: { value: 'lost' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('Saved the field state.')).toBeTruthy()
    expect(posts.map((p) => p.body.expectedHash)).toEqual(['h1', 'h9'])
    expect(posts[1].body.changes).toEqual([{ op: 'set', path: [], value: 'lost' }])
    expect(screen.queryByRole('region', { name: /The note changed/ })).toBeNull()
  })

  it('returns focus to the field’s own Edit in file button', async () => {
    stubFields()
    renderApp('/u/dracula/e/E-MINA')
    fireEvent.click(await screen.findByRole('button', { name: "Edit in file: x_odd can only be changed in the note's file" }))
    expect(await screen.findByText('Editing the whole file')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(document.activeElement?.id).toBe('edit-file-x_odd'))
  })
})
