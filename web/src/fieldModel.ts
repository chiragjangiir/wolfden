import type { SourceFieldType } from './editApi'

// What a field editor holds, and the changes it sends (#28, part b). A field
// is edited as its entries: a plain value is one entry with nothing but its
// value, so every field has one form. On Save, the changes are worked out
// from the value as written and the form, keeping track of which item or
// entry came from where, so only what the writer changed is sent: an
// untouched item, entry, or key keeps its bytes (format.md §12).

/**
 * A JSON value, as the source gives a field's value as written. A plain
 * number comes as `{ $number: "2.50" }`, its text as written, since a JSON
 * number would lose `2.50`'s last digit or a long number's precision; the
 * page sends numbers back the same way.
 */
export type Json = null | boolean | number | string | Json[] | { [key: string]: Json }

/** A number as written: `{ $number: "2.50" }`. */
export type NumberText = { $number: string }

/** True for a number as written (`{ $number: "2.50" }`), which is a scalar, not a mapping. */
export function isNumberText(v: unknown): v is NumberText {
  return typeof v === 'object' && v !== null && !Array.isArray(v) && Object.keys(v).length === 1 && typeof (v as NumberText).$number === 'string'
}

/** A number to send, as its text. */
export function numberText(text: string): NumberText {
  return { $number: text }
}

/** One change of /api/edit/field: `path` is under the field, `[]` for the field itself. */
export interface FieldChange {
  op: 'set' | 'add' | 'remove'
  path: (string | number)[]
  value?: Json
  index?: number
}

/**
 * One value in the form, as the writer types it: its text, and for a date
 * its sort key too. `raw` is the value as written, which is sent back
 * unchanged when the text is still `initial`, so `Past` isn't rewritten
 * as `past` behind the writer's back.
 */
export interface Draft {
  text: string
  sort: string
  initial: string
  initialSort: string
  raw: Json | undefined
}

/** One item of a list value, with the item number it had in the file, or null for a new one. */
export interface ItemDraft {
  id: number
  orig: number | null
  draft: Draft
}

/** A value: one, or a list of items for a list field. */
export type ValueDraft = { kind: 'one'; draft: Draft } | { kind: 'list'; items: ItemDraft[]; raw: Json | undefined }

/** One entry (format.md §7.3): a plain value is one with no from, until, status, or pin. */
export interface EntryDraft {
  id: number
  orig: number | null
  value: ValueDraft
  from: string
  until: string
  status: string
  pin: boolean
  /** The entry's other keys, as written: kept as they are. */
  extra: { [key: string]: Json }
  /** The entry as written, or undefined for a new one or a plain value. */
  raw: { [key: string]: Json } | undefined
}

export type Shape = 'none' | 'plain' | 'entry' | 'entries'

/** A field in the form. */
export interface FieldDraft {
  /** How the file writes it: not at all, a plain value, a single entry, or a temporal list of entries. */
  shape: Shape
  entries: EntryDraft[]
}

let nextId = 1

const ENTRY_KEYS = ['value', 'from', 'until', 'status', 'pin']

/** True for a mapping; a number as written isn't one. */
function isObject(v: unknown): v is { [key: string]: Json } {
  return typeof v === 'object' && v !== null && !Array.isArray(v) && !isNumberText(v)
}

function isEntry(v: unknown): v is { [key: string]: Json } {
  return isObject(v) && 'value' in v
}

function textOf(v: Json | undefined): string {
  return v === null || v === undefined ? '' : isNumberText(v) ? v.$number : String(v)
}

/** A form value for `raw`, or null if the form can't show it (a list or mapping where a scalar goes, a date with other keys). */
export function draftOf(type: SourceFieldType, raw: Json | undefined): Draft | null {
  if (Array.isArray(raw)) {
    return null
  }

  if (isObject(raw)) {
    if (type.type !== 'date' || Object.keys(raw).some((k) => k !== 'text' && k !== 'sort')) {
      return null
    }

    const sort = raw.sort
    const ints = isNumberText(sort) ? [sort] : Array.isArray(sort) ? sort : sort === undefined || sort === null ? [] : null
    if (ints === null || ints.some((n) => !isNumberText(n) || !/^-?[0-9]+$/.test(n.$number)) || (raw.text !== undefined && raw.text !== null && typeof raw.text !== 'string')) {
      return null
    }

    const text = textOf(raw.text)
    const sortText = ints.map(textOf).join(', ')
    return { text, sort: sortText, initial: text, initialSort: sortText, raw }
  }

  let text = textOf(raw)
  if (type.type === 'enum') {
    // Enums match without regard to case (types.md F-FT-8): the form shows the defined key.
    text = type.values.find((v) => v === text.toLowerCase()) ?? text
  }

  return { text, sort: '', initial: text, initialSort: '', raw }
}

function newDraft(): Draft {
  return { text: '', sort: '', initial: '', initialSort: '', raw: undefined }
}

function valueDraftOf(type: SourceFieldType, raw: Json | undefined): ValueDraft | null {
  if (!type.list) {
    const draft = draftOf(type, raw)
    return draft && { kind: 'one', draft }
  }

  if (raw === null || raw === undefined) {
    return { kind: 'list', items: [], raw }
  }

  const list = Array.isArray(raw) ? raw : [raw]
  const items: ItemDraft[] = []
  for (const [i, item] of list.entries()) {
    const draft = Array.isArray(item) || isObject(item) || item === null ? null : draftOf(type, item)
    if (!draft) {
      return null
    }

    // A value that isn't a list is read as a one-item list (F-FT-12); it has no item numbers to keep.
    items.push({ id: nextId++, orig: Array.isArray(raw) ? i : null, draft })
  }

  return { kind: 'list', items, raw }
}

function entryOf(type: SourceFieldType, raw: { [key: string]: Json }, orig: number | null): EntryDraft | null {
  const value = valueDraftOf(type, raw.value)
  const positions = [raw.from, raw.until, raw.status]
  if (!value || positions.some((p) => p !== undefined && p !== null && typeof p !== 'string') || (raw.pin !== undefined && raw.pin !== null && typeof raw.pin !== 'boolean')) {
    return null
  }

  const extra: { [key: string]: Json } = {}
  for (const [k, v] of Object.entries(raw)) {
    if (!ENTRY_KEYS.includes(k)) {
      extra[k] = v
    }
  }

  return {
    id: nextId++,
    orig,
    value,
    from: textOf(raw.from),
    until: textOf(raw.until),
    status: textOf(raw.status),
    pin: raw.pin === true,
    extra,
    raw,
  }
}

/** A plain value as the one entry the form shows. */
function plainEntry(value: ValueDraft): EntryDraft {
  return { id: nextId++, orig: null, value, from: '', until: '', status: '', pin: false, extra: {}, raw: undefined }
}

/** A new, empty entry. */
export function newEntry(type: SourceFieldType): EntryDraft {
  return plainEntry(type.list ? { kind: 'list', items: [], raw: undefined } : { kind: 'one', draft: newDraft() })
}

/** A new, empty list item. */
export function newItem(): ItemDraft {
  return { id: nextId++, orig: null, draft: newDraft() }
}

/**
 * The form for a field's value as written (`undefined` for a field the file
 * doesn't set), or a reason it can't be shown in the form, in words for the
 * writer: such a value is changed with Edit file.
 */
export function fieldDraftOf(type: SourceFieldType, raw: Json | undefined): FieldDraft | string {
  const why = "It's written in a way this form can't show"
  if (raw === undefined) {
    return { shape: 'none', entries: [newEntry(type)] }
  }

  if (raw === null) {
    // Written with no value (`born:`): unset, and sent back as it is unless the writer gives it one.
    return { shape: 'plain', entries: [plainEntry(valueDraftOf(type, null)!)] }
  }

  if (Array.isArray(raw) && raw.length > 0 && raw.some(isEntry)) {
    if (!raw.every(isEntry)) {
      return `${why}: its list mixes dated entries with plain items (F-TMP-1).`
    }

    const entries = raw.map((e, i) => entryOf(type, e as { [key: string]: Json }, i))
    return entries.every((e) => e !== null) ? { shape: 'entries', entries: entries as EntryDraft[] } : `${why}.`
  }

  if (isEntry(raw)) {
    const entry = entryOf(type, raw, null)
    return entry ? { shape: 'entry', entries: [entry] } : `${why}.`
  }

  const value = valueDraftOf(type, raw)
  if (!value) {
    return Array.isArray(raw) && !type.list
      ? `${why}: it's a list, and ${type.name} isn't a list field. If it's a link, it needs quotes: "[[name]]" (F-FLD-6).`
      : `${why}.`
  }

  return { shape: 'plain', entries: [plainEntry(value)] }
}

/** True if the entry is just a value, with nothing that needs an entry to write it. */
function isBare(e: EntryDraft): boolean {
  return e.from.trim() === '' && e.until.trim() === '' && e.status === '' && !e.pin && Object.keys(e.extra).length === 0 && !(e.raw && 'pin' in e.raw)
}

/** A number as types.md F-FT-6 reads one: whole or decimal, or 0o17 and 0x1F. */
const NUMBER = /^(?:[-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?|0o[0-7]+|0x[0-9a-fA-F]+)$/
const SORT = /^\s*-?[0-9]+(?:\s*,\s*-?[0-9]+)*\s*$/

/** A form value as JSON to write, or the value as written if it wasn't changed. */
function jsonOfDraft(type: SourceFieldType, d: Draft): Json {
  if (d.text === d.initial && d.sort === d.initialSort && d.raw !== undefined) {
    return d.raw
  }

  const text = type.type === 'string' || type.type === 'text' ? d.text : d.text.trim()
  switch (type.type) {
    case 'number':
      // Sent as its text, so `2.50` is written `2.50`; the server reads it as a number.
      return NUMBER.test(text) && Number.isFinite(Number(text)) ? numberText(text) : text
    case 'bool':
      return text === 'true' ? true : text === 'false' ? false : text
    case 'date': {
      if (d.sort.trim() === '') {
        return text
      }

      const ints = d.sort.split(',').map((n) => numberText(n.trim()))
      const sort = ints.length === 1 ? ints[0] : ints
      return text === '' ? { sort } : { text, sort }
    }
    default:
      return text
  }
}

function untouched(d: Draft): boolean {
  return d.text === d.initial && d.sort === d.initialSort && d.raw !== undefined
}

/**
 * True for a list value whose items are all as written, in their places, with
 * none added or taken out: it's sent back as written, so a null value or a
 * single scalar isn't turned into a list.
 */
function untouchedList(v: Extract<ValueDraft, { kind: 'list' }>): boolean {
  if (v.raw === undefined) {
    return false
  }

  if (v.raw === null) {
    return v.items.length === 0
  }

  if (!Array.isArray(v.raw)) {
    return v.items.length === 1 && v.items[0].orig === null && untouched(v.items[0].draft)
  }

  return v.items.length === v.raw.length && v.items.every((item, i) => item.orig === i && untouched(item.draft))
}

function jsonOfValue(type: SourceFieldType, v: ValueDraft): Json {
  if (v.kind === 'one') {
    if (untouched(v.draft)) {
      return v.draft.raw as Json
    }

    const json = jsonOfDraft(type, v.draft)
    return json === '' ? null : json
  }

  if (untouchedList(v)) {
    return v.raw as Json
  }

  return v.items.map((i) => jsonOfDraft(type, i.draft))
}

function jsonOfEntry(type: SourceFieldType, e: EntryDraft): { [key: string]: Json } {
  const out: { [key: string]: Json } = { ...(e.raw ?? {}) }
  out.value = jsonOfValue(type, e.value)
  for (const key of ['from', 'until', 'status'] as const) {
    if (e[key].trim() !== '') {
      out[key] = e[key].trim()
    } else if (key in out && out[key] !== null) {
      delete out[key]
    }
  }

  if (e.pin) {
    out.pin = true
  } else if (out.pin === true) {
    delete out.pin
  }

  return { ...out, ...e.extra }
}

/** What the file would say for the field once saved, as JSON, and the item numbers each item came from; undefined to remove it. */
export function jsonOfField(type: SourceFieldType, field: FieldDraft): { value: Json | undefined; origins: (number | null)[] | null } {
  const { entries, shape } = field
  if (entries.length === 0) {
    return { value: undefined, origins: null }
  }

  if (entries.length === 1 && shape !== 'entries' && isBare(entries[0]) && shape !== 'entry') {
    const value = entries[0].value
    return { value: jsonOfValue(type, value), origins: value.kind === 'list' ? value.items.map((i) => i.orig) : null }
  }

  if (entries.length === 1 && shape !== 'entries') {
    return { value: jsonOfEntry(type, entries[0]), origins: null }
  }

  // Entries that were a plain value or one entry become a temporal list: in place where the items line up, else as a whole.
  return { value: entries.map((e) => jsonOfEntry(type, e)), origins: shape === 'entries' ? entries.map((e) => e.orig) : null }
}

function same(a: Json | undefined, b: Json | undefined): boolean {
  return JSON.stringify(a) === JSON.stringify(b)
}

/** The changes that turn `before` into `after` at `path`: key by key in mappings, item by item in lists of one length. */
function diff(path: (string | number)[], before: Json | undefined, after: Json, out: FieldChange[]) {
  if (same(before, after)) {
    return
  }

  if (isObject(before) && isObject(after)) {
    for (const [k, v] of Object.entries(after)) {
      if (k in before) {
        diff([...path, k], before[k], v, out)
      } else {
        out.push({ op: 'set', path: [...path, k], value: v })
      }
    }

    for (const k of Object.keys(before)) {
      if (!(k in after)) {
        out.push({ op: 'remove', path: [...path, k] })
      }
    }

    return
  }

  if (Array.isArray(before) && Array.isArray(after) && before.length === after.length && before.length > 0) {
    after.forEach((item, i) => diff([...path, i], before[i], item, out))
    return
  }

  out.push({ op: 'set', path, value: after })
}

/**
 * The changes that make the field, as written (`before`), what the form
 * says. Items and entries are followed by where they came from: one taken
 * out is removed, a new one added where it goes, one moved is removed and
 * added again, and one that stayed has only its changed keys set.
 */
export function fieldChanges(type: SourceFieldType, before: Json | undefined, field: FieldDraft): FieldChange[] {
  const { value: after, origins } = jsonOfField(type, field)
  const out: FieldChange[] = []
  if (same(before, after)) {
    return []
  }

  if (after === undefined) {
    return before === undefined ? [] : [{ op: 'remove', path: [] }]
  }

  if (!Array.isArray(before) || !Array.isArray(after) || origins === null) {
    if (before === undefined || before === null) {
      return [{ op: 'set', path: [], value: after }]
    }

    diff([], before, after, out)
    return out
  }

  // Removals first, from the end, so the numbers before them hold.
  const kept = new Set(origins.filter((o): o is number => o !== null))
  for (let i = before.length - 1; i >= 0; i--) {
    if (!kept.has(i)) {
      out.push({ op: 'remove', path: [i] })
    }
  }

  // Then put each item in its place: new ones added, moved ones taken out and added again.
  const current = before.map((_, i) => i).filter((i) => kept.has(i)) as (number | null)[]
  const moved = new Set<number>()
  origins.forEach((origin, k) => {
    if (current[k] === origin && origin !== null) {
      return
    }

    if (origin !== null) {
      const m = current.indexOf(origin)
      out.push({ op: 'remove', path: [m] })
      current.splice(m, 1)
      moved.add(origin)
    }

    out.push({ op: 'add', path: [], value: after[k], ...(k < current.length ? { index: k } : {}) })
    current.splice(k, 0, null)
  })

  // Then what changed in the items that stayed where they are.
  origins.forEach((origin, k) => {
    if (origin !== null && !moved.has(origin)) {
      diff([k], before[origin], after[k], out)
    }
  })

  return out
}

/** One thing the writer has to fix before saving, and where. */
export interface Problem {
  /** The id of the control it's about. */
  at: string
  message: string
}

/** The ids the form gives its controls, so a problem can point at one. */
export function controlId(base: string, entry: EntryDraft, part: string, item?: ItemDraft): string {
  return `${base}-${entry.id}-${part}${item ? `-${item.id}` : ''}`
}

function draftProblem(type: SourceFieldType, d: Draft): string | null {
  if (untouched(d)) {
    return null
  }

  const text = d.text.trim()
  switch (type.type) {
    case 'number':
      return NUMBER.test(text) && Number.isFinite(Number(text)) ? null : `${type.name} is a number, such as 140 or 2.5.`
    case 'bool':
      return text === 'true' || text === 'false' ? null : `Choose yes or no for ${type.name}.`
    case 'enum':
      return type.values.includes(text) ? null : `Choose one of ${type.values.join(', ')} for ${type.name}.`
    case 'ref':
      return /^\[\[[^[\]]+\]\]$/.test(text) ? null : `${type.name} is a link to one note, such as [[mina-harker]]: pick one from the list as you type.`
    case 'date':
      if (d.sort.trim() !== '' && !SORT.test(d.sort)) {
        return 'A sort key is whole numbers separated by commas, such as 1893, 8, 10.'
      }

      return text === '' && d.sort.trim() === '' ? `Give ${type.name} a date.` : null
    case 'string':
      return /[\r\n]/.test(d.text) ? `${type.name} is one line of text.` : null
    default:
      return null
  }
}

/** What the writer has to fix before the field can be saved; empty if nothing. */
export function fieldProblems(type: SourceFieldType, field: FieldDraft, base: string): Problem[] {
  const problems: Problem[] = []
  const plain = jsonOfField(type, field).value
  for (const entry of field.entries) {
    const v = entry.value
    if (v.kind === 'one') {
      const empty = v.draft.text.trim() === '' && v.draft.sort.trim() === ''
      const untouched = v.draft.text === v.draft.initial && v.draft.sort === v.draft.initialSort && v.draft.raw !== undefined
      if (empty && !untouched) {
        // An entry may be unset from its 'from' on (F-TMP-2); a plain value has to be something.
        if (!isObject(plain) && !Array.isArray(plain)) {
          problems.push({ at: controlId(base, entry, 'value'), message: `Give ${type.name} a value, or use Clear field to take it out of the note.` })
        }
      } else if (!empty) {
        const problem = draftProblem(type, v.draft)
        if (problem) {
          problems.push({ at: controlId(base, entry, 'value'), message: problem })
        }
      }
    } else {
      for (const item of v.items) {
        const problem = item.draft.text.trim() === '' ? 'An item is empty: write it, or remove it.' : draftProblem(type, item.draft)
        if (problem) {
          problems.push({ at: controlId(base, entry, 'value', item), message: problem })
        }
      }
    }
  }

  if (type.list && field.entries.length === 1 && Array.isArray(plain) && plain.length === 0 && field.shape !== 'entries' && field.shape !== 'entry') {
    problems.push({ at: controlId(base, field.entries[0], 'add-item'), message: `Add an item to ${type.name}, or use Clear field to take it out of the note.` })
  }

  return problems
}

/** A note's slug: its file name, where that's a key (F-KEY-4), else null. */
export function slugOf(entity: { path: string }): string | null {
  const file = entity.path.split('/').pop() ?? ''
  const slug = file.endsWith('.md') ? file.slice(0, -3) : ''
  return /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(slug) ? slug : null
}

/**
 * The link to write for a note, as the den writes them: `[[slug]]`, by its
 * file name, where that reaches only this note. A slug is the second tier of
 * link resolution (format.md F-LINK-6), so another note of the universe whose
 * file name matches it, ignoring case, could take the link or make it
 * ambiguous (F-LINK-7): then, or when `others` isn't known, or the file name
 * isn't a slug, the link is by id, `[[01J...]]`, which reaches only this
 * note. A note without an id is linked by its name only where that can be
 * written in a link (no `|`, `#`, `[`, or `]`) and no other note's slug or
 * name matches it; otherwise null: no link is sure to reach it.
 */
export function linkFor(
  entity: { path: string; name: string; id: string | null },
  others: readonly { path: string; name?: string }[] | null,
): string | null {
  const slug = slugOf(entity)
  const rivals = others?.filter((o) => o.path !== entity.path) ?? null
  // Another note's file name competes for the slug as links match, ignoring case (F-LINK-4): Mina-Harker.md with mina-harker.
  if (slug !== null && rivals !== null && !rivals.some((o) => fileKey(o.path) === slug)) {
    return `[[${slug}]]`
  }

  if (entity.id) {
    return `[[${entity.id}]]`
  }

  // No id: by name, only where it can be written in a link and nothing comes first or ties (a slug, F-LINK-6, or a name).
  const name = normalized(entity.name)
  const byName =
    slug === null && rivals !== null && name !== '' && !/[|#[\]]/.test(entity.name)
    && !rivals.some((o) => fileKey(o.path) === name || (o.name !== undefined && normalized(o.name) === name))
  return byName ? `[[${entity.name.trim()}]]` : null
}

/** A link's text as F-LINK-4 matches it: NFC, trimmed, whitespace runs as one space, case ignored. */
function normalized(text: string): string {
  return text.normalize('NFC').trim().replace(/\s+/g, ' ').toLowerCase()
}

/** A note's file name without `.md`, as a link by slug would match it. */
function fileKey(path: string): string {
  return normalized((path.split('/').pop() ?? '').replace(/\.md$/i, ''))
}

/** How names, slugs, and what's typed are split into words for the link picker. */
const WORDS = /[\s\-_.,:;'’"()]+/

/**
 * True if a note's name or slug matches what was typed for a link: each word
 * typed starts a word of the name or slug, ignoring case, so `jon har` finds
 * Jonathan Harker but a note that only mentions him in its text doesn't.
 */
export function nameMatches(entity: { name: string; path: string }, typed: string): boolean {
  const words = typed.toLowerCase().split(WORDS).filter((w) => w !== '')
  if (words.length === 0) {
    return false
  }

  const candidates = [entity.name, slugOf(entity) ?? ''].map((t) => t.toLowerCase().split(WORDS).filter((w) => w !== ''))
  return candidates.some((parts) => words.every((w) => parts.some((p) => p.startsWith(w))))
}

/** A value as the lines of YAML-like text a writer reads, for the conflict view. */
export function valueLines(value: Json | undefined): string {
  if (value === undefined) {
    return '(not set)'
  }

  if (Array.isArray(value)) {
    return value.length === 0 ? '[]' : value.map((v) => `- ${flow(v)}`).join('\n')
  }

  return flow(value)

  function flow(v: Json): string {
    if (Array.isArray(v)) {
      return `[${v.map(flow).join(', ')}]`
    }

    if (isObject(v)) {
      return `{ ${Object.entries(v).map(([k, x]) => `${k}: ${flow(x)}`).join(', ')} }`
    }

    return v === null ? '~' : typeof v === 'string' ? JSON.stringify(v) : isNumberText(v) ? v.$number : String(v)
  }
}
