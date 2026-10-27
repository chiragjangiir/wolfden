import { useEffect, useId, useRef, useState, type FormEvent, type KeyboardEvent as ReactKeyboardEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { count } from '../api'
import {
  CHECKPOINT_STATUS_KEY,
  changeWord,
  checkpointStatusQuery,
  groupChanges,
  makeCheckpoint,
  type CheckpointMade,
  type CheckpointStatus,
} from '../checkpointApi'
import '../checkpoint.css'

/**
 * The quiet top-bar note of what's changed since the last checkpoint (#30):
 * "3 notes changed since the last checkpoint · Checkpoint…". It's hidden
 * when nothing has changed, and opens a dialog that lists the changes and
 * asks the writer to name the checkpoint. Without history, it says history
 * is off, and the dialog says how to turn it on. Saving never makes a
 * checkpoint (#28); only the writer does, here, or an agent, or
 * `auto_checkpoint`.
 */
export function CheckpointIndicator() {
  const status = useQuery(checkpointStatusQuery)
  const [open, setOpen] = useState(false)
  const triggerRef = useRef<HTMLButtonElement>(null)

  const close = () => {
    setOpen(false)
    // Back to the indicator, if it's still there (after a checkpoint it hides).
    requestAnimationFrame(() => triggerRef.current?.focus())
  }

  const data = status.data
  const visible = data !== undefined && (!data.available || data.count > 0)

  // The status couldn't be read (git failed, or the server's gone): say so, quietly,
  // rather than hide the indicator as if nothing had changed.
  if (status.isError && data === undefined) {
    return (
      <>
        <button type="button" ref={triggerRef} className="checkpoint-indicator" aria-haspopup="dialog" onClick={() => setOpen(true)}>
          History unavailable
        </button>
        {open && <UnavailableDialog message={status.error.message} onClose={close} />}
      </>
    )
  }

  return (
    <>
      {visible && (
        <button
          type="button"
          ref={triggerRef}
          className="checkpoint-indicator"
          aria-haspopup="dialog"
          onClick={() => setOpen(true)}
        >
          {data.available ? (
            <>
              {count(data.count, 'note', 'notes')} {data.latest ? 'changed since the last checkpoint' : 'not checkpointed yet'}
              {' · '}
              <span className="checkpoint-action">Checkpoint…</span>
            </>
          ) : (
            <>
              No history ·{' '}
              <span className="checkpoint-action">Turn on…</span>
            </>
          )}
        </button>
      )}
      {open && data !== undefined && <CheckpointDialog status={data} onClose={close} />}
    </>
  )
}

/** Keeps Tab and Shift+Tab inside a dialog; with nothing to focus, on the dialog itself. */
function trapTab(e: ReactKeyboardEvent<HTMLDivElement>, dialog: HTMLDivElement | null) {
  const focusable = [...(dialog?.querySelectorAll<HTMLElement>('button, textarea, a[href], input') ?? [])].filter((el) => !el.hasAttribute('disabled'))
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

/** What the indicator opens when the status couldn't be read. */
function UnavailableDialog({ message, onClose }: { message: string; onClose: () => void }) {
  const titleId = useId()
  const dialogRef = useRef<HTMLDivElement>(null)
  useEffect(() => dialogRef.current?.querySelector<HTMLElement>('button')?.focus(), [])
  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div
        className="dialog checkpoint-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        ref={dialogRef}
        tabIndex={-1}
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            e.stopPropagation()
            onClose()
          } else if (e.key === 'Tab') {
            trapTab(e, dialogRef.current)
          }
        }}
      >
        <h2 id={titleId}>History unavailable</h2>
        <p>Wolfden couldn't tell what's changed since the last checkpoint: {message}</p>
        <p className="muted">Your edits are still saved in the files. The page asks again in a minute.</p>
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Close
          </button>
        </div>
      </div>
    </div>
  )
}

/** The checkpoint dialog: what's changed, grouped for review, and the writer's name for it. */
export function CheckpointDialog({ status, onClose }: { status: CheckpointStatus; onClose: () => void }) {
  const titleId = useId()
  const descriptionId = useId()
  const messageId = useId()
  const errorId = useId()
  const dialogRef = useRef<HTMLDivElement>(null)
  const messageRef = useRef<HTMLTextAreaElement>(null)
  const doneRef = useRef<HTMLButtonElement>(null)
  const [message, setMessage] = useState('')
  const [missing, setMissing] = useState(false)
  // The changes as they were when the dialog opened, so a refresh while it's open doesn't reshuffle them.
  const [shown] = useState(status)
  const client = useQueryClient()

  const checkpoint = useMutation({
    mutationFn: makeCheckpoint,
    onSuccess: async () => {
      await Promise.all([
        client.invalidateQueries({ queryKey: CHECKPOINT_STATUS_KEY }),
        client.invalidateQueries({ queryKey: ['history'] }),
        client.invalidateQueries({ queryKey: ['history-diff'] }),
      ])
    },
  })

  useEffect(() => {
    ;(messageRef.current ?? dialogRef.current?.querySelector<HTMLElement>('button'))?.focus()
  }, [])

  useEffect(() => {
    if (checkpoint.isSuccess) {
      doneRef.current?.focus()
    }
  }, [checkpoint.isSuccess])

  const busy = checkpoint.isPending
  const [confirming, setConfirming] = useState(false)

  // While the request runs, the box and buttons are disabled, so focus stays on the
  // dialog itself rather than falling to the page behind it; after an error, it goes
  // back to the box, to try again.
  useEffect(() => {
    if (busy) {
      dialogRef.current?.focus()
    }
  }, [busy])

  useEffect(() => {
    if (checkpoint.isError) {
      messageRef.current?.focus()
    }
  }, [checkpoint.isError])

  /** A click outside, or Escape: closes, but asks first if a name has been typed, as the editor does. */
  const dismiss = () => {
    if (busy) {
      return
    }

    if (message.trim() !== '' && !checkpoint.isSuccess) {
      setConfirming(true)
      return
    }

    onClose()
  }

  const onKeyDown = (e: ReactKeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'Escape') {
      e.stopPropagation()
      if (confirming) {
        setConfirming(false)
        messageRef.current?.focus()
      } else {
        dismiss()
      }

      return
    }

    if (e.key === 'Tab') {
      trapTab(e, dialogRef.current)
    }
  }

  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (message.trim() === '') {
      setMissing(true)
      messageRef.current?.focus()
      return
    }

    setMissing(false)
    checkpoint.mutate(message.trim())
  }

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && dismiss()}>
      <div
        className="dialog checkpoint-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={descriptionId}
        ref={dialogRef}
        tabIndex={-1}
        aria-busy={busy}
        onKeyDown={onKeyDown}
      >
        {!shown.available ? (
          <>
            <h2 id={titleId}>History is off</h2>
            <div id={descriptionId}>
              <p>{shown.reason}</p>
              {shown.enable && <p>{shown.enable}</p>}
              <p className="muted">
                A checkpoint saves a version of the den's notes that you can look back at or compare with later. Without history, your edits
                are still saved in the files.
              </p>
            </div>
            <div className="dialog-actions">
              <button type="button" onClick={onClose}>
                Close
              </button>
            </div>
          </>
        ) : checkpoint.isSuccess ? (
          <>
            <h2 id={titleId}>{checkpoint.data.recorded ? 'Checkpoint made' : 'Nothing to checkpoint'}</h2>
            <div id={descriptionId} role="status">
              <Outcome made={checkpoint.data} />
            </div>
            <div className="dialog-actions">
              <button type="button" ref={doneRef} onClick={onClose}>
                Close
              </button>
            </div>
          </>
        ) : (
          <form onSubmit={submit} noValidate>
            <h2 id={titleId}>Make a checkpoint</h2>
            <p id={descriptionId} className="muted">
              A checkpoint saves this version of the den's notes in its history, so you can look back at it or compare with it later. Name it
              for what you did, such as a notes pass after a chapter or a planning session.
            </p>
            <Changes status={shown} />
            {shown.inProgress && (
              <p className="banner checkpoint-warning">
                Git is in the middle of {shown.inProgress}. A checkpoint now becomes part of it, so you may want to finish that in git
                first.
              </p>
            )}
            <label htmlFor={messageId} className="checkpoint-label">
              Name this checkpoint
            </label>
            <textarea
              id={messageId}
              ref={messageRef}
              rows={2}
              required
              aria-required="true"
              aria-invalid={missing}
              aria-describedby={missing ? errorId : undefined}
              placeholder="For example: Notes pass after drafting ch 12"
              value={message}
              disabled={busy}
              onChange={(e) => {
                setMessage(e.target.value)
                if (e.target.value.trim() !== '') {
                  setMissing(false)
                }
              }}
            />
            {missing && (
              <p id={errorId} className="field-error" role="alert">
                Give the checkpoint a name, so you can find it in the history later.
              </p>
            )}
            {checkpoint.isError && (
              <div className="error" role="alert">
                <p>{checkpoint.error.message}</p>
              </div>
            )}
            <AutoNote status={shown} />
            {confirming ? (
              <div className="banner leave-prompt" role="alertdialog" aria-labelledby={`${titleId}-discard`}>
                <p id={`${titleId}-discard`}>Close without making the checkpoint? The name you typed isn't kept.</p>
                <p className="banner-actions">
                  <button
                    type="button"
                    autoFocus
                    onClick={() => {
                      setConfirming(false)
                      messageRef.current?.focus()
                    }}
                  >
                    Keep it open
                  </button>
                  <button type="button" onClick={onClose}>
                    Close without a checkpoint
                  </button>
                </p>
              </div>
            ) : (
              <div className="dialog-actions">
                <button type="submit" className="primary" disabled={busy}>
                  {busy ? 'Making the checkpoint…' : 'Checkpoint'}
                </button>
                <button type="button" onClick={onClose} disabled={busy}>
                  Cancel
                </button>
              </div>
            )}
          </form>
        )}
      </div>
    </div>
  )
}

/** The changes a checkpoint would record, grouped by universe and book. */
function Changes({ status }: { status: CheckpointStatus }) {
  const groups = groupChanges(status.changes)
  const anySavedHere = status.changes.some((c) => c.savedHere)
  return (
    <section className="checkpoint-changes" aria-label="Changes since the last checkpoint">
      <p>
        {count(status.count, 'note', 'notes')} {status.latest ? 'changed since the last checkpoint' : 'not checkpointed yet'}:
      </p>
      {groups.map((group) => (
        <div key={group.key} className="checkpoint-group">
          <h3>{group.title}</h3>
          <ul>
            {group.changes.map(({ change, shown }) => (
              <li key={change.path}>
                <span className={`change-kind kind-${change.kind}`}>{changeWord(change.kind)}</span> <code>{shown}</code>
                {change.oldPath && <span className="muted"> (was {change.oldPath})</span>}
                {change.savedHere && <span className="badge saved-here">saved in Wolfden</span>}
              </li>
            ))}
          </ul>
        </div>
      ))}
      {status.otherUniverses > 0 && (
        <p className="muted">
          And {count(status.otherUniverses, 'file', 'files')} in other universes, which this server doesn't show because it's serving one
          universe. A checkpoint records the whole den, so they're included too.
        </p>
      )}
      {anySavedHere && (
        <p className="muted">
          Notes marked “saved in Wolfden” were saved on this page. The others changed another way: in an editor, by an AI assistant, or
          before Wolfden started.
        </p>
      )}
    </section>
  )
}

function AutoNote({ status }: { status: CheckpointStatus }) {
  const auto = status.autoCheckpoint
  if (auto.problem) {
    return <p className="muted">den.yaml: {auto.problem}</p>
  }

  if (auto.every && auto.runs) {
    return <p className="muted">Automatic checkpoints are on: Wolfden makes one after {auto.every} without changes.</p>
  }

  return null
}

function Outcome({ made }: { made: CheckpointMade }) {
  if (!made.recorded || !made.checkpoint) {
    return <p>Nothing had changed since the last checkpoint, so nothing was recorded.</p>
  }

  const firstLine = made.checkpoint.message.split('\n', 1)[0]
  return (
    <>
      <p>
        Recorded “{firstLine}”, with {count(made.checkpoint.files, 'note', 'notes')}, as checkpoint <code>{made.checkpoint.id.slice(0, 10)}</code>.
        Each note's History tab lists it.
      </p>
      {made.inProgress && <p className="banner checkpoint-warning">Git was in the middle of {made.inProgress}, so the checkpoint is part of it.</p>}
    </>
  )
}
