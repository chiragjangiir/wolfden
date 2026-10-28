import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent as ReactKeyboardEvent,
  type ReactNode,
} from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useNavigate, useParams } from '@tanstack/react-router'
import { entitiesQuery, hierarchyQuery, positionsQuery, type PositionView, type UniverseNode } from '../api'
import { CreateError, createEntity, typesQuery, type NewScope, type TypeView } from '../createApi'
import type { WriteResult } from '../editApi'
import { useAfterSave } from './Editing'
import { usePosition } from '../hooks'
import { describePosition } from '../story'
import '../newEntity.css'

/**
 * Creating notes from type templates (#29; maintainer decisions on #29,
 * 2026-10-28). A short form asks for the type, the name, and where the note
 * goes (the universe, or one of its series or books, by default where the
 * writer is); Wolfden makes the file from the type's template and opens the
 * new note's page, where its fields and sections are edited one at a time
 * (#28). The new note has no `from`, so it's canon from the start of where
 * it goes, unless the writer is viewing at a story position and ticks
 * "Enters the story at …".
 *
 * Two ways in: the top bar's **New ▾** menu (any type) and a **New <type>**
 * button in each type's list in the contents tree (the type and the place
 * filled in).
 */

interface NewEntityRequest {
  type?: string
  scope?: NewScope
}

interface Created {
  universe: string
  key: string
  result: WriteResult
  typeName: string
  /** True once the new note's page has been shown, so leaving it clears the notice. */
  seen: boolean
}

interface NewEntityContextValue {
  open: (request: NewEntityRequest) => void
}

const NewEntityContext = createContext<NewEntityContextValue | null>(null)

/** Opens the New note form, with the type and the place filled in. */
export function useNewEntity(): NewEntityContextValue['open'] {
  const context = useContext(NewEntityContext)
  if (!context) {
    throw new Error('useNewEntity is used outside NewEntityProvider.')
  }

  return context.open
}

/**
 * Holds the New note form for the whole app, and what the last create made,
 * so the new note's page can say which file it is (#29: the file is shown
 * after the create, not before). The notice lasts until the writer leaves
 * the new note's page.
 */
export function NewEntityProvider({ universe, children }: { universe: string | undefined; children: ReactNode }) {
  const [request, setRequest] = useState<NewEntityRequest | null>(null)
  const [created, setCreated] = useState<Created | null>(null)
  const returnFocus = useRef<HTMLElement | null>(null)
  const params = useParams({ strict: false }) as { universe?: string; _splat?: string }
  const onCreatedPage = created !== null && params.universe === created.universe && params._splat === created.key

  // The notice is for the new note's page: once that's been shown, going anywhere else ends it.
  useEffect(() => {
    if (!created) {
      return
    }

    if (onCreatedPage && !created.seen) {
      setCreated({ ...created, seen: true })
    } else if (!onCreatedPage && created.seen) {
      setCreated(null)
    }
  }, [created, onCreatedPage])

  const open = useCallback((next: NewEntityRequest) => {
    returnFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    setRequest(next)
  }, [])

  const close = useCallback(() => {
    setRequest(null)
    // Back to what opened the form, as the checkpoint dialog does.
    const target = returnFocus.current
    requestAnimationFrame(() => target?.focus())
  }, [])

  const value = useMemo(() => ({ open }), [open])

  return (
    <NewEntityContext.Provider value={value}>
      <CreatedContext.Provider value={onCreatedPage ? { created: created!, dismiss: () => setCreated(null) } : null}>{children}</CreatedContext.Provider>
      {request && universe && (
        <NewEntityDialog
          universe={universe}
          request={request}
          onClose={close}
          onCreated={(result, typeName) => {
            setRequest(null)
            setCreated({ universe, key: result.entity!.key, result, typeName, seen: false })
          }}
        />
      )}
    </NewEntityContext.Provider>
  )
}

const CreatedContext = createContext<{ created: Created; dismiss: () => void } | null>(null)

/** On the new note's page, what the create made: its file, and any warnings. Focus moves here, so a screen reader hears it. */
export function CreatedNotice() {
  const shown = useContext(CreatedContext)
  const ref = useRef<HTMLDivElement>(null)
  const key = shown?.created.result.path
  useEffect(() => {
    if (key) {
      ref.current?.focus()
    }
  }, [key])

  if (!shown) {
    return null
  }

  const { result, typeName } = shown.created
  return (
    <div className="save-result created-notice" role="status" tabIndex={-1} ref={ref}>
      <p>
        Created <code>{result.path}</code> from the {typeName} template. Fill in its fields and sections below: each has its own Edit button.
      </p>
      {result.warnings.length > 0 && (
        <ul className="warnings">
          {result.warnings.map((w) => (
            <li key={`${w.rule}:${w.line ?? ''}:${w.message}`}>
              {w.rule}: {w.message}
            </li>
          ))}
        </ul>
      )}
      <p className="editor-actions">
        <button type="button" onClick={shown.dismiss}>
          Dismiss
        </button>
      </p>
    </div>
  )
}

/**
 * The top bar's **New ▾** menu: every type the den's packs define. It's a
 * menu button: Enter, Space, or the down arrow opens it on the first type,
 * the arrows, Home, and End move through the types, Escape closes it and
 * returns to the button, and choosing a type opens the New note form.
 */
export function NewMenu({ universe }: { universe: string }) {
  const types = useQuery(typesQuery)
  const openForm = useNewEntity()
  const scope = useContextScope(universe)
  const [open, setOpen] = useState(false)
  const menuId = useId()
  const buttonRef = useRef<HTMLButtonElement>(null)
  const menuRef = useRef<HTMLUListElement>(null)
  const [focusOn, setFocusOn] = useState<'first' | 'last' | null>(null)

  const items = () => [...(menuRef.current?.querySelectorAll<HTMLElement>('[role="menuitem"]:not([aria-disabled="true"])') ?? [])]

  useEffect(() => {
    if (!open || !focusOn) {
      return
    }

    const all = items()
    const target = focusOn === 'first' ? all[0] : all[all.length - 1]
    ;(target ?? menuRef.current)?.focus()
    setFocusOn(null)
  }, [open, focusOn, types.data])

  // A click anywhere else closes it.
  useEffect(() => {
    if (!open) {
      return
    }

    const onDown = (e: MouseEvent) => {
      const target = e.target as Node
      if (!menuRef.current?.contains(target) && !buttonRef.current?.contains(target)) {
        setOpen(false)
      }
    }
    document.addEventListener('mousedown', onDown)
    return () => document.removeEventListener('mousedown', onDown)
  }, [open])

  const show = (at: 'first' | 'last') => {
    setOpen(true)
    setFocusOn(at)
  }

  const closeMenu = (refocus: boolean) => {
    setOpen(false)
    if (refocus) {
      buttonRef.current?.focus()
    }
  }

  const choose = (type: TypeView) => {
    setOpen(false)
    // The form returns focus to the menu button when it closes.
    buttonRef.current?.focus()
    openForm({ type: type.key, scope })
  }

  const onMenuKey = (e: ReactKeyboardEvent<HTMLUListElement>) => {
    const all = items()
    const at = all.indexOf(document.activeElement as HTMLElement)
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault()
        all[(at + 1) % all.length]?.focus()
        break
      case 'ArrowUp':
        e.preventDefault()
        all[(at - 1 + all.length) % all.length]?.focus()
        break
      case 'Home':
        e.preventDefault()
        all[0]?.focus()
        break
      case 'End':
        e.preventDefault()
        all[all.length - 1]?.focus()
        break
      case 'Escape':
        e.preventDefault()
        e.stopPropagation()
        closeMenu(true)
        break
      case 'Tab':
        closeMenu(false)
        break
    }
  }

  return (
    <div className="new-menu">
      <button
        type="button"
        ref={buttonRef}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        onClick={() => (open ? closeMenu(false) : show('first'))}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown') {
            e.preventDefault()
            show('first')
          } else if (e.key === 'ArrowUp') {
            e.preventDefault()
            show('last')
          }
        }}
      >
        New <span aria-hidden="true">▾</span>
      </button>
      {open && (
        <ul id={menuId} role="menu" aria-label="New note" className="new-menu-list" ref={menuRef} tabIndex={-1} onKeyDown={onMenuKey}>
          {types.isPending ? (
            <li role="menuitem" aria-disabled="true" tabIndex={-1}>
              Loading the types…
            </li>
          ) : types.isError ? (
            <li role="menuitem" aria-disabled="true" tabIndex={-1}>
              The types couldn't be read: {types.error.message}
            </li>
          ) : (
            types.data.types.map((t) => (
              <li
                key={t.key}
                role="menuitem"
                tabIndex={-1}
                title={t.description ?? undefined}
                onClick={() => choose(t)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault()
                    choose(t)
                  }
                }}
              >
                {t.name}
              </li>
            ))
          )}
        </ul>
      )}
    </div>
  )
}

/** A type list's **New <type>** button, with the type and the place filled in. */
export function NewTypeButton({ type, scope, scopeName }: { type: string; scope: NewScope; scopeName: string }) {
  const types = useQuery(typesQuery)
  const openForm = useNewEntity()
  const known = types.data?.types.find((t) => t.key === type)
  if (types.isSuccess && !known) {
    // A type no enabled pack defines any more: there's no template to make one from.
    return null
  }

  const name = known?.name ?? type
  return (
    <button type="button" className="new-type" aria-label={`New ${name} in ${scopeName}`} onClick={() => openForm({ type, scope })}>
      New {name}
    </button>
  )
}

/**
 * Where the writer is, as a place for a new note: the book of a book or
 * chapter page, the series of a series page, the place a note is filed on
 * its page, and otherwise the universe.
 */
function useContextScope(universe: string): NewScope {
  const params = useParams({ strict: false }) as { book?: string; series?: string; _splat?: string }
  const all = useQuery({ ...entitiesQuery(universe, undefined), enabled: params._splat !== undefined })
  if (params.book) {
    return { kind: 'book', key: params.book }
  }

  if (params.series) {
    return { kind: 'series', key: params.series }
  }

  const here = params._splat === undefined ? undefined : all.data?.entities.find((e) => e.key === params._splat || e.id === params._splat || e.path === params._splat)
  return here ? { kind: here.scopeKind, key: here.scopeKey } : { kind: 'universe', key: universe }
}

interface Place {
  value: string
  label: string
  scope: NewScope
  name: string
  final: boolean
}

/** The places a note can go, in story order: the universe, then each series and its books, then the standalone books. */
function placesIn(u: UniverseNode): Place[] {
  const places: Place[] = [{ value: `universe:${u.key}`, label: `${u.name} (the whole universe)`, scope: { kind: 'universe', key: u.key }, name: u.name, final: false }]
  const book = (b: UniverseNode['books'][number], inSeries: boolean): Place => ({
    value: `book:${b.key}`,
    label: `${inSeries ? ' ' : ''}${b.name} (book${b.status === 'final' ? ', final' : ''})`,
    scope: { kind: 'book', key: b.key },
    name: b.name,
    final: b.status === 'final',
  })
  for (const s of u.series) {
    places.push({ value: `series:${s.key}`, label: `${s.name} (series)`, scope: { kind: 'series', key: s.key }, name: s.name, final: false })
    places.push(...s.books.map((b) => book(b, true)))
  }

  places.push(...u.books.map((b) => book(b, false)))
  return places
}

/** Keeps Tab and Shift+Tab inside the dialog, as the checkpoint dialog does. */
function trapTab(e: ReactKeyboardEvent<HTMLDivElement>, dialog: HTMLDivElement | null) {
  const focusable = [...(dialog?.querySelectorAll<HTMLElement>('button, input, select, textarea, a[href]') ?? [])].filter((el) => !el.hasAttribute('disabled'))
  if (focusable.length === 0) {
    e.preventDefault()
    dialog?.focus()
    return
  }

  const first = focusable[0]
  const last = focusable[focusable.length - 1]
  if (e.shiftKey && (document.activeElement === first || document.activeElement === dialog)) {
    e.preventDefault()
    last.focus()
  } else if (!e.shiftKey && (document.activeElement === last || document.activeElement === dialog)) {
    e.preventDefault()
    first.focus()
  }
}

/** The New note form: the type, the name, and where it goes; then Wolfden makes it and opens it. */
function NewEntityDialog({
  universe,
  request,
  onClose,
  onCreated,
}: {
  universe: string
  request: NewEntityRequest
  onClose: () => void
  onCreated: (result: WriteResult, typeName: string) => void
}) {
  const titleId = useId()
  const descriptionId = useId()
  const typeId = useId()
  const nameId = useId()
  const whereId = useId()
  const nameErrorId = useId()
  const frozenId = useId()
  const dialogRef = useRef<HTMLDivElement>(null)
  const nameRef = useRef<HTMLInputElement>(null)
  const afterSave = useAfterSave()
  const navigate = useNavigate()
  const { at } = usePosition()
  const types = useQuery(typesQuery)
  const hierarchy = useQuery(hierarchyQuery(universe, undefined))
  const positions = useQuery(positionsQuery(universe))

  const [type, setType] = useState(request.type ?? '')
  const [name, setName] = useState('')
  const [missing, setMissing] = useState(false)
  const [where, setWhere] = useState(request.scope ? `${request.scope.kind}:${request.scope.key}` : `universe:${universe}`)
  const [entersHere, setEntersHere] = useState(false)

  const places = hierarchy.data ? placesIn(hierarchy.data.universe) : []
  const place = places.find((p) => p.value === where)
  const typeList = types.data?.types ?? []
  const chosenType = typeList.find((t) => t.key === type)
  // The position to offer as the note's entry: only one the universe has (F-POS-4), and only where the
  // chosen place can see it, so a book's note never enters in another book (F-ENT-6).
  const viewed = at ? positions.data?.positions.find((p) => p.position === at || (p.chapter === null && p.book === at)) : undefined
  const position = viewed && place && placeSees(place, viewed) ? at : undefined

  useEffect(() => {
    nameRef.current?.focus()
  }, [])

  // A place that can't see the position takes the box away, and clears it.
  useEffect(() => {
    if (!position) {
      setEntersHere(false)
    }
  }, [position])

  // The type list may arrive after the form opens: with none chosen, take the first.
  useEffect(() => {
    if (!type && typeList.length > 0) {
      setType(typeList[0].key)
    }
  }, [type, typeList])

  const create = useMutation({
    mutationFn: (allowRelinks: boolean) =>
      createEntity({
        type,
        name: name.trim(),
        universe,
        series: place?.scope.kind === 'series' ? place.scope.key : undefined,
        book: place?.scope.kind === 'book' ? place.scope.key : undefined,
        from: entersHere && position ? position : undefined,
        allowRelinks: allowRelinks || undefined,
      }),
    onSuccess: (result) => {
      // Everything is asked again, as after any save: the tree, the lists, search, link pickers, the checkpoint indicator.
      void afterSave()

      onCreated(result, chosenType?.name ?? type)
      void navigate({ to: '/u/$universe/e/$', params: { universe, _splat: result.entity!.key }, search: {} })
    },
  })

  const busy = create.isPending

  useEffect(() => {
    if (busy) {
      dialogRef.current?.focus()
    }
  }, [busy])

  // Links the new note would take from the note they reach now (#29): listed, with Create anyway.
  const relinks = create.error instanceof CreateError ? create.error.relinks : null
  const relinkRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!create.isError) {
      return
    }

    // To the refusal, so a screen reader hears which links would move; otherwise back to the name, to try again.
    if (relinks) {
      relinkRef.current?.focus()
    } else {
      nameRef.current?.focus()
    }
  }, [create.isError, relinks])

  /** Any change to what would be sent ends a refusal: it was about the old values. */
  const changed = () => {
    if (create.isError) {
      create.reset()
    }
  }

  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (name.trim() === '') {
      setMissing(true)
      nameRef.current?.focus()
      return
    }

    // Belt and braces with the disabled button: one create per submit.
    if (create.isPending || !type || !place || place.final) {
      return
    }

    setMissing(false)
    create.mutate(false)
  }

  const dismiss = () => {
    if (!busy) {
      onClose()
    }
  }

  const loadError = types.error ?? hierarchy.error
  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && dismiss()}>
      <div
        className="dialog new-entity-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={descriptionId}
        aria-busy={busy}
        ref={dialogRef}
        tabIndex={-1}
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            e.stopPropagation()
            dismiss()
          } else if (e.key === 'Tab') {
            trapTab(e, dialogRef.current)
          }
        }}
      >
        <form onSubmit={submit} noValidate>
          <h2 id={titleId}>New {chosenType ? chosenType.name.toLowerCase() : 'note'}</h2>
          <p id={descriptionId} className="muted">
            Wolfden makes the note from its type's template, then opens it for you to fill in.
          </p>
          {loadError && (
            <p className="error" role="alert">
              {loadError.message}
            </p>
          )}
          <label htmlFor={typeId} className="new-entity-label">
            Type
          </label>
          <select id={typeId} value={type} disabled={busy || typeList.length === 0} onChange={(e) => {
              setType(e.currentTarget.value)
              changed()
            }}>
            {typeList.length === 0 && <option value={type}>{types.isPending ? 'Loading…' : type}</option>}
            {typeList.map((t) => (
              <option key={t.key} value={t.key}>
                {t.name}
              </option>
            ))}
          </select>
          {chosenType?.description && <p className="muted new-entity-hint">{chosenType.description}</p>}
          <label htmlFor={nameId} className="new-entity-label">
            Name
          </label>
          <input
            id={nameId}
            ref={nameRef}
            type="text"
            autoComplete="off"
            required
            aria-required="true"
            aria-invalid={missing}
            aria-describedby={missing ? nameErrorId : undefined}
            value={name}
            disabled={busy}
            onChange={(e) => {
              setName(e.currentTarget.value)
              changed()
              if (e.currentTarget.value.trim() !== '') {
                setMissing(false)
              }
            }}
          />
          {missing && (
            <p id={nameErrorId} className="field-error" role="alert">
              Give the note a name. Links reach it by its name, and its file is named from it.
            </p>
          )}
          <label htmlFor={whereId} className="new-entity-label">
            Where it goes
          </label>
          <select
            id={whereId}
            value={where}
            disabled={busy || places.length === 0}
            aria-describedby={place?.final ? frozenId : undefined}
            onChange={(e) => {
              setWhere(e.currentTarget.value)
              changed()
            }}
          >
            {places.length === 0 && <option value={where}>{hierarchy.isPending ? 'Loading…' : where}</option>}
            {places.map((p) => (
              <option key={p.value} value={p.value}>
                {p.label}
              </option>
            ))}
          </select>
          {place?.final && (
            <p id={frozenId} className="field-error">
              {place.name} is final (frozen), so nothing can be added to it. Unfreeze it on its page first, or choose another place.
            </p>
          )}
          {position && (
            <label className="new-entity-check">
              <input type="checkbox" checked={entersHere} disabled={busy} onChange={(e) => {
                  setEntersHere(e.currentTarget.checked)
                  changed()
                }}
              /> Enters the story at{' '}
              {describePosition(position, positions.data?.positions ?? [])}
            </label>
          )}
          {!entersHere && <p className="muted new-entity-hint">{entryHint(place)}</p>}
          {create.isError && !relinks && (
            <div className="error" role="alert">
              <p>{create.error.message}</p>
            </div>
          )}
          {relinks && (
            <div className="banner relink-prompt" role="alert" tabIndex={-1} ref={relinkRef}>
              <p>
                A note called {name.trim()} there would take links that now reach another note, so nothing was created. These links would
                reach the new note instead:
              </p>
              <ul>
                {relinks.map((r) => (
                  <li key={r}>{r}</li>
                ))}
              </ul>
              <p className="muted">Choose another name or place, or create it anyway and check those links afterwards.</p>
            </div>
          )}
          <div className="dialog-actions">
            {relinks ? (
              <button type="button" className="primary" disabled={busy} onClick={() => !create.isPending && create.mutate(true)}>
                {busy ? 'Creating…' : 'Create anyway'}
              </button>
            ) : (
              <button type="submit" className="primary" disabled={busy || !type || !place || place.final}>
                {busy ? 'Creating…' : 'Create'}
              </button>
            )}
            <button type="button" onClick={onClose} disabled={busy}>
              Cancel
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}

/**
 * True if a note filed at `place` can enter the story at `position`: anywhere
 * in the universe, in a series' own books, and in a book only within it.
 */
function placeSees(place: Place, position: PositionView): boolean {
  switch (place.scope.kind) {
    case 'book':
      return position.book === place.scope.key
    case 'series':
      return position.series === place.scope.key
    default:
      return true
  }
}

/** Where the new note enters canon without a position of its own (F-ENT-6). */
function entryHint(place: Place | undefined): string {
  if (!place) {
    return ''
  }

  switch (place.scope.kind) {
    case 'book':
      return `It's canon from the start of ${place.name}.`
    case 'series':
      return `It's canon from the start of ${place.name}'s first book.`
    default:
      return "It's canon from the start of the story."
  }
}
