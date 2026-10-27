import { Suspense, createContext, lazy, useCallback, useContext, useEffect, useId, useMemo, useRef, useState, type ComponentProps, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useBlocker } from '@tanstack/react-router'
import { EditError, postEdit, sourceQuery, type EditPath, type EntitySource, type WriteResult } from '../editApi'
import { Markdown } from './Markdown'
import { ErrorState, Loading } from './States'

// Editing a note in place (#28; maintainer decisions on #28, 2026-10-27):
// one piece at a time (the lead, a section, or the whole file), as Markdown
// in CodeMirror with a preview, saved explicitly. Every save carries the
// hash of the file it was based on; if the file changed meanwhile, the
// writer sees both versions and chooses. While anything is being edited the
// page shows the full note, whatever the story position.

/** What's being edited: one piece of the note, named as the page knows it. */
export type EditTarget =
  | { kind: 'lead' }
  | { kind: 'section'; line: number; name: string }
  | { kind: 'rename'; line: number; name: string }
  | { kind: 'remove'; line: number; name: string }
  | { kind: 'add' }
  | { kind: 'file' }
  | { kind: 'field'; name: string }
  | { kind: 'add-field' }

/** The id of the button that opens `target`, where focus goes back when it closes. */
export function editButtonId(target: EditTarget): string {
  switch (target.kind) {
    case 'lead':
    case 'add':
    case 'file':
    case 'add-field':
      return `edit-${target.kind}`
    case 'field':
      return `edit-field-${target.name}`
    default:
      return `edit-${target.kind}-${target.line}`
  }
}

interface Saved {
  what: string
  result: WriteResult
}

interface Editing {
  universe: string
  entityKey: string
  /** Why the note can't be edited, or null. */
  frozen: string | null
  target: EditTarget | null
  open: (target: EditTarget) => void
  /** Closes the editor; after a save, shows what the save did. */
  close: (saved?: Saved) => void
  setDirty: (dirty: boolean) => void
  /** Cancel or Escape: closes the editor, or with unsaved changes, asks first (DiscardPrompt). */
  cancel: () => void
  /** True while the page asks whether to discard unsaved changes. */
  discarding: boolean
  keepEditing: () => void
  /** The badge, the last save's result, and the prompt before leaving, for EditingStatus. */
  status: ReactNode
}

const EditingContext = createContext<Editing | null>(null)

// CodeMirror is loaded when an editor first opens, so reading the den doesn't wait for it.
const LazyEditor = lazy(() => import('./MarkdownEditor').then((m) => ({ default: m.MarkdownEditor })))

function MarkdownEditor(props: ComponentProps<typeof LazyEditor>) {
  return (
    <Suspense fallback={<Loading what="the editor" />}>
      <LazyEditor {...props} />
    </Suspense>
  )
}

export function useEditing(): Editing | null {
  return useContext(EditingContext)
}

/**
 * Holds what's being edited on an entity's page, warns before leaving with
 * unsaved changes (a navigation in the app, or closing or reloading the
 * tab), and shows what the last save did.
 */
export function EditingProvider({
  universe,
  entityKey,
  frozen,
  target,
  setTarget,
  children,
}: {
  universe: string
  entityKey: string
  frozen: string | null
  target: EditTarget | null
  setTarget: (target: EditTarget | null) => void
  children: ReactNode
}) {
  const [dirty, setDirty] = useState(false)
  const [saved, setSaved] = useState<Saved | null>(null)
  const [discarding, setDiscarding] = useState(false)
  const focusBack = useRef<HTMLElement | null>(null)
  const dirtyRef = useRef(false)
  dirtyRef.current = dirty
  const returnTo = useRef<string | null>(null)
  const shouldBlockFn = useCallback(() => dirtyRef.current, [])
  // Leaving through the app asks in the page; closing or reloading the tab, the browser asks (below).
  const blocker = useBlocker({ shouldBlockFn, enableBeforeUnload: false, withResolver: true })
  useEffect(() => {
    if (!dirty) {
      return
    }

    const warn = (e: BeforeUnloadEvent) => {
      e.preventDefault()
      e.returnValue = ''
    }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])

  const open = useCallback(
    (next: EditTarget) => {
      setSaved(null)
      setDirty(false)
      setDiscarding(false)
      setTarget(next)
    },
    [setTarget],
  )
  const close = useCallback(
    (result?: Saved) => {
      returnTo.current = result ? 'edit-saved' : target ? editButtonId(target) : null
      setDirty(false)
      setDiscarding(false)
      setSaved(result ?? null)
      setTarget(null)
    },
    [setTarget, target],
  )

  const cancel = useCallback(() => {
    if (!dirtyRef.current) {
      close()
      return
    }

    // Focus goes back where it was (inside the editor's shadow root, for CodeMirror) if the writer keeps editing.
    const active = document.activeElement as HTMLElement | null
    focusBack.current = (active?.shadowRoot?.activeElement as HTMLElement | null) ?? active
    setDiscarding(true)
  }, [close])
  const keepEditing = useCallback(() => {
    setDiscarding(false)
    const back = focusBack.current
    focusBack.current = null
    setTimeout(() => back?.focus(), 0)
  }, [])

  // Focus goes back where the edit started, or to what the save did.
  useEffect(() => {
    if (target === null && returnTo.current) {
      document.getElementById(returnTo.current)?.focus()
      returnTo.current = null
    }
  }, [target, saved])

  const value = useMemo<Editing>(
    () => ({
      universe,
      entityKey,
      frozen,
      target,
      open,
      close,
      setDirty,
      cancel,
      discarding,
      keepEditing,
      status: (
        <>
          {blocker.status === 'blocked' && (
            <UnsavedPrompt
              question="You have changes that aren't saved. Leave without saving them?"
              discard="Leave without saving"
              onKeep={() => blocker.reset()}
              onDiscard={() => blocker.proceed()}
            />
          )}
          {target !== null && (
            <p className="editing-badge" role="status">
              Editing shows everything
            </p>
          )}
          {saved && <SaveResult saved={saved} onDismiss={() => setSaved(null)} />}
        </>
      ),
    }),
    [universe, entityKey, frozen, target, open, close, cancel, discarding, keepEditing, blocker, saved],
  )
  return <EditingContext.Provider value={value}>{children}</EditingContext.Provider>
}

/**
 * The page's own question before unsaved changes are dropped, by leaving
 * the page or by Cancel or Escape: "Keep editing" (focused) or discard.
 */
function UnsavedPrompt({ question, discard, onKeep, onDiscard }: { question: string; discard: string; onKeep: () => void; onDiscard: () => void }) {
  const id = useId()
  return (
    <div className="banner leave-prompt" role="alertdialog" aria-labelledby={id}>
      <p id={id}>{question}</p>
      <p className="banner-actions">
        <button type="button" onClick={onKeep} autoFocus>
          Keep editing
        </button>
        <button type="button" onClick={onDiscard}>
          {discard}
        </button>
      </p>
    </div>
  )
}

/** Asks before Cancel or Escape drops unsaved changes, in the editor that has them. */
export function DiscardPrompt() {
  const editing = useEditing()!
  return editing.discarding ? (
    <UnsavedPrompt
      question="Discard the changes you haven't saved?"
      discard="Discard changes"
      onKeep={editing.keepEditing}
      onDiscard={() => editing.close()}
    />
  ) : null
}

/** The editing state the page shows under its heading: the badge, what the last save did, and the prompt before leaving. */
export function EditingStatus() {
  return <>{useEditing()?.status}</>
}

/** What a save did: its effects, the pinned canon it touched, and the file's warnings. */
function SaveResult({ saved, onDismiss }: { saved: Saved; onDismiss: () => void }) {
  const { result } = saved
  return (
    <div className="save-result" role="status" id="edit-saved" tabIndex={-1}>
      <p>
        <strong>{result.changed ? `Saved ${saved.what}.` : `Nothing changed in ${saved.what}: the file already said that.`}</strong>{' '}
        Saving doesn't make a checkpoint.
      </p>
      {result.pinnedCanon.length > 0 && (
        <div className="pinned-canon">
          <p>This touched pinned canon:</p>
          <ul>
            {result.pinnedCanon.map((p) => (
              <li key={p}>{p}</li>
            ))}
          </ul>
        </div>
      )}
      {result.changed && result.effects.length > 0 && (
        <ul className="effects">
          {result.effects.map((e) => (
            <li key={e}>{e}</li>
          ))}
        </ul>
      )}
      {result.warnings.length > 0 && (
        <div className="warnings">
          <p>Warnings in the file:</p>
          <ul>
            {result.warnings.map((w) => (
              <li key={`${w.line}:${w.rule}:${w.message}`}>
                {w.line !== null && `Line ${w.line}: `}
                {w.message} <span className="muted">({w.rule})</span>
              </li>
            ))}
          </ul>
        </div>
      )}
      <button type="button" onClick={onDismiss}>
        Dismiss
      </button>
    </div>
  )
}

/** An Edit (or Rename, Remove, …) button, off while the note is frozen or another piece is being edited. */
export function EditButton({ target, label, id, children }: { target: EditTarget; label: string; id?: string; children: ReactNode }) {
  const editing = useEditing()
  if (!editing) {
    return null
  }

  const busy = editing.target !== null
  return (
    <button
      type="button"
      className="edit-button"
      id={id ?? editButtonId(target)}
      aria-label={label}
      disabled={editing.frozen !== null || busy}
      aria-describedby={editing.frozen !== null ? 'frozen-reason' : undefined}
      title={editing.frozen ?? (busy ? 'Save or cancel the open edit first.' : undefined)}
      onClick={() => editing.open(target)}
    >
      {children}
    </button>
  )
}

/** Says why a frozen note's Edit controls are off. */
export function FrozenNotice() {
  const editing = useEditing()
  return editing?.frozen ? (
    <p className="frozen-notice" id="frozen-reason">
      Editing is off for this note: {editing.frozen}
    </p>
  ) : null
}

/** The piece of the source a text editor edits, and how to send it back. */
interface Piece {
  hash: string
  text: string
  /** The section's index and name, for a section. */
  index?: number
  name?: string
}

type TextTarget = Extract<EditTarget, { kind: 'lead' | 'section' | 'file' }>

/** The piece `target` names in `source`, or null if it isn't there (any more). */
function pieceOf(source: EntitySource, target: TextTarget | { kind: 'rename' | 'remove'; line: number; name: string }, opened?: Piece | null): Piece | null {
  switch (target.kind) {
    case 'lead':
      return { hash: source.hash, text: source.lead.text }
    case 'file':
      return { hash: source.hash, text: source.text }
    default: {
      // In the file as it was opened, by its line; in a newer file, by the index it had, if that still has its name.
      const exact = opened
        ? source.sections.find((s) => s.index === opened.index && s.name === target.name)
        : source.sections.find((s) => s.line === target.line && s.name === target.name)
      const named = source.sections.filter((s) => s.name === target.name)
      const section = exact ?? (named.length === 1 ? named[0] : undefined)
      return section ? { hash: source.hash, text: section.body, index: section.index, name: section.name } : null
    }
  }
}

/** Fetches the note's source when an editor opens, and gives the piece it edits, kept as it was when it opened. */
function useOpenedPiece(target: TextTarget | { kind: 'rename' | 'remove'; line: number; name: string }) {
  const editing = useEditing()!
  const source = useQuery({ ...sourceQuery(editing.universe, editing.entityKey), refetchOnMount: 'always' })
  const [piece, setPiece] = useState<Piece | null>(null)
  const [missing, setMissing] = useState(false)
  // Only the file as fetched for this editor, never an answer cached from an earlier one.
  const fresh = source.isFetchedAfterMount && !source.isFetching
  useEffect(() => {
    if (fresh && source.data && piece === null && !missing) {
      const found = pieceOf(source.data, target)
      if (found) {
        setPiece(found)
      } else {
        setMissing(true)
      }
    }
  }, [fresh, source.data, piece, missing, target])
  return { source, piece, setPiece, missing }
}

/** Invalidates every cached answer after a save: names, links, lists, searches, and history can all follow from one edit. */
export function useAfterSave() {
  const client = useQueryClient()
  return useCallback(() => client.invalidateQueries(), [client])
}

/**
 * Edits one piece of the note in place: the lead, a section's text, or the
 * whole file. Save (or Ctrl/⌘+Enter) sends it with the hash it was based on;
 * Cancel (or Escape) leaves it as it was.
 */
export function PieceEditor({ target, what, file }: { target: TextTarget; what: string; file: string }) {
  const editing = useEditing()!
  const afterSave = useAfterSave()
  const { source, piece, setPiece, missing } = useOpenedPiece(target)
  const [text, setText] = useState<string | null>(null)
  const [preview, setPreview] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<EditError | Error | null>(null)
  const [conflict, setConflict] = useState<EntitySource | null>(null)
  const [copied, setCopied] = useState<{ text: string; note: string } | null>(null)
  const labelId = useId()

  useEffect(() => {
    if (piece && text === null) {
      setText(piece.text)
    }
  }, [piece, text])

  const { setDirty } = editing
  useEffect(() => {
    setDirty(piece !== null && text !== null && text !== piece.text)
  }, [piece, text, setDirty])

  const save = async (base: Piece | null = piece) => {
    if (!base || text === null || saving) {
      return
    }

    if (base === piece && text === piece.text) {
      // Nothing to save: no request, and the file is left as it is.
      editing.close()
      return
    }

    setSaving(true)
    setError(null)
    try {
      const path: EditPath = target.kind === 'section' ? 'section' : target.kind
      const body: Record<string, unknown> = { entity: editing.entityKey, universe: editing.universe, expectedHash: base.hash, text }
      if (target.kind === 'section') {
        body.index = base.index
        body.name = base.name
      }

      const result = await postEdit(path, body)
      await afterSave()
      editing.close({ what, result })
    } catch (e) {
      if (e instanceof EditError && e.code === 'stale_file' && e.current) {
        setConflict(e.current)
      } else {
        setError(e as Error)
      }
    } finally {
      setSaving(false)
    }
  }

  const cancel = editing.cancel
  const dirty = text !== piece?.text

  if (source.isError) {
    return <ErrorState error={source.error} />
  }

  if (missing) {
    return (
      <div className="editor-panel" role="alert">
        <p>This part of the note isn't in the file now; it may have been changed elsewhere. Reload the page to see it as it is.</p>
        <button type="button" onClick={cancel}>
          Close
        </button>
      </div>
    )
  }

  if (!piece || text === null) {
    return <Loading what="the note's source" />
  }

  if (conflict) {
    const theirs = pieceOf(conflict, target, piece)
    return (
      <Conflict
        what={what}
        mine={text}
        theirs={theirs?.text ?? null}
        saving={saving}
        onKeepMine={
          theirs
            ? () => {
                setPiece(theirs)
                setConflict(null)
                void save(theirs)
              }
            : undefined
        }
        onTakeTheirs={() => {
          void afterSave()
          editing.close()
        }}
        onCopyMine={async () => {
          let note = 'Your version is copied. The editor now holds the file as it is; paste your text where you want it.'
          try {
            await navigator.clipboard.writeText(text)
          } catch {
            note = "Your version couldn't be copied. It's shown below the editor; copy it from there."
          }

          setCopied({ text, note })
          setConflict(null)
          if (theirs) {
            setPiece(theirs)
            setText(theirs.text)
          }
        }}
      />
    )
  }

  return (
    <div className="editor-panel">
      <div className="editor-toolbar">
        <span id={labelId} className="editor-label">
          Editing {what}
        </span>
        <button type="button" aria-pressed={preview} onClick={() => setPreview(!preview)}>
          Preview
        </button>
      </div>
      {piece && target.kind === 'section' && <SectionCommentNote target={target} source={source.data} />}
      <div hidden={preview}>
        <MarkdownEditor
          value={text}
          onChange={setText}
          onSave={() => void save()}
          onCancel={cancel}
          label={`Markdown of ${what}`}
          universe={editing.universe}
          autoFocus
          minLines={target.kind === 'file' ? 20 : 4}
        />
      </div>
      {preview && (
        <div className="editor-preview" aria-label={`Preview of ${what}`}>
          {target.kind === 'file' ? <pre className="file-preview">{text}</pre> : <Markdown text={text} links={[]} universe={editing.universe} file={file} />}
          <p className="muted">Links show as written in the preview; they're resolved once the note is saved.</p>
        </div>
      )}
      {error && <SaveError error={error} />}
      <DiscardPrompt />
      <p className="editor-actions">
        <button type="button" className="primary" onClick={() => void save()} disabled={saving || !dirty}>
          {saving ? 'Saving…' : 'Save'}
        </button>
        <button type="button" onClick={cancel} disabled={saving}>
          Cancel
        </button>
        <span className="muted">Ctrl+Enter (⌘+Enter) saves, Escape cancels.</span>
      </p>
      {copied !== null && <CopiedText text={copied.text} note={copied.note} />}
    </div>
  )
}

/** The writer's version, after "Copy mine and reload", to paste back in or copy by hand. */
function CopiedText({ text, note }: { text: string; note: string }) {
  return (
    <div className="copied" role="status">
      <p>{note}</p>
      <label>
        Your version
        <textarea readOnly value={text} rows={Math.min(12, text.split('\n').length + 1)} />
      </label>
    </div>
  )
}

/** A section's comment (`<!-- wolfden: ... -->`), which the section editor keeps aside: Edit file changes it. */
function SectionCommentNote({ target, source }: { target: { line: number; name: string }; source: EntitySource | undefined }) {
  const section = source?.sections.find((s) => s.line === target.line && s.name === target.name)
  return section?.comment ? (
    <p className="section-comment">
      Kept as it is: <code>{section.comment}</code> <span className="muted">(change it with Edit file)</span>
    </p>
  ) : null
}

export function SaveError({ error }: { error: Error }) {
  const code = error instanceof EditError ? error.code : null
  return (
    <div className="error" role="alert">
      <p>
        {code === 'invalid_edit' ? "It wasn't saved, because the note would have errors: " : code === 'frozen' ? "It can't be saved: " : "It wasn't saved: "}
        {error.message}
      </p>
      {code === 'bad_argument' && error.message.includes('heading') && (
        <p className="muted">To add a section, use Add section; to change the note's outline, use Edit file.</p>
      )}
    </div>
  )
}

/**
 * The writer's version and the file's current one, when the file changed
 * since the editor opened (by an agent, or another editor), with the lines
 * that differ marked, and the three ways forward.
 */
export function Conflict({
  what,
  mine,
  theirs,
  saving,
  onKeepMine,
  onTakeTheirs,
  onCopyMine,
}: {
  what: string
  mine: string
  theirs: string | null
  saving: boolean
  onKeepMine: (() => void) | undefined
  onTakeTheirs: () => void
  /** Copy mine and reload, where the editor can open again on the file as it is now. */
  onCopyMine?: () => void
}) {
  const heading = useRef<HTMLHeadingElement>(null)
  useEffect(() => heading.current?.focus(), [])
  const [mineMarks, theirMarks] = useMemo(() => differingLines(mine, theirs ?? ''), [mine, theirs])
  return (
    <div className="editor-panel conflict" role="region" aria-labelledby="conflict-heading">
      <h3 id="conflict-heading" ref={heading} tabIndex={-1}>
        The note changed while you were editing {what}
      </h3>
      <p>Something else saved the file since you opened the editor, perhaps an AI agent or another editor. Nothing of yours was saved yet. Choose what to keep.</p>
      <div className="conflict-versions">
        <Version title="Your version" text={mine} marks={mineMarks} />
        {theirs === null ? (
          <div className="version">
            <h4>The file now</h4>
            <p className="muted">This part isn't in the file now.</p>
          </div>
        ) : (
          <Version title="The file now" text={theirs} marks={theirMarks} />
        )}
      </div>
      <p className="editor-actions">
        <button type="button" className="primary" onClick={onKeepMine} disabled={!onKeepMine || saving}>
          Keep mine
        </button>
        <button type="button" onClick={onTakeTheirs} disabled={saving}>
          Take theirs
        </button>
        {onCopyMine && (
          <button type="button" onClick={onCopyMine} disabled={saving}>
            Copy mine and reload
          </button>
        )}
      </p>
      <p className="muted">
        Keep mine saves your version over the new file. Take theirs drops your changes.
        {onCopyMine && ' Copy mine and reload copies your version, then opens the file as it is now.'}
      </p>
    </div>
  )
}

function Version({ title, text, marks }: { title: string; text: string; marks: boolean[] }) {
  const lines = text.split('\n')
  return (
    <div className="version">
      <h4>{title}</h4>
      <pre>
        {lines.map((line, i) => (
          <span key={i} className={marks[i] ? 'differs' : undefined}>
            {marks[i] && <span className="visually-hidden">Differs: </span>}
            {line}
            {'\n'}
          </span>
        ))}
      </pre>
    </div>
  )
}

/** For each line of `a` and of `b`, true if it isn't in their longest common run of lines. */
export function differingLines(a: string, b: string): [boolean[], boolean[]] {
  const x = a.split('\n')
  const y = b.split('\n')
  const table = Array.from({ length: x.length + 1 }, () => new Array<number>(y.length + 1).fill(0))
  for (let i = x.length - 1; i >= 0; i--) {
    for (let j = y.length - 1; j >= 0; j--) {
      table[i][j] = x[i] === y[j] ? table[i + 1][j + 1] + 1 : Math.max(table[i + 1][j], table[i][j + 1])
    }
  }

  const inX = new Array<boolean>(x.length).fill(true)
  const inY = new Array<boolean>(y.length).fill(true)
  let i = 0
  let j = 0
  while (i < x.length && j < y.length) {
    if (x[i] === y[j]) {
      inX[i++] = false
      inY[j++] = false
    } else if (table[i + 1][j] >= table[i][j + 1]) {
      i++
    } else {
      j++
    }
  }

  return [inX, inY]
}

/** Renames a section in place. Links to it by heading follow, den-wide. */
export function RenameSection({ target }: { target: { kind: 'rename'; line: number; name: string } }) {
  const editing = useEditing()!
  const afterSave = useAfterSave()
  const { piece, setPiece, missing } = useOpenedPiece(target)
  const [name, setName] = useState(target.name)
  const [error, setError] = useState<Error | null>(null)
  const [saving, setSaving] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  const { setDirty } = editing
  useEffect(() => setDirty(name.trim() !== target.name), [name, target.name, setDirty])
  useEffect(() => input.current?.focus(), [])

  const submit = async () => {
    if (!piece || saving) {
      return
    }

    setSaving(true)
    setError(null)
    try {
      const result = await postEdit('section/rename', {
        entity: editing.entityKey, universe: editing.universe, expectedHash: piece.hash, index: piece.index, name: piece.name, newName: name,
      })
      await afterSave()
      editing.close({ what: `the section “${target.name}”`, result })
    } catch (e) {
      setError(staleAgain(e, target, piece, setPiece))
    } finally {
      setSaving(false)
    }
  }

  return (
    <form
      className="inline-form"
      onSubmit={(e) => {
        e.preventDefault()
        void submit()
      }}
      onKeyDown={(e) => {
        if (e.key === 'Escape') {
          e.preventDefault()
          editing.cancel()
        }
      }}
    >
      <label>
        New name for the section “{target.name}”
        <input ref={input} value={name} onChange={(e) => setName(e.target.value)} required />
      </label>
      <p className="muted">Links to this section by its heading are changed to the new name, in every note.</p>
      {missing && <p role="alert">This section isn't in the file now. Reload the page to see it as it is.</p>}
      {error && <SaveError error={error} />}
      <DiscardPrompt />
      <p className="editor-actions">
        <button type="submit" className="primary" disabled={!piece || saving}>
          {saving ? 'Renaming…' : 'Rename'}
        </button>
        <button type="button" onClick={editing.cancel} disabled={saving}>
          Cancel
        </button>
      </p>
    </form>
  )
}

/**
 * After a stale rename or removal, takes the section from the file as it is
 * now, so trying again sends the new hash, and says the note changed.
 */
function staleAgain(
  e: unknown,
  target: { kind: 'rename' | 'remove'; line: number; name: string },
  opened: Piece | null,
  setPiece: (p: Piece | null) => void,
): Error {
  if (e instanceof EditError && e.code === 'stale_file') {
    const now = e.current ? pieceOf(e.current, target, opened) : null
    if (now) {
      setPiece(now)
      return new Error('The note changed meanwhile, perhaps edited by an agent or in another editor. Check it, then try again.')
    }

    return new Error("The note changed meanwhile, and this section isn't in it now. Reload the page to see it as it is.")
  }

  return e as Error
}

/** Asks, in the page, before removing a section with its subsections. */
export function RemoveSection({ target, subsections }: { target: { kind: 'remove'; line: number; name: string }; subsections: number }) {
  const editing = useEditing()!
  const afterSave = useAfterSave()
  const { piece, setPiece, missing } = useOpenedPiece(target)
  const [error, setError] = useState<Error | null>(null)
  const [saving, setSaving] = useState(false)
  const keep = useRef<HTMLButtonElement>(null)
  useEffect(() => keep.current?.focus(), [])

  const remove = async () => {
    if (!piece || saving) {
      return
    }

    setSaving(true)
    setError(null)
    try {
      const result = await postEdit('section/remove', {
        entity: editing.entityKey, universe: editing.universe, expectedHash: piece.hash, index: piece.index, name: piece.name,
      })
      await afterSave()
      editing.close({ what: `the note (removed “${target.name}”)`, result })
    } catch (e) {
      setError(staleAgain(e, target, piece, setPiece))
    } finally {
      setSaving(false)
    }
  }

  return (
    <div
      className="inline-form confirm"
      role="group"
      aria-labelledby={`remove-${target.line}`}
      onKeyDown={(e) => {
        if (e.key === 'Escape') {
          e.preventDefault()
          editing.close()
        }
      }}
    >
      <p id={`remove-${target.line}`}>
        Remove the section “{target.name}”{subsections > 0 ? ` and its ${subsections} subsection${subsections === 1 ? '' : 's'}` : ''}? Its text goes
        from the file. Links to it by heading aren't changed.
      </p>
      {missing && <p role="alert">This section isn't in the file now. Reload the page to see it as it is.</p>}
      {error && <SaveError error={error} />}
      <p className="editor-actions">
        <button type="button" className="danger" onClick={() => void remove()} disabled={!piece || saving}>
          {saving ? 'Removing…' : 'Remove section'}
        </button>
        <button type="button" ref={keep} onClick={() => editing.close()} disabled={saving}>
          Keep it
        </button>
      </p>
    </div>
  )
}

/** Adds a section, at the end or before another, with its text. */
export function AddSection({ file }: { file: string }) {
  const editing = useEditing()!
  const afterSave = useAfterSave()
  const source = useQuery({ ...sourceQuery(editing.universe, editing.entityKey), refetchOnMount: 'always' })
  const fresh = source.isFetchedAfterMount && !source.isFetching
  const [base, setBase] = useState<EntitySource | null>(null)
  const [name, setName] = useState('')
  const [before, setBefore] = useState('')
  const [text, setText] = useState('')
  const [preview, setPreview] = useState(false)
  const [error, setError] = useState<Error | null>(null)
  const [saving, setSaving] = useState(false)
  const nameInput = useRef<HTMLInputElement>(null)
  const listId = useId()
  const { setDirty } = editing

  useEffect(() => {
    if (fresh && source.data && base === null) {
      setBase(source.data)
    }
  }, [fresh, source.data, base])
  useEffect(() => setDirty(name.trim() !== '' || text.trim() !== ''), [name, text, setDirty])
  useEffect(() => nameInput.current?.focus(), [base])

  const save = async () => {
    if (!base || saving) {
      return
    }

    setSaving(true)
    setError(null)
    try {
      const place = before === '' ? undefined : base.sections.find((s) => String(s.index) === before)
      const result = await postEdit('section/add', {
        entity: editing.entityKey, universe: editing.universe, expectedHash: base.hash, name, text,
        ...(place ? { beforeIndex: place.index, beforeName: place.name } : {}),
      })
      await afterSave()
      editing.close({ what: `the new section “${name.trim()}”`, result })
    } catch (e) {
      if (e instanceof EditError && e.code === 'stale_file' && e.current) {
        // A new section can go into the new file as well: keep the form, on the new hash.
        const placeName = base.sections.find((s) => String(s.index) === before)?.name
        const again = placeName === undefined ? undefined : e.current.sections.find((s) => s.name === placeName)
        setBase(e.current)
        setBefore(again ? String(again.index) : '')
        setError(new Error('The note changed meanwhile, perhaps edited by an agent or in another editor. Check where the section goes, then save again.'))
      } else {
        setError(e as Error)
      }
    } finally {
      setSaving(false)
    }
  }

  if (source.isError) {
    return <ErrorState error={source.error} />
  }

  if (!base) {
    return <Loading what="the note's source" />
  }

  const missingStandard = base.standardSections.filter((s) => !base.sections.some((x) => x.name === s))
  return (
    <form
      className="editor-panel inline-form"
      aria-label="Add a section"
      onSubmit={(e) => {
        e.preventDefault()
        void save()
      }}
      onKeyDown={(e) => {
        if (e.key === 'Escape' && !e.defaultPrevented) {
          e.preventDefault()
          editing.cancel()
        }
      }}
    >
      <label>
        Section name
        <input ref={nameInput} value={name} onChange={(e) => setName(e.target.value)} list={listId} required />
      </label>
      <datalist id={listId}>
        {missingStandard.map((s) => (
          <option key={s} value={s} />
        ))}
      </datalist>
      <label>
        Where
        <select value={before} onChange={(e) => setBefore(e.target.value)}>
          <option value="">At the end</option>
          {base.sections.map((s) => (
            <option key={s.index} value={String(s.index)}>
              Before “{s.name}”
            </option>
          ))}
        </select>
      </label>
      <div className="editor-toolbar">
        <span className="editor-label">Its text</span>
        <button type="button" aria-pressed={preview} onClick={() => setPreview(!preview)}>
          Preview
        </button>
      </div>
      <div hidden={preview}>
        <MarkdownEditor
          value={text}
          onChange={setText}
          onSave={() => void save()}
          onCancel={editing.cancel}
          label="Markdown of the new section"
          universe={editing.universe}
        />
      </div>
      {preview && (
        <div className="editor-preview" aria-label="Preview of the new section">
          <Markdown text={text} links={[]} universe={editing.universe} file={file} />
        </div>
      )}
      {error && <SaveError error={error} />}
      <DiscardPrompt />
      <p className="editor-actions">
        <button type="submit" className="primary" disabled={saving}>
          {saving ? 'Adding…' : 'Add section'}
        </button>
        <button type="button" onClick={editing.cancel} disabled={saving}>
          Cancel
        </button>
      </p>
    </form>
  )
}
