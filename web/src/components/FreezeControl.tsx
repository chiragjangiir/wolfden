import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { bookFreezeQuery, changeFreeze, FAILED_SNAPSHOTS, type BookFreeze, type FreezeResult } from '../freezeApi'

type Action = 'freeze' | 'finish' | 'unfreeze'

/**
 * Freeze or unfreeze a book, on its page (#31; design.md §5). Freezing marks
 * the book final, so its notes, chapters, and outline can't be edited; with
 * history, it also checkpoints the den and tags the snapshot. Each asks
 * first, then says what happened, including a step in the history that
 * failed. A final book whose freeze didn't finish (git refused the
 * checkpoint or the tag) offers to finish it.
 */
export function FreezeControl({ universe, book }: { universe: string; book: string }) {
  const status = useQuery(bookFreezeQuery(universe, book))
  const client = useQueryClient()
  const [confirming, setConfirming] = useState<Action | null>(null)
  const [outcome, setOutcome] = useState<FreezeResult | null>(null)
  const promptId = useId()
  const triggerRef = useRef<HTMLButtonElement>(null)
  const outcomeRef = useRef<HTMLDivElement>(null)

  const change = useMutation({
    mutationFn: ({ action, current }: { action: Action; current: BookFreeze }) =>
      changeFreeze(action === 'unfreeze' ? 'unfreeze' : 'freeze', { universe, book, expectedHash: current.hash }),
    onSuccess: async (result) => {
      setConfirming(null)
      setOutcome(result)
      // The book's status reaches every page in it: its notes' edit controls, the
      // chapters, the checkpoint indicator, and the history. Those refetch in the
      // background; only this control's own status is waited for, so it never
      // offers the old action on a stale hash.
      const own = bookFreezeQuery(universe, book).queryKey
      void client.invalidateQueries({ predicate: (q) => q.queryKey[0] !== own[0] })
      await client.invalidateQueries({ queryKey: own })
    },
    onError: async () => {
      // book.md may have changed: ask again, so a retry is based on the file as it is now.
      await client.invalidateQueries({ queryKey: bookFreezeQuery(universe, book).queryKey })
    },
  })

  useEffect(() => {
    if (outcome) {
      outcomeRef.current?.focus()
    }
  }, [outcome])

  if (status.isPending) {
    return null
  }

  if (status.isError) {
    // The rest of the book page still works; only the control is missing.
    return <p className="muted">Freezing isn't available: {status.error.message}</p>
  }

  const current = status.data
  const final = current.status === 'final'
  const busy = change.isPending

  const ask = (action: Action) => {
    setOutcome(null)
    change.reset()
    setConfirming(action)
  }

  const cancel = () => {
    setConfirming(null)
    change.reset()
    requestAnimationFrame(() => triggerRef.current?.focus())
  }

  return (
    <div className="freeze-control">
      {outcome && (
        <div
          className={FAILED_SNAPSHOTS.includes(outcome.snapshot) ? 'save-result freeze-warning' : 'save-result'}
          role="status"
          tabIndex={-1}
          ref={outcomeRef}
        >
          <p>{outcome.message}</p>
          {outcome.warnings.length > 0 && (
            <ul className="warnings">
              {outcome.warnings.map((w) => (
                <li key={`${w.rule}:${w.line ?? ''}:${w.message}`}>
                  {w.rule}: {w.message}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
      {confirming ? (
        <div
          className="inline-form confirm"
          role="group"
          aria-labelledby={promptId}
          onKeyDown={(e) => {
            if (e.key === 'Escape' && !busy) {
              e.preventDefault()
              cancel()
            }
          }}
        >
          <p id={promptId}>{prompt(confirming, current)}</p>
          {change.isError && (
            <p className="error" role="alert">
              {change.error.message}
            </p>
          )}
          <p className="editor-actions">
            <button type="button" className="primary" disabled={busy} onClick={() => change.mutate({ action: confirming, current })}>
              {busy ? BUSY[confirming] : LABEL[confirming]}
            </button>
            <button type="button" autoFocus onClick={cancel} disabled={busy}>
              Cancel
            </button>
          </p>
        </div>
      ) : (
        <p className="editor-actions">
          {final && current.unfinished && (
            <button type="button" className="primary" onClick={() => ask('finish')}>
              Finish freezing…
            </button>
          )}
          <button type="button" ref={triggerRef} onClick={() => ask(final ? 'unfreeze' : 'freeze')}>
            {final ? 'Unfreeze…' : 'Freeze…'}
          </button>
          <span className="muted">
            {final
              ? current.unfinished
                ? 'This book is final, but its snapshot isn’t finished.'
                : 'This book is final (frozen): its notes can’t be edited.'
              : 'Freeze the book when it’s finished.'}
          </span>
        </p>
      )}
    </div>
  )
}

const LABEL: Record<Action, string> = { freeze: 'Freeze book', finish: 'Finish freezing', unfreeze: 'Unfreeze book' }
const BUSY: Record<Action, string> = { freeze: 'Freezing…', finish: 'Freezing…', unfreeze: 'Unfreezing…' }

/** The other changed files the checkpoint takes in, counted (never named), as the checkpoint dialog counts other universes' files. */
function others(current: BookFreeze): string {
  if (!current.history || current.otherChanges === 0) {
    return ''
  }

  return current.otherChanges === 1
    ? ' The checkpoint also records 1 other changed file outside this book.'
    : ` The checkpoint also records ${current.otherChanges} other changed files outside this book.`
}

/** What the confirmation says the action will do. */
function prompt(action: Action, current: BookFreeze): ReactNode {
  switch (action) {
    case 'unfreeze':
      return (
        <>
          Unfreeze {current.name}? Its notes, chapters, and outline can be edited again. book.md will say <code>status: draft</code>
          {current.history ? ', and Wolfden makes a checkpoint, so the history shows when the book was reopened.' : '.'} Its freeze tags stay
          where they are.
          {others(current)}
        </>
      )
    case 'finish':
      return (
        <>
          Finish freezing {current.name}? It's already final, but its snapshot isn't finished: Wolfden makes the checkpoint and adds the tag that
          are missing.
          {others(current)}
        </>
      )
    default:
      return (
        <>
          Freeze {current.name}? book.md will say <code>status: final</code>, and its notes, chapters, and outline can't be edited until you
          unfreeze it.{' '}
          {current.history
            ? `Wolfden then makes a checkpoint of the whole den${current.nextTag ? ` and tags it ${current.nextTag}` : ''}, a snapshot to come back to.`
            : 'This den has no history, so no snapshot is taken.'}
          {others(current)}
        </>
      )
  }
}
