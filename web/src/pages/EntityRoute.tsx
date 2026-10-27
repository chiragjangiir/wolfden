import { getRouteApi } from '@tanstack/react-router'
import { RevealProvider } from '../reveal'
import { EntityPageView } from './EntityPageView'
import { HistoryPage } from './HistoryPage'

const route = getRouteApi('/u/$universe/e/$')

/**
 * An entity's page, or with `?view=history` its history (#27). The two tabs
 * share one "Show it anyway" for the note at the position. The history is
 * keyed by the note, so nothing it holds (a compare tick) carries over to
 * another note.
 */
export function EntityRoute() {
  const { universe, _splat: key = '' } = route.useParams()
  const { view, at } = route.useSearch()
  return (
    <RevealProvider id={JSON.stringify([universe, key, at ?? null])}>
      {view === 'history' ? <HistoryPage key={JSON.stringify([universe, key])} /> : <EntityPageView key={JSON.stringify([universe, key])} />}
    </RevealProvider>
  )
}
