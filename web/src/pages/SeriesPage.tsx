import { useQuery } from '@tanstack/react-query'
import { Link, getRouteApi } from '@tanstack/react-router'
import { hierarchyQuery } from '../api'
import { ErrorState, Loading } from '../components/States'
import { usePosition } from '../hooks'
import { BookList, Counts } from './UniversePage'

const route = getRouteApi('/u/$universe/s/$series')

/** A series: its books in series order. */
export function SeriesPage() {
  const { universe, series } = route.useParams()
  const { at } = usePosition()
  const hierarchy = useQuery(hierarchyQuery(universe, at))

  if (hierarchy.isPending) {
    return <Loading what="the series" />
  }

  if (hierarchy.isError) {
    return <ErrorState error={hierarchy.error} />
  }

  const u = hierarchy.data.universe
  const s = u.series.find((x) => x.key === series)
  if (!s) {
    return (
      <div className="page-body">
        <h1>No such series</h1>
        <p>{u.name} has no series called '{series}'.</p>
      </div>
    )
  }

  return (
    <div className="page-body">
      <nav className="breadcrumb" aria-label="Where this is">
        <Link to="/u/$universe" params={{ universe }}>
          {u.name}
        </Link>
      </nav>
      <p className="eyebrow">Series</p>
      <h1>{s.name}</h1>
      <h2>Books</h2>
      <BookList universe={universe} books={s.books} empty="This series has no books yet." />
      <Counts counts={s.entities} title="Filed in the series" />
    </div>
  )
}
