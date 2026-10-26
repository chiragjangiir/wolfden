import { useQuery } from '@tanstack/react-query'
import { Link, Navigate } from '@tanstack/react-router'
import { count, denQuery } from '../api'
import { ErrorState, Loading } from '../components/States'

/**
 * The den's home page. A den with one universe (or a server pinned to one,
 * `serve --universe`) goes straight to that universe's overview; otherwise
 * it lists them.
 */
export function Home() {
  const den = useQuery(denQuery)

  if (den.isPending) {
    return <Loading what="the den" />
  }

  if (den.isError) {
    return (
      <div className="page-body">
        <h1>Wolfden</h1>
        <ErrorState error={den.error} />
      </div>
    )
  }

  const { name, pinnedUniverse, universes } = den.data
  const only = pinnedUniverse ?? (universes.length === 1 ? universes[0].key : null)
  if (only) {
    return <Navigate to="/u/$universe" params={{ universe: only }} replace />
  }

  return (
    <div className="page-body">
      <h1>{name}</h1>
      {universes.length === 0 ? (
        <p>This den has no universes yet.</p>
      ) : (
        <>
          <p>Choose a universe.</p>
          <ul className="card-list">
            {universes.map((u) => (
              <li key={u.key}>
                <Link to="/u/$universe" params={{ universe: u.key }} search={{ at: undefined }}>
                  {u.name}
                </Link>{' '}
                <code>{u.key}</code>: {count(u.series, 'series', 'series')}, {count(u.books, 'book', 'books')}, {count(u.entities, 'note', 'notes')}
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  )
}
