import { Fragment, memo, useMemo, type ReactNode } from 'react'
import { describeHeaders, pairChanges, parsePatch, type DiffLine, type Segment } from '../diff'
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

// Characters git keeps inside a line that would otherwise be invisible or
// break the line on screen, each shown as a small mark.
// (Spelled as code points: a line or paragraph separator written as itself
// would end a line of this source file in some tools.)
const MARKS: Record<string, { mark: string; name: string }> = {
  [String.fromCodePoint(0x0d)]: { mark: '␍', name: 'carriage return' },
  [String.fromCodePoint(0x85)]: { mark: 'NEL', name: 'next line (U+0085)' },
  [String.fromCodePoint(0x2028)]: { mark: 'LS', name: 'line separator (U+2028)' },
  [String.fromCodePoint(0x2029)]: { mark: 'PS', name: 'paragraph separator (U+2029)' },
}

const MARKED = new RegExp(`([${Object.keys(MARKS).join('')}])`)

/**
 * Text as it is, with each carriage return, NEL, line separator, and
 * paragraph separator shown as a visible mark rather than dropped or taken
 * as a line break. Always text: nothing in a note is ever rendered as HTML.
 */
export function VisibleText({ text }: { text: string }) {
  if (!MARKED.test(text)) {
    return <>{text}</>
  }

  return (
    <>
      {text.split(MARKED).map((piece, i) => {
        const special = i % 2 === 1 ? MARKS[piece] : undefined
        return special ? (
          <span key={i} className="diff-cr" title={special.name} aria-label={special.name}>
            {special.mark}
          </span>
        ) : (
          <Fragment key={i}>{piece}</Fragment>
        )
      })}
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
          <mark key={i} className="diff-word">
            <VisibleText text={s.text} />
          </mark>
        ) : (
          <VisibleText key={i} text={s.text} />
        ),
      )}
    </>
  )
}

/** The +/- column: a sign to see, and a word for a screen reader. */
function Mark({ kind }: { kind: DiffLine['kind'] }) {
  if (kind === 'added' || kind === 'removed') {
    return (
      <>
        <span aria-hidden="true">{kind === 'added' ? '+' : '-'}</span>
        <span className="visually-hidden">{kind === 'added' ? 'Added' : 'Removed'}</span>
      </>
    )
  }

  return null
}

/** The table rows of a parsed patch. */
function diffRows(patch: string): { rows: ReactNode[]; notes: string[] } {
  const parts = parsePatch(patch)
  const notes = describeHeaders(parts)
  const rows: ReactNode[] = []
  for (const [h, hunk] of parts.flatMap((p) => p.hunks).entries()) {
    const segments = pairChanges(hunk.lines)
    rows.push(
      <tr key={`h${h}`} className="diff-hunk">
        <td colSpan={4}>
          <VisibleText text={hunk.header} />
        </td>
      </tr>,
    )
    hunk.lines.forEach((line, n) => {
      if (line.kind === 'note') {
        rows.push(
          <tr key={`h${h}:${n}`} className="diff-eof">
            <td colSpan={3} />
            <td>
              <VisibleText text={line.text} />
            </td>
          </tr>,
        )
        return
      }

      rows.push(
        <tr key={`h${h}:${n}`} className={`diff-line diff-${line.kind}`}>
          <td className="diff-number">{line.oldNumber ?? ''}</td>
          <td className="diff-number">{line.newNumber ?? ''}</td>
          <td className="diff-mark">
            <Mark kind={line.kind} />
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

  return { rows, notes }
}

/**
 * One file's change: its heading, then its patch as a unified diff, with
 * line numbers. Parsed and built once per patch, and not rendered again
 * unless the file changes, since a long diff is costly to build.
 */
export const FileDiffView = memo(function FileDiffView({ file }: { file: FileDiff }) {
  const { rows, notes } = useMemo(() => diffRows(file.patch), [file.patch])

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
})
