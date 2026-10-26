import { useState } from 'react'
import type { EntitySummary } from '../api'
import type { LaterEntity } from '../story'
import { EntityLink } from './Links'
import { StatusBadge } from './States'

/**
 * A list of entities: those the reader has met at the position, then a
 * control that peeks at the rest, greyed, each with where it enters (#24,
 * #26). At no position, everything is listed.
 */
export function EntityList({
  universe,
  shown,
  hidden,
  current,
  empty,
}: {
  universe: string
  shown: readonly EntitySummary[]
  hidden: readonly LaterEntity[]
  current?: string
  empty?: string
}) {
  const [peek, setPeek] = useState(false)
  const allLater = hidden.every((h) => h.later)
  const more = `${hidden.length} more ${allLater ? (hidden.length === 1 ? 'appears later' : 'appear later') : 'not in view here'}`

  return (
    <>
      {shown.length === 0 && hidden.length === 0 && empty && <p className="muted">{empty}</p>}
      <ul className="entity-list">
        {shown.map((e) => (
          <li key={e.key}>
            <EntityLink universe={universe} entityKey={e.key} className={e.key === current ? 'current' : undefined}>
              {e.name}
            </EntityLink>{' '}
            <StatusBadge status={e.status} />
          </li>
        ))}
        {peek &&
          hidden.map((h) => (
            <li key={h.entity.key} className="later">
              <EntityLink universe={universe} entityKey={h.entity.key} className={h.entity.key === current ? 'current' : undefined}>
                {h.entity.name}
              </EntityLink>{' '}
              <span className="entry-note">{h.later ? `enters at ${h.entersAt}` : `not in canon: ${h.entity.status}`}</span>
            </li>
          ))}
      </ul>
      {hidden.length > 0 && (
        <button type="button" className="peek" aria-expanded={peek} onClick={() => setPeek(!peek)}>
          {peek ? `Hide the ${hidden.length} not met yet` : more}
        </button>
      )}
    </>
  )
}
