import { useCallback } from 'react'
import { useNavigate, useParams, useSearch } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { entitiesQuery, positionsQuery } from './api'
import { splitByView, type LaterEntity } from './story'
import type { EntitySummary } from './api'
import type { StorySearch } from './router'

/**
 * The story position from the URL (`?at=`), or undefined for everything, and
 * a way to change it. A change replaces the history entry, so Back leaves the
 * page rather than stepping back through chapters.
 */
export function usePosition(): { at: string | undefined; setAt: (at: string | undefined) => void } {
  const { at } = useSearch({ strict: false }) as StorySearch
  const navigate = useNavigate()
  const setAt = useCallback(
    (next: string | undefined) => {
      void navigate({ to: '.', search: (prev: StorySearch) => ({ ...prev, at: next }), replace: true })
    },
    [navigate],
  )
  return { at, setAt }
}

/** The universe in the URL, if the page is in one. */
export function useUniverseKey(): string | undefined {
  return (useParams({ strict: false }) as { universe?: string }).universe
}

/**
 * The universe's entities, and which of them the reader has met at the
 * position: the unfiltered list, compared with the list as of the position.
 */
export function useEntitiesInView(universe: string) {
  const { at } = usePosition()
  const all = useQuery(entitiesQuery(universe, undefined))
  const inView = useQuery({ ...entitiesQuery(universe, at), enabled: at !== undefined })
  const positions = useQuery(positionsQuery(universe))

  const isPending = all.isPending || positions.isPending || (at !== undefined && inView.isPending)
  const error = all.error ?? positions.error ?? (at !== undefined ? inView.error : null)
  const viewKeys = at !== undefined && inView.data ? new Set(inView.data.entities.map((e) => e.key)) : null

  /** Splits some of the universe's entities into those met and those met later. */
  const split = (entities: readonly EntitySummary[]): { shown: EntitySummary[]; hidden: LaterEntity[] } =>
    splitByView(entities, viewKeys, at, positions.data?.positions ?? [])

  return { at, all: all.data, positions: positions.data?.positions, isPending, error, split }
}
