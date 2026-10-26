import { useEffect, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from '@tanstack/react-router'
import { denQuery, hierarchyQuery, type BookNode, type EntitySummary, type ScopeKind } from '../api'
import { useEntitiesInView } from '../hooks'
import { EntityList } from './EntityList'
import { typePlural } from './Links'
import { ErrorState, Loading } from './States'

/**
 * The hierarchy tree (#24): universe → series in story order → books →
 * chapters and entity types → entities. Universe and series notes sit under
 * their own scope. As of a position, each type group lists only what the
 * reader has met, with a peek at the rest.
 */
export function Sidebar({ universe }: { universe: string | undefined }) {
  const den = useQuery(denQuery)
  const navigate = useNavigate()
  const universes = den.data?.universes ?? []

  return (
    <div className="tree">
      {universes.length > 1 && (
        <label className="switcher">
          <span>Universe</span>
          <select
            value={universe ?? ''}
            onChange={(e) =>
              // Positions belong to one universe, so a switch starts at everything.
              void navigate({ to: '/u/$universe', params: { universe: e.currentTarget.value }, search: { at: undefined } })
            }
          >
            {!universe && <option value="">Choose…</option>}
            {universes.map((u) => (
              <option key={u.key} value={u.key}>
                {u.name}
              </option>
            ))}
          </select>
        </label>
      )}
      {universe ? <UniverseTree universe={universe} /> : <p className="muted">Choose a universe to browse it.</p>}
    </div>
  )
}

function UniverseTree({ universe }: { universe: string }) {
  const hierarchy = useQuery(hierarchyQuery(universe, undefined))
  const view = useEntitiesInView(universe)
  const params = useParams({ strict: false }) as { book?: string; chapter?: string; _splat?: string }

  if (hierarchy.isPending || view.isPending) {
    return <Loading what="the contents" />
  }

  const error = hierarchy.error ?? view.error
  if (error) {
    return <ErrorState error={error} />
  }

  const u = hierarchy.data!.universe
  const all = view.all!.entities
  const current = all.find((e) => e.key === params._splat || e.id === params._splat || e.path === params._splat)
  const byScope = groupByScope(all)
  const scopeGroups = (kind: ScopeKind, key: string) => (
    <TypeGroups universe={universe} entities={byScope.get(`${kind}:${key}`) ?? []} view={view} current={current} />
  )
  const book = (b: BookNode) => (
    <TreeNode
      key={b.key}
      label={<Link to="/u/$universe/b/$book" params={{ universe, book: b.key }}>{b.name}</Link>}
      open={params.book === b.key || current?.scopeKey === b.key || u.books.length + u.series.reduce((n, s) => n + s.books.length, 0) === 1}
    >
      {b.chapters.length > 0 && (
        <TreeNode label="Chapters" open={params.book === b.key && params.chapter !== undefined}>
          <ol className="chapter-list">
            {b.chapters.map((c) => (
              <li key={c.key}>
                <Link to="/u/$universe/b/$book/c/$chapter" params={{ universe, book: b.key, chapter: c.key }}>
                  {c.number !== null && `${c.number}. `}
                  {c.title ?? `Chapter ${c.key}`}
                </Link>
              </li>
            ))}
          </ol>
        </TreeNode>
      )}
      {scopeGroups('book', b.key)}
    </TreeNode>
  )

  return (
    <nav aria-label="Contents">
      <p className="tree-root">
        <Link to="/u/$universe" params={{ universe }} activeOptions={{ exact: true }}>
          {u.name}
        </Link>
      </p>
      {view.all!.truncated && <p className="muted">Only the first {all.length} notes are listed.</p>}
      {scopeGroups('universe', u.key)}
      {u.series.map((s) => (
        <TreeNode key={s.key} label={<Link to="/u/$universe/s/$series" params={{ universe, series: s.key }}>{s.name}</Link>} open>
          {scopeGroups('series', s.key)}
          {s.books.map(book)}
        </TreeNode>
      ))}
      {u.books.map(book)}
    </nav>
  )
}

function groupByScope(entities: readonly EntitySummary[]): Map<string, EntitySummary[]> {
  const groups = new Map<string, EntitySummary[]>()
  for (const e of entities) {
    const key = `${e.scopeKind}:${e.scopeKey}`
    groups.set(key, [...(groups.get(key) ?? []), e])
  }

  return groups
}

function TypeGroups({
  universe,
  entities,
  view,
  current,
}: {
  universe: string
  entities: readonly EntitySummary[]
  view: ReturnType<typeof useEntitiesInView>
  current: EntitySummary | undefined
}) {
  const { split } = view
  const types = [...new Set(entities.map((e) => e.type))].sort()
  return (
    <>
      {types.map((type) => {
        const { shown, hidden } = split(entities.filter((e) => e.type === type))
        return (
          <TreeNode
            key={type}
            label={
              <>
                {typePlural(type)} <span className="count">{shown.length}</span>
              </>
            }
            open={current !== undefined && current.type === type && entities.includes(current)}
          >
            <EntityList key={view.at ?? ''} universe={universe} shown={shown} hidden={hidden} current={current?.key} />
          </TreeNode>
        )
      })}
    </>
  )
}

/** A node of the tree that opens and closes. */
function TreeNode({ label, open, children }: { label: ReactNode; open?: boolean; children: ReactNode }) {
  const [isOpen, setOpen] = useState(open ?? false)
  // Opens when the page moves to something inside it; the writer closes it.
  useEffect(() => {
    if (open) {
      setOpen(true)
    }
  }, [open])
  return (
    <details className="tree-node" open={isOpen} onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary>{label}</summary>
      <div className="tree-children">{children}</div>
    </details>
  )
}
