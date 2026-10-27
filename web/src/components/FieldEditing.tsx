import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { STATUSES, apiPath, entitiesQuery, getJson, type PositionView, type SearchResult } from '../api'
import { EditError, postEdit, sourceQuery, type EntitySource, type SourceFieldType } from '../editApi'
import {
  controlId,
  fieldChanges,
  fieldDraftOf,
  fieldProblems,
  jsonOfField,
  linkFor,
  nameMatches,
  newEntry,
  newItem,
  valueLines,
  type Draft,
  type EntryDraft,
  type FieldChange,
  type FieldDraft,
  type ItemDraft,
  type Json,
  type Problem,
} from '../fieldModel'
import { describePosition, positionLabel } from '../story'
import { Conflict, DiscardPrompt, EditButton, SaveError, useAfterSave, useEditing } from './Editing'
import { ErrorState, Loading } from './States'

// Editing a note's fields in place (#28, part b): one field at a time, with
// a control for its type from the type definition. A field is edited as its
// entries, so a fact that holds from chapter 3 until chapter 9 is a value
// with a From and an Until; a plain value is one entry with neither. Save
// sends only what changed, through /api/edit/field (the MCP update_fields).

/** The value the file gives `name`, or undefined if it doesn't set it. */
function rawOf(source: EntitySource, name: string): Json | undefined {
  return source.fields.find((f) => f.name === name)?.value
}

/** "a link to a character, which can change over the story": the field's type in words. */
export function typeWords(type: SourceFieldType): string {
  const one: Record<SourceFieldType['type'], string> = {
    string: 'one line of text',
    text: 'text',
    number: 'a number',
    bool: 'yes or no',
    date: 'an in-world date',
    enum: `one of ${type.values.join(', ')}`,
    ref: type.to ? `a link to a ${type.to.join(' or ')}` : 'a link to a note',
    position: 'a place in the story',
  }
  const what = type.list ? `a list, each item ${one[type.type]}` : one[type.type]
  return `${what[0].toUpperCase()}${what.slice(1)}${type.temporal ? ', which can change over the story' : ''}.`
}

/**
 * Whether the page can offer a field's own editor: the type defines it and
 * the form can show its value. Otherwise, why not, for "Edit in file".
 */
export function fieldEditability(source: EntitySource | undefined, name: string): { editable: boolean; reason: string | null } {
  if (!source) {
    return { editable: false, reason: null }
  }

  const type = source.fieldTypes.find((t) => t.name === name)
  if (!type) {
    return { editable: false, reason: `This note's type doesn't define ${name}.` }
  }

  const draft = fieldDraftOf(type, rawOf(source, name))
  return typeof draft === 'string' ? { editable: false, reason: draft } : { editable: true, reason: null }
}

/** A field's Edit control, or, for a value the form can't show, Edit in file and why. */
export function FieldTools({ name, source }: { name: string; source: EntitySource | undefined }) {
  const editing = useEditing()
  if (!editing || !source) {
    return null
  }

  const { editable, reason } = fieldEditability(source, name)
  return editable ? (
    <EditButton target={{ kind: 'field', name }} label={`Edit ${name}`}>
      Edit
    </EditButton>
  ) : (
    <span className="field-readonly">
      <EditButton target={{ kind: 'file', from: `edit-file-${name}` }} id={`edit-file-${name}`} label={`Edit in file: ${name} can only be changed in the note's file`}>
        Edit in file
      </EditButton>{' '}
      <span className="muted">{reason}</span>
    </span>
  )
}

/** "Add field": the type's fields the note doesn't set, to choose one and edit it. */
export function AddField({ source }: { source: EntitySource | undefined }) {
  const editing = useEditing()!
  const [name, setName] = useState('')
  const select = useRef<HTMLSelectElement>(null)
  useEffect(() => select.current?.focus(), [])
  // A field written with no value is unset too (format.md F-FLD-4).
  const unset = source?.fieldTypes.filter((t) => rawOf(source, t.name) == null) ?? []
  return (
    <form
      className="inline-form"
      aria-label="Add a field"
      onSubmit={(e) => {
        e.preventDefault()
        if (name) {
          editing.open({ kind: 'field', name })
        }
      }}
      onKeyDown={(e) => {
        if (e.key === 'Escape') {
          e.preventDefault()
          editing.close()
        }
      }}
    >
      {unset.length === 0 ? (
        <p>The note sets every field its type has. Fields of another kind go in the file: use Edit file.</p>
      ) : (
        <label>
          Field
          <select ref={select} value={name} onChange={(e) => setName(e.target.value)} required>
            <option value="">Choose a field…</option>
            {unset.map((t) => (
              <option key={t.name} value={t.name}>
                {t.name}: {typeWords(t)}
              </option>
            ))}
          </select>
        </label>
      )}
      <p className="editor-actions">
        {unset.length > 0 && (
          <button type="submit" className="primary" disabled={!name}>
            Add field
          </button>
        )}
        <button type="button" onClick={() => editing.close()}>
          Cancel
        </button>
      </p>
    </form>
  )
}

/**
 * Edits one field in place, with controls for its type. Save (or
 * Ctrl/⌘+Enter) sends the changes with the hash the form was based on;
 * Cancel (or Escape) leaves it as it was. Clear field takes it out.
 */
export function FieldEditor({ name, positions }: { name: string; positions: PositionView[] }) {
  const editing = useEditing()!
  const afterSave = useAfterSave()
  const source = useQuery({ ...sourceQuery(editing.universe, editing.entityKey), refetchOnMount: 'always' })
  const fresh = source.isFetchedAfterMount && !source.isFetching
  const [base, setBase] = useState<EntitySource | null>(null)
  const [draft, setDraft] = useState<FieldDraft | null>(null)
  const [initial, setInitial] = useState('')
  const [problems, setProblems] = useState<Problem[]>([])
  const [error, setError] = useState<Error | null>(null)
  const [saving, setSaving] = useState(false)
  const [conflict, setConflict] = useState<EntitySource | null>(null)
  const [clearing, setClearing] = useState(false)
  // True when the save that met a conflict was Clear field, so Keep mine clears it.
  const [pendingClear, setPendingClear] = useState(false)
  const idBase = `f${useId().replace(/[^a-zA-Z0-9]/g, '')}`
  const type = base?.fieldTypes.find((t) => t.name === name)

  // The form is made once, from the file as fetched when it opened.
  useEffect(() => {
    if (fresh && source.data && base === null) {
      setBase(source.data)
      const t = source.data.fieldTypes.find((x) => x.name === name)
      const d = t ? fieldDraftOf(t, rawOf(source.data, name)) : null
      if (d && typeof d !== 'string') {
        setDraft(d)
        setInitial(JSON.stringify(d))
      }
    }
  }, [fresh, source.data, base, name])

  const { setDirty } = editing
  const dirty = draft !== null && JSON.stringify(draft) !== initial
  useEffect(() => setDirty(dirty), [dirty, setDirty])

  // Focus goes to the first control when the form opens.
  const first = draft?.entries[0]
  const firstId = first ? controlId(idBase, first, first.value.kind === 'list' && first.value.items.length === 0 ? 'add-item' : 'value', first.value.kind === 'list' ? first.value.items[0] : undefined) : null
  const focused = useRef(false)
  useEffect(() => {
    if (firstId && !focused.current) {
      focused.current = true
      document.getElementById(firstId)?.focus()
    }
  }, [firstId])

  if (source.isError) {
    return <ErrorState error={source.error} />
  }

  if (!base) {
    return <Loading what="the note's source" />
  }

  if (!type || !draft) {
    const why = !type ? `This note's type doesn't define ${name}.` : (fieldDraftOf(type, rawOf(base, name)) as string)
    return (
      <div className="editor-panel" role="alert">
        <p>
          {why} Change it with Edit file.
        </p>
        <p className="editor-actions">
          <button type="button" onClick={() => editing.open({ kind: 'file' })}>
            Edit file
          </button>
          <button type="button" onClick={() => editing.close()}>
            Close
          </button>
        </p>
      </div>
    )
  }

  const set = (entries: EntryDraft[]) => setDraft({ ...draft, entries })
  const setEntry = (entry: EntryDraft, next: Partial<EntryDraft>) => set(draft.entries.map((e) => (e.id === entry.id ? { ...e, ...next } : e)))

  /** The changes to send on `on`: as worked out, or, where the field itself changed meanwhile, its whole new value. */
  const changesOn = (on: EntitySource): FieldChange[] => {
    const before = rawOf(base, name)
    if (on === base || JSON.stringify(rawOf(on, name)) === JSON.stringify(before)) {
      return fieldChanges(type, before, draft)
    }

    const after = jsonOfField(type, draft).value
    return after === undefined ? [{ op: 'remove', path: [] }] : [{ op: 'set', path: [], value: after }]
  }

  /**
   * Sends the field's changes on `on`, or, for Clear field, its removal. If
   * the file changed meanwhile but this field didn't (another field or a
   * section did), nothing of the field is at stake, so it's sent again on
   * the new file; otherwise the writer sees both values (a conflict).
   */
  const send = async (on: EntitySource, clear: boolean) => {
    setSaving(true)
    setError(null)
    let at = on
    try {
      for (let attempt = 0; ; attempt++) {
        if (clear && rawOf(at, name) === undefined) {
          // Already gone from the file: nothing to clear.
          editing.close()
          return
        }

        const changes: FieldChange[] = clear ? [{ op: 'remove', path: [] }] : changesOn(at)
        if (changes.length === 0) {
          // Nothing to save: no request, and the file is left as it is.
          editing.close()
          return
        }

        try {
          const result = await postEdit('field', { entity: editing.entityKey, universe: editing.universe, expectedHash: at.hash, field: name, changes })
          await afterSave()
          editing.close({ what: clear ? `the note (cleared ${name})` : `the field ${name}`, result })
          return
        } catch (e) {
          const current = e instanceof EditError && e.code === 'stale_file' ? e.current : null
          if (current && attempt < 2 && JSON.stringify(rawOf(current, name)) === JSON.stringify(rawOf(base, name))) {
            at = current
            continue
          }

          throw e
        }
      }
    } catch (e) {
      if (e instanceof EditError && e.code === 'stale_file' && e.current) {
        setPendingClear(clear)
        setConflict(e.current)
      } else {
        setError(e as Error)
      }
    } finally {
      setSaving(false)
    }
  }

  const save = async (on: EntitySource = base) => {
    if (saving) {
      return
    }

    const found = fieldProblems(type, draft, idBase)
    setProblems(found)
    if (found.length > 0) {
      document.getElementById(found[0].at)?.focus()
      return
    }

    await send(on, false)
  }

  if (conflict) {
    return (
      <Conflict
        what={`the field ${name}`}
        mine={pendingClear ? valueLines(undefined) : valueLines(jsonOfField(type, draft).value)}
        theirs={valueLines(rawOf(conflict, name))}
        saving={saving}
        onKeepMine={() => {
          const on = conflict
          setConflict(null)
          void (pendingClear ? send(on, true) : save(on))
        }}
        onTakeTheirs={() => {
          void afterSave()
          editing.close()
        }}
      />
    )
  }

  const temporal = type.temporal
  const isSet = rawOf(base, name) !== undefined
  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault()
      void save()
    } else if (e.key === 'Escape' && !e.defaultPrevented) {
      e.preventDefault()
      if (clearing) {
        setClearing(false)
      } else {
        editing.cancel()
      }
    }
  }

  return (
    <form
      className="editor-panel inline-form field-editor"
      aria-label={`Edit ${name}`}
      noValidate
      onSubmit={(e) => {
        e.preventDefault()
        void save()
      }}
      onKeyDown={onKeyDown}
    >
      <p className="field-about">
        <strong>{name}</strong>: {typeWords(type)}
        {type.description && <span className="muted"> {type.description}</span>}
      </p>
      {draft.entries.map((entry, i) => (
        <EntryEditor
          key={entry.id}
          idBase={idBase}
          type={type}
          entry={entry}
          index={i}
          count={draft.entries.length}
          dated={temporal || draft.shape === 'entries'}
          positions={positions}
          problems={problems}
          onChange={(next) => setEntry(entry, next)}
          onRemove={() => set(draft.entries.filter((e) => e.id !== entry.id))}
        />
      ))}
      {temporal && (
        <p>
          <button type="button" onClick={() => set([...draft.entries, newEntry(type)])}>
            Add a dated entry
          </button>{' '}
          <span className="muted">A new value from a later chapter: the one before it holds until then.</span>
        </p>
      )}
      {draft.entries.length === 0 && <p className="muted">No entries: saving takes {name} out of the note.</p>}
      {problems.length > 0 && (
        <ul className="problems" role="alert">
          {problems.map((p) => (
            <li key={`${p.at}:${p.message}`}>{p.message}</li>
          ))}
        </ul>
      )}
      {error && <SaveError error={error} />}
      <DiscardPrompt />
      {clearing && (
        <div className="confirm" role="group" aria-labelledby={`${idBase}-clear`}>
          <p id={`${idBase}-clear`}>Clear {name}? It's taken out of the note's file, with every entry it has.</p>
          <p className="editor-actions">
            <button type="button" className="danger" disabled={saving} onClick={() => void send(base, true)}>
              {saving ? 'Clearing…' : 'Clear field'}
            </button>
            <button type="button" autoFocus onClick={() => setClearing(false)}>
              Keep it
            </button>
          </p>
        </div>
      )}
      <p className="editor-actions">
        <button type="submit" className="primary" disabled={saving || !dirty}>
          {saving ? 'Saving…' : 'Save'}
        </button>
        <button type="button" onClick={editing.cancel} disabled={saving}>
          Cancel
        </button>
        {isSet && !clearing && (
          <button type="button" className="danger" onClick={() => setClearing(true)} disabled={saving}>
            Clear field
          </button>
        )}
        <span className="muted">Ctrl+Enter (⌘+Enter) saves, Escape cancels.</span>
      </p>
    </form>
  )
}

/** When an entry holds, in words: "from Dracula chapter 3 until Dracula chapter 9". */
function spanWords(entry: EntryDraft, positions: PositionView[]): string {
  const from = entry.from.trim() ? `from ${describePosition(entry.from.trim(), positions)}` : 'from the start'
  return entry.until.trim() ? `${from} until ${describePosition(entry.until.trim(), positions)}` : from
}

function EntryEditor({
  idBase,
  type,
  entry,
  index,
  count,
  dated,
  positions,
  problems,
  onChange,
  onRemove,
}: {
  idBase: string
  type: SourceFieldType
  entry: EntryDraft
  index: number
  count: number
  /** True for a field whose value changes over the story, or a list of dated entries: each has its own From and Until. */
  dated: boolean
  positions: PositionView[]
  problems: Problem[]
  onChange: (next: Partial<EntryDraft>) => void
  onRemove: () => void
}) {
  const id = (part: string, item?: ItemDraft) => controlId(idBase, entry, part, item)
  const value = (
    <ValueEditor idBase={idBase} type={type} entry={entry} positions={positions} problems={problems} onChange={(v) => onChange({ value: v })} />
  )
  const facts = (
    <>
      <PositionInput
        id={id('from')}
        label={dated ? 'From' : 'Known from'}
        none={dated ? 'The start of the story' : 'The start (the reader always knows it)'}
        value={entry.from}
        positions={positions}
        onChange={(from) => onChange({ from })}
      />
      {(dated || entry.until !== '') && (
        <PositionInput
          id={id('until')}
          label="Until"
          none="No end (until the next entry)"
          value={entry.until}
          positions={positions}
          onChange={(until) => onChange({ until })}
        />
      )}
      <label htmlFor={id('status')}>
        Status
        <select id={id('status')} value={entry.status} onChange={(e) => onChange({ status: e.target.value })}>
          <option value="">The note’s status</option>
          {STATUSES.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
          {entry.status !== '' && !(STATUSES as readonly string[]).includes(entry.status) && <option value={entry.status}>{entry.status} (as written)</option>}
        </select>
      </label>
      <label className="checkbox" htmlFor={id('pin')}>
        <input id={id('pin')} type="checkbox" checked={entry.pin} onChange={(e) => onChange({ pin: e.target.checked })} /> Pinned (hard canon)
      </label>
    </>
  )

  if (!dated) {
    const open = entry.from !== '' || entry.until !== '' || entry.status !== '' || entry.pin
    return (
      <div className="entry">
        {value}
        <details className="entry-facts" open={open}>
          <summary>When the reader learns it, its status, and pin</summary>
          {facts}
        </details>
      </div>
    )
  }

  return (
    <fieldset className="entry">
      <legend>
        Entry {index + 1} of {count}: holds {spanWords(entry, positions)}
      </legend>
      {value}
      {facts}
      <p className="editor-actions">
        <button type="button" className="danger" onClick={onRemove} aria-label={`Remove entry ${index + 1}`}>
          Remove entry
        </button>
      </p>
    </fieldset>
  )
}

function ValueEditor({
  idBase,
  type,
  entry,
  positions,
  problems,
  onChange,
}: {
  idBase: string
  type: SourceFieldType
  entry: EntryDraft
  positions: PositionView[]
  problems: Problem[]
  onChange: (value: EntryDraft['value']) => void
}) {
  const v = entry.value
  const problemFor = (at: string) => problems.find((p) => p.at === at)?.message
  if (v.kind === 'one') {
    const at = controlId(idBase, entry, 'value')
    return (
      <ValueInput id={at} label="Value" type={type} draft={v.draft} positions={positions} problem={problemFor(at)} onChange={(draft) => onChange({ kind: 'one', draft })} />
    )
  }

  const setItems = (items: ItemDraft[]) => onChange({ ...v, items })
  const move = (i: number, by: number) => {
    const items = [...v.items]
    const [item] = items.splice(i, 1)
    items.splice(i + by, 0, item)
    setItems(items)
    // Focus follows the item it moved.
    setTimeout(() => document.getElementById(`${controlId(idBase, entry, 'value', item)}-${by < 0 ? 'up' : 'down'}`)?.focus(), 0)
  }

  return (
    <div className="list-editor" role="group" aria-label="Items">
      <ol>
        {v.items.map((item, i) => {
          const at = controlId(idBase, entry, 'value', item)
          return (
            <li key={item.id}>
              <ValueInput
                id={at}
                label={`Item ${i + 1}`}
                type={type}
                draft={item.draft}
                positions={positions}
                problem={problemFor(at)}
                onChange={(draft) => setItems(v.items.map((x) => (x.id === item.id ? { ...x, draft } : x)))}
              />
              <span className="item-tools">
                <button type="button" id={`${at}-up`} onClick={() => move(i, -1)} disabled={i === 0} aria-label={`Move item ${i + 1} up`}>
                  ↑
                </button>
                <button type="button" id={`${at}-down`} onClick={() => move(i, 1)} disabled={i === v.items.length - 1} aria-label={`Move item ${i + 1} down`}>
                  ↓
                </button>
                <button
                  type="button"
                  onClick={() => {
                    setItems(v.items.filter((x) => x.id !== item.id))
                    setTimeout(() => document.getElementById(controlId(idBase, entry, 'add-item'))?.focus(), 0)
                  }}
                  aria-label={`Remove item ${i + 1}`}
                >
                  Remove
                </button>
              </span>
            </li>
          )
        })}
      </ol>
      <button
        type="button"
        id={controlId(idBase, entry, 'add-item')}
        onClick={() => {
          const item = newItem()
          setItems([...v.items, item])
          setTimeout(() => document.getElementById(controlId(idBase, entry, 'value', item))?.focus(), 0)
        }}
      >
        Add item
      </button>
      {problemFor(controlId(idBase, entry, 'add-item')) && <p className="problem">{problemFor(controlId(idBase, entry, 'add-item'))}</p>}
    </div>
  )
}

/** A control for one value of the field's type. */
function ValueInput({
  id,
  label,
  type,
  draft,
  positions,
  problem,
  onChange,
}: {
  id: string
  label: string
  type: SourceFieldType
  draft: Draft
  positions: PositionView[]
  problem: string | undefined
  onChange: (draft: Draft) => void
}) {
  const universe = useEditing()!.universe
  const text = (t: string) => onChange({ ...draft, text: t })
  const invalid = problem !== undefined
  const described = invalid ? `${id}-problem` : undefined
  const note = invalid ? (
    <span className="problem" id={`${id}-problem`}>
      {problem}
    </span>
  ) : null
  const common = { id, 'aria-invalid': invalid || undefined, 'aria-describedby': described }
  /** An option for a value as written that the type's choices don't have, so the form doesn't change it unasked. */
  const written = (choices: string[]) =>
    draft.text !== '' && !choices.includes(draft.text) ? <option value={draft.text}>{draft.text} (as written; not one of the choices)</option> : null

  let control: ReactNode
  switch (type.type) {
    case 'text':
      control = <textarea {...common} value={draft.text} rows={Math.min(8, Math.max(2, draft.text.split('\n').length))} onChange={(e) => text(e.target.value)} />
      break
    case 'number':
      control = <input {...common} inputMode="decimal" value={draft.text} onChange={(e) => text(e.target.value)} />
      break
    case 'bool':
      control = (
        <select {...common} value={draft.text} onChange={(e) => text(e.target.value)}>
          {draft.text === '' && <option value="">Choose…</option>}
          <option value="true">Yes (true)</option>
          <option value="false">No (false)</option>
          {written(['true', 'false'])}
        </select>
      )
      break
    case 'enum':
      control = (
        <select {...common} value={draft.text} onChange={(e) => text(e.target.value)}>
          {draft.text === '' && <option value="">Choose…</option>}
          {type.values.map((v) => (
            <option key={v} value={v}>
              {v}
            </option>
          ))}
          {written(type.values)}
        </select>
      )
      break
    case 'ref':
      return <RefInput {...{ id, label, draft, universe, to: type.to, note, invalid, onChange }} />
    case 'position':
      return (
        <>
          <PositionInput id={id} label={label} none="None" value={draft.text} positions={positions} onChange={text} />
          {note}
        </>
      )
    case 'date':
      return (
        <fieldset className="date-input">
          <legend>{label}</legend>
          <label htmlFor={id}>
            Date
            <input {...common} value={draft.text} onChange={(e) => text(e.target.value)} placeholder="1893-08-08" />
          </label>
          <label htmlFor={`${id}-sort`}>
            Sort key (optional)
            <input id={`${id}-sort`} value={draft.sort} onChange={(e) => onChange({ ...draft, sort: e.target.value })} placeholder="1893, 8, 8" />
          </label>
          <span className="muted">
            Write 1893-08-08, or 1893-08-08 06:40 with the time of day, and it sorts by itself. Free text, such as “late spring”, sorts only with a sort key.
          </span>
          {note}
        </fieldset>
      )
    default:
      control = <input {...common} value={draft.text} onChange={(e) => text(e.target.value)} />
  }

  return (
    <div className="value-input">
      <label htmlFor={id}>
        {label}
        {control}
      </label>
      {note}
    </div>
  )
}

/**
 * A story position: a book and a chapter from the den's books, or, with
 * "Type a position", any position as text, such as one for a chapter the
 * book doesn't list yet.
 */
function PositionInput({
  id,
  label,
  none,
  value,
  positions,
  onChange,
}: {
  id: string
  label: string
  none: string
  value: string
  positions: PositionView[]
  onChange: (position: string) => void
}) {
  const books = useMemo(() => {
    const seen = new Map<string, string>()
    for (const p of positions) {
      seen.set(p.book, p.bookName)
    }

    return [...seen].map(([key, name]) => ({ key, name }))
  }, [positions])
  const known = positions.find((p) => p.position === value && p.chapter !== null)
  const isBook = books.some((b) => b.key === value)
  const [typing, setTyping] = useState(value !== '' && !known && !isBook)
  const book = typing ? '*' : known ? known.book : isBook ? value : ''
  return (
    <fieldset className="position-input">
      <legend>{label}</legend>
      <label htmlFor={id}>
        Book
        <select
          id={id}
          value={book}
          onChange={(e) => {
            const next = e.target.value
            setTyping(next === '*')
            if (next !== '*') {
              onChange(next)
            }
          }}
        >
          <option value="">{none}</option>
          {books.map((b) => (
            <option key={b.key} value={b.key}>
              {b.name}
            </option>
          ))}
          <option value="*">Type a position…</option>
        </select>
      </label>
      {book !== '' && book !== '*' && (
        <label htmlFor={`${id}-chapter`}>
          Chapter
          <select id={`${id}-chapter`} value={known ? known.position : book} onChange={(e) => onChange(e.target.value)}>
            <option value={book}>The start of the book</option>
            {positions
              .filter((p) => p.book === book && p.chapter !== null)
              .map((p) => (
                <option key={p.position} value={p.position}>
                  {positionLabel(p).slice(p.bookName.length + 2)}
                </option>
              ))}
          </select>
        </label>
      )}
      {typing && (
        <label htmlFor={`${id}-text`}>
          Position
          <input id={`${id}-text`} value={value} onChange={(e) => onChange(e.target.value)} placeholder="book:chapter" autoFocus />
        </label>
      )}
    </fieldset>
  )
}

/**
 * A link to one note: type a few letters of its name, and pick it from the
 * notes the search finds (of the field's types, if it has them), with the
 * arrow keys and Enter, or the mouse. It writes the link as the den writes
 * them, `[[slug]]`. A link can also be typed in full.
 */
function RefInput({
  id,
  label,
  draft,
  universe,
  to,
  note,
  invalid,
  onChange,
}: {
  id: string
  label: string
  draft: Draft
  universe: string
  to: string[] | null
  note: ReactNode
  invalid: boolean
  onChange: (draft: Draft) => void
}) {
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const words = draft.text.replace(/^\[\[/, '').replace(/\]\]$/, '').trim()
  const searchable = open && /[\p{L}\p{N}]$/u.test(words)
  const search = useQuery({
    queryKey: ['ref-search', universe, words, to],
    queryFn: () => getJson<SearchResult>(apiPath('/api/search', { query: `${words}*`, universe, limit: 20, type: to?.length === 1 ? to[0] : undefined })),
    enabled: searchable,
    staleTime: 30_000,
  })
  const options = useMemo(() => {
    const seen = new Set<string>()
    return (search.data?.hits ?? [])
      .map((h) => h.entity)
      // Only notes whose name or slug matches what's typed, not ones that just mention it.
      .filter((e) => (!to || to.includes(e.type)) && nameMatches(e, words) && !seen.has(e.key) && (seen.add(e.key), true))
      .slice(0, 8)
  }, [search.data, to, words])
  const listId = `${id}-options`
  const showing = searchable && options.length > 0
  // Every note of the universe, to tell whether a slug reaches only the note picked.
  const all = useQuery(entitiesQuery(universe, undefined))
  const pick = (entity: { path: string; name: string; id: string | null }) => {
    onChange({ ...draft, text: linkFor(entity, all.data && !all.data.truncated ? all.data.entities : null) })
    setOpen(false)
  }

  return (
    <div className="ref-input">
      <label htmlFor={id}>{label}</label>
      <input
        id={id}
        role="combobox"
        aria-expanded={showing}
        aria-controls={showing ? listId : undefined}
        aria-autocomplete="list"
        aria-activedescendant={showing ? `${listId}-${active}` : undefined}
        aria-invalid={invalid || undefined}
        aria-describedby={invalid ? `${id}-problem` : `${id}-hint`}
        value={draft.text}
        autoComplete="off"
        onChange={(e) => {
          onChange({ ...draft, text: e.target.value })
          setOpen(true)
          setActive(0)
        }}
        onBlur={() => setTimeout(() => setOpen(false), 150)}
        onKeyDown={(e) => {
          if (!showing) {
            return
          }

          if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            e.preventDefault()
            setActive((a) => (a + (e.key === 'ArrowDown' ? 1 : options.length - 1)) % options.length)
          } else if (e.key === 'Enter' && !e.ctrlKey && !e.metaKey) {
            e.preventDefault()
            pick(options[active])
          } else if (e.key === 'Escape') {
            // Closes the list, not the editor.
            e.preventDefault()
            e.stopPropagation()
            setOpen(false)
          }
        }}
      />
      <span className="muted" id={`${id}-hint`}>
        Type a few letters of the note’s name, then pick it.
      </span>
      {showing && (
        <ul className="ref-options" role="listbox" id={listId} aria-label={`Notes for ${label}`}>
          {options.map((e, i) => (
            <li
              key={e.key}
              id={`${listId}-${i}`}
              role="option"
              aria-selected={i === active}
              onMouseDown={(ev) => {
                ev.preventDefault()
                pick(e)
              }}
            >
              {e.name} <span className="muted">{e.type}</span>
            </li>
          ))}
        </ul>
      )}
      {note}
    </div>
  )
}
