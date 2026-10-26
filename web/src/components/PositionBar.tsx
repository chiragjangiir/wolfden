import { useQuery } from '@tanstack/react-query'
import { positionsQuery, type PositionView } from '../api'
import { usePosition } from '../hooks'
import { chapterWords, ordinalOf, positionLabel } from '../story'

/**
 * The story-position bar across the top of every page (#26): a slider along
 * every position, book and chapter menus to jump, and a way back to
 * everything. The position is the URL's `?at=`; without it the pages show
 * everything, the end of the story. The slider's last step is "Everything",
 * so the arrow keys step chapter by chapter up to the end, and Home and End
 * jump to the first chapter and to everything.
 */
export function PositionBar({ universe }: { universe: string | undefined }) {
  if (!universe) {
    return (
      <div className="position-bar" role="group" aria-label="Story position">
        <span className="muted">Choose a universe to pick a point in the story.</span>
      </div>
    )
  }

  return <UniversePositionBar universe={universe} />
}

function UniversePositionBar({ universe }: { universe: string }) {
  const { at, setAt } = usePosition()
  const positions = useQuery(positionsQuery(universe))

  if (positions.isPending) {
    return (
      <div className="position-bar" role="group" aria-label="Story position">
        <span className="muted">Loading the story's chapters…</span>
      </div>
    )
  }

  if (positions.isError) {
    return (
      <div className="position-bar" role="group" aria-label="Story position">
        <span className="muted">The story's chapters can't be read: {positions.error.message}</span>
      </div>
    )
  }

  const list = positions.data.positions
  const end = list.length
  const ordinal = at === undefined ? end : ordinalOf(at, list)
  // A bare book key (the start of a book) sits on that book's first chapter.
  const index = ordinal === null ? end : Math.ceil(ordinal)
  const next = ordinal === null ? end : Math.floor(ordinal) + 1
  const current: PositionView | undefined = list[index]
  const unknown = at !== undefined && ordinal === null
  const label = at === undefined ? 'Everything (the end of the story)' : unknown ? `'${at}', which isn't a position in this story` : at === current?.book ? `${current.bookName}, start` : current ? positionLabel(current) : at

  const go = (i: number) => setAt(i >= end ? undefined : list[Math.max(0, i)].position)
  const books = [...new Map(list.map((p) => [p.book, p.bookName])).entries()]
  const book = at === undefined || unknown ? '' : current?.book ?? ''
  const chapters = list.filter((p) => p.book === book && p.chapter !== null)

  return (
    <div className="position-bar" role="group" aria-label="Story position">
      <button type="button" className="step" onClick={() => go(index - 1)} disabled={end === 0 || index === 0} aria-label="Previous chapter" title="Previous chapter">
        ‹
      </button>
      <input
        type="range"
        className="slider"
        min={0}
        max={end}
        step={1}
        value={index}
        disabled={end === 0}
        aria-label="Story position"
        aria-valuetext={label}
        onChange={(e) => go(Number(e.currentTarget.value))}
        onKeyDown={(e) => {
          const to = { ArrowLeft: index - 1, ArrowDown: index - 1, ArrowRight: next, ArrowUp: next, Home: 0, End: end }[e.key]
          if (to !== undefined && end > 0) {
            e.preventDefault()
            go(Math.min(Math.max(to, 0), end))
          }
        }}
      />
      <button type="button" className="step" onClick={() => go(next)} disabled={end === 0 || at === undefined} aria-label="Next chapter" title="Next chapter">
        ›
      </button>
      <select aria-label="Book" value={book} onChange={(e) => setAt(e.currentTarget.value ? list.find((p) => p.book === e.currentTarget.value)!.position : undefined)}>
        <option value="">Everything</option>
        {books.map(([key, name]) => (
          <option key={key} value={key}>
            {name}
          </option>
        ))}
      </select>
      <select aria-label="Chapter" value={chapters.some((p) => p.position === at) ? at : ''} disabled={chapters.length === 0} onChange={(e) => e.currentTarget.value && setAt(e.currentTarget.value)}>
        {!chapters.some((p) => p.position === at) && <option value="">{book ? 'Start' : '—'}</option>}
        {chapters.map((p) => (
          <option key={p.position} value={p.position}>
            {chapterWords(p)}
            {p.title ? `: ${p.title}` : ''}
          </option>
        ))}
      </select>
      <output className="position-label" aria-live="polite">
        {at === undefined ? <strong>Everything</strong> : <>As of <strong>{label}</strong></>}
      </output>
      {at !== undefined && (
        <button type="button" className="reset" onClick={() => setAt(undefined)}>
          Show everything
        </button>
      )}
    </div>
  )
}
