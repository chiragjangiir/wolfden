import { getRouteApi } from '@tanstack/react-router'
import { EntityPageView } from './EntityPageView'
import { HistoryPage } from './HistoryPage'

const route = getRouteApi('/u/$universe/e/$')

/** An entity's page, or with `?view=history` its history (#27). */
export function EntityRoute() {
  const { view } = route.useSearch()
  return view === 'history' ? <HistoryPage /> : <EntityPageView />
}
