import { useQuery } from '@tanstack/react-query'
import { Link, getRouteApi } from '@tanstack/react-router'
import { hierarchyQuery, positionsQuery, type BookNode, type SeriesNode, type UniverseNode } from '../api'
import { FreezeControl } from '../components/FreezeControl'
import { ErrorState, Loading } from '../components/States'
import { useDocumentTitle, usePosition } from '../hooks'
import { ordinalOf } from '../story'
import { Counts } from './UniversePage'

const route = getRouteApi('/u/$universe/b/$book')

/** Finds a book in a universe, with the series it's in, if any. */
export function findBook(u: UniverseNode, key: string): { book: BookNode; series: SeriesNode | null } | null {
  for (const s of u.series) {
    const book = s.books.find((b) => b.key === key)
    if (book) {
      return { book, series: s }
    }
  }

  const book = u.books.find((b) => b.key === key)
  return book ? { book, series: null } : null
}

/** A book: its chapters with their titles, each a link to the chapter. */
export function BookPage() {
  const { universe, book } = route.useParams()
  const { at } = usePosition()
  const hierarchy = useQuery(hierarchyQuery(universe, at))
  const positions = useQuery(positionsQuery(universe))
  useDocumentTitle(hierarchy.data ? findBook(hierarchy.data.universe, book)?.book.name : undefined)

  if (hierarchy.isPending) {
    return <Loading what="the book" />
  }

  if (hierarchy.isError) {
    return <ErrorState error={hierarchy.error} />
  }

  const u = hierarchy.data.universe
  const found = findBook(u, book)
  if (!found) {
    return (
      <div className="page-body">
        <h1>No such book</h1>
        <p>{u.name} has no book called '{book}'.</p>
      </div>
    )
  }

  const { book: b, series } = found
  const list = positions.data?.positions ?? []
  const here = at === undefined ? null : ordinalOf(at, list)
  return (
    <div className="page-body">
      <nav className="breadcrumb" aria-label="Where this is">
        <Link to="/u/$universe" params={{ universe }}>
          {u.name}
        </Link>
        {series && (
          <>
            {' › '}
            <Link to="/u/$universe/s/$series" params={{ universe, series: series.key }}>
              {series.name}
            </Link>
          </>
        )}
      </nav>
      <p className="eyebrow">Book{b.status === 'final' ? ', final' : ''}</p>
      <h1>{b.name}</h1>
      <FreezeControl universe={universe} book={b.key} />
      <h2>Chapters</h2>
      {b.chapters.length === 0 ? (
        <p className="muted">This book has no chapters yet.</p>
      ) : (
        <ol className="chapters">
          {b.chapters.map((c) => {
            const ordinal = ordinalOf(c.position, list)
            const later = here !== null && ordinal !== null && ordinal > here
            return (
              <li key={c.key} className={later ? 'later' : undefined} aria-current={c.position === at ? 'step' : undefined}>
                <Link to="/u/$universe/b/$book/c/$chapter" params={{ universe, book: b.key, chapter: c.key }}>
                  <span className="chapter-number">{c.number ?? ''}</span>
                  <span className="chapter-title">{c.title ?? `Chapter ${c.key}`}</span>
                </Link>
                {c.position === at && <span className="badge">you are here</span>}
              </li>
            )
          })}
        </ol>
      )}
      <Counts counts={b.entities} title={at === undefined ? 'Filed in this book' : 'Filed in this book, met by this point'} />
    </div>
  )
}
