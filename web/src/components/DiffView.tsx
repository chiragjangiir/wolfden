import { Fragment, useMemo, type ReactNode } from 'react'
import { pairChanges, parsePatch, type DiffLine, type Segment } from '../diff'
import type { FileDiff } from '../historyApi'

/** "Added", "Renamed from notes/mina.md", for a file's heading. */
export function changeWords(change: FileDiff['change']): string {
  switch (change.kind) {
    case 'added':
      return 'Added'
    case 'deleted':
      return 'Deleted'
    case 'renamed':
      return change.oldPath ? `Renamed from ${change.oldPath}` : 'Renamed'
    default:
      return 'Changed'
  }
}

// Header lines the file's heading already says, so they aren't repeated.
const SAID = /^(diff --git |index |--- |\+\+\+ |similarity index |dissimilarity index |rename from |rename to |new file mode |deleted file mode )/

/**
 * Text as it is, with each carriage return shown as a visible mark rather
 * than dropped, so a note saved with CRLF line endings shows them. Always
 * text: nothing in a note is ever rendered as HTML.
 */
export function VisibleText({ text }: { text: string }) {
  if (!text.includes('\r')) {
    return <>{text}</>
  }

  const pieces = text.split('\r')
  return (
    <>
      {pieces.map((piece, i) => (
        <Fragment key={i}>
          {piece}
          {i < pieces.length - 1 && (
            <span className="diff-cr" title="A carriage return (\r)" aria-label="carriage return">
              ␍
            </span>
          )}
        </Fragment>
      ))}
    </>
  )
}

function LineText({ line, segments }: { line: DiffLine; segments: Segment[] | undefined }) {
  if (!segments) {
    return <VisibleText text={line.text} />
  }

  return (
    <>
      {segments.map((s, i) =>
        s.changed ? (
          <span key={i} className="diff-word">
            <VisibleText text={s.text} />
          </span>
        ) : (
          <VisibleText key={i} text={s.text} />
        ),
      )}
    </>
  )
}

const MARK: Record<DiffLine['kind'], string> = { context: ' ', added: '+', removed: '-' }

/** One file's change: its heading, then its patch as a unified diff, with line numbers. */
export function FileDiffView({ file }: { file: FileDiff }) {
  const parts = useMemo(() => parsePatch(file.patch), [file.patch])
  const hunks = parts.flatMap((p) => p.hunks)
  const notes = parts.flatMap((p) => p.headers.filter((h) => !SAID.test(h)))
  const rows: ReactNode[] = []
  for (const [h, hunk] of hunks.entries()) {
    const segments = pairChanges(hunk.lines)
    rows.push(
      <tr key={`h${h}`} className="diff-hunk">
        <td colSpan={4}>
          <VisibleText text={hunk.header} />
        </td>
      </tr>,
    )
    hunk.lines.forEach((line, n) => {
      rows.push(
        <tr key={`h${h}:${n}`} className={`diff-line diff-${line.kind}`}>
          <td className="diff-number">{line.oldNumber ?? ''}</td>
          <td className="diff-number">{line.newNumber ?? ''}</td>
          <td className="diff-mark" aria-hidden="true">
            {MARK[line.kind]}
          </td>
          <td className="diff-text">
            <LineText line={line} segments={segments.get(line)} />
          </td>
        </tr>,
      )
      if (line.noNewline) {
        rows.push(
          <tr key={`h${h}:${n}:eof`} className="diff-eof">
            <td colSpan={3} />
            <td>No newline at end of file</td>
          </tr>,
        )
      }
    })
  }

  return (
    <section className="diff-file">
      <h3>
        <span className={`badge change-${file.change.kind}`}>{changeWords(file.change)}</span> <code>{file.change.path}</code>
      </h3>
      {notes.map((n, i) => (
        <p key={i} className="muted diff-note">
          <VisibleText text={n} />
        </p>
      ))}
      {rows.length > 0 ? (
        <table className="diff">
          <colgroup>
            <col className="diff-col-number" />
            <col className="diff-col-number" />
            <col className="diff-col-mark" />
            <col />
          </colgroup>
          <thead className="visually-hidden">
            <tr>
              <th>Old line</th>
              <th>New line</th>
              <th>Change</th>
              <th>Text</th>
            </tr>
          </thead>
          <tbody>{rows}</tbody>
        </table>
      ) : (
        notes.length === 0 && <p className="muted">No change to the text.</p>
      )}
    </section>
  )
}
