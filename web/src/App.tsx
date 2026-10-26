import { useQuery } from '@tanstack/react-query'
import { count, denQuery } from './api'

/** The den's name and its universes. The hierarchy browser and the rest of M3 build on this (#24 to #27). */
export function App() {
  const den = useQuery(denQuery)

  if (den.isPending) {
    return <main><p>Loading the den…</p></main>
  }

  if (den.isError) {
    return (
      <main>
        <h1>Wolfden</h1>
        <p role="alert">{den.error.message}</p>
      </main>
    )
  }

  const { name, pinnedUniverse, universes } = den.data
  return (
    <main>
      <h1>{name}</h1>
      {pinnedUniverse && <p>Showing only universe <code>{pinnedUniverse}</code>.</p>}
      {universes.length === 0 ? (
        <p>This den has no universes yet.</p>
      ) : (
        <ul>
          {universes.map((u) => (
            <li key={u.key}>
              <strong>{u.name}</strong> <code>{u.key}</code>: {count(u.series, 'series', 'series')},{' '}
              {count(u.books, 'book', 'books')}, {count(u.entities, 'note', 'notes')}
            </li>
          ))}
        </ul>
      )}
    </main>
  )
}
