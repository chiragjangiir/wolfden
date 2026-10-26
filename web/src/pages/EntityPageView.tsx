import { Fragment, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, getRouteApi, useRouterState } from '@tanstack/react-router'
import {
  entityQuery,
  type BacklinkView,
  type ContainerRef,
  type EntityPage,
  type EntitySummary,
  type LinkView,
  type PageField,
  type PageSection,
  type PositionView,
} from '../api'
import { EntityLink, LinkedText, createLinkMatcher, sectionAnchor } from '../components/Links'
import { Markdown } from '../components/Markdown'
import { ErrorState, Loading, StatusBadge } from '../components/States'
import { useDocumentTitle, useEntitiesInView, usePosition } from '../hooks'
import { chapterWords, describePosition, entryOf, ordinalOf } from '../story'

const route = getRouteApi('/u/$universe/e/$')

/**
 * An entity's page (#24): its name, type, status, scope, and where it enters
 * the story; its fields and sections, with links in-app; and what links to
 * it. As of a position it shows the canon-as-of view, and an entity the
 * reader hasn't met shows a banner saying where they first meet it, and
 * nothing else of it.
 */
export function EntityPageView() {
  const { universe, _splat: key = '' } = route.useParams()
  const { at } = usePosition()
  const page = useQuery(entityQuery(universe, key, at))
  const view = useEntitiesInView(universe)
  // Only the API's page names the note, and it's null when the reader hasn't met it.
  useDocumentTitle(page.data ? (page.data.entity?.name ?? 'Not met yet') : undefined)

  if (page.isPending) {
    return <Loading what="the note" />
  }

  if (page.isError) {
    return <ErrorState error={page.error} />
  }

  const { entity } = page.data
  if (!entity) {
    const summary = view.all?.entities.find((e) => e.key === key || e.id === key || e.path === key)
    // Keyed by the note and the position, so "Show it anyway" resets when either changes.
    return (
      <NotInView
        key={`${key}
${at ?? ''}`}
        universe={universe}
        entityKey={key}
        summary={summary}
        note={page.data.note}
        positions={view.positions ?? []}
        at={at}
      />
    )
  }

  return <EntityArticle universe={universe} entity={entity} positions={view.positions ?? []} />
}

/**
 * The banner for an entity the reader hasn't met at the position. "Show it
 * anyway" (maintainer decision, #26) shows the note's full view below it,
 * greyed, at the same position; the full view is fetched only on that
 * click, and the reveal lasts only while this page shows this note at this
 * position.
 */
function NotInView({
  universe,
  entityKey,
  summary,
  note,
  positions,
  at,
}: {
  universe: string
  entityKey: string
  summary: EntitySummary | undefined
  note: string | null
  positions: PositionView[]
  at: string | undefined
}) {
  const { setAt } = usePosition()
  const [revealed, setRevealed] = useState(false)
  const full = useQuery({ ...entityQuery(universe, entityKey, undefined), enabled: revealed })
  const entry = summary ? entryOf(summary, positions) : null
  const entryOrdinal = entry === null ? -1 : ordinalOf(entry, positions)
  const here = at === undefined ? null : ordinalOf(at, positions)
  const later = summary !== undefined && entry !== null && here !== null && entryOrdinal !== null && entryOrdinal > here
  const where = entry === null ? '' : describePosition(entry, positions)

  return (
    <div className="page-body">
      <div className="banner" role="status">
        {later ? (
          <p>
            Readers first meet <strong>{summary!.name}</strong> {where.startsWith('the start of') ? 'at' : 'in'} {where}.
          </p>
        ) : summary ? (
          <p>
            <strong>{summary.name}</strong> isn't in canon at this point in the story: its status is {summary.status}.
          </p>
        ) : (
          <p>{note ?? "The reader hasn't met this note by this point in the story."}</p>
        )}
        <p className="banner-actions">
          {later && entry && (
            <button type="button" onClick={() => setAt(entry)}>
              Go to {where}
            </button>
          )}
          <button type="button" onClick={() => setAt(undefined)}>
            Show everything
          </button>
          {!revealed && (
            <button type="button" onClick={() => setRevealed(true)}>
              Show it anyway
            </button>
          )}
        </p>
      </div>
      {revealed && (
        <div className="revealed" aria-label="Shown anyway, from the full view">
          {full.isPending ? (
            <Loading what="the note" />
          ) : full.isError ? (
            <ErrorState error={full.error} />
          ) : full.data.entity ? (
            <EntityArticle universe={universe} entity={full.data.entity} positions={positions} />
          ) : (
            <p className="muted">{full.data.note}</p>
          )}
        </div>
      )}
    </div>
  )
}

function EntityArticle({ universe, entity, positions }: { universe: string; entity: EntityPage; positions: PositionView[] }) {
  const lead = stripTitle(entity.lead, entity.name)
  // A link to a section (#section-...) arrives before the page has loaded,
  // so the router can't scroll to it; scroll once the section is here.
  const hash = useRouterState({ select: (s) => s.location.hash })
  useEffect(() => {
    if (hash) {
      document.getElementById(hash)?.scrollIntoView?.()
    }
  }, [hash, entity.key])
  return (
    <article className="page-body entity">
      <Breadcrumb universe={universe} scope={entity.scope} />
      <header>
        <p className="eyebrow">
          {entity.type} <StatusBadge status={entity.status} />
        </p>
        <h1>{entity.name}</h1>
        <p className="entry">{entryWords(entity, positions)}</p>
        {entity.aliases && entity.aliases.length > 0 && <p className="aliases">Also: {entity.aliases.join(', ')}</p>}
      </header>
      {lead && <Markdown text={lead} links={entity.leadLinks} universe={universe} />}
      {entity.fields.length > 0 && <Fields universe={universe} fields={entity.fields} positions={positions} />}
      {entity.sections.map((s) => (
        <Section key={s.line} universe={universe} section={s} positions={positions} />
      ))}
      <Backlinks universe={universe} backlinks={entity.backlinks} positions={positions} />
    </article>
  )
}

/** The lead without a first `# Name` heading, which the page shows already. */
export function stripTitle(lead: string | null, name: string): string {
  if (!lead) {
    return ''
  }

  const lines = lead.split('\n')
  if (lines[0].trim() === `# ${name}`) {
    lines.shift()
  }

  return lines.join('\n').trim()
}

function Breadcrumb({ universe, scope }: { universe: string; scope: ContainerRef[] }) {
  return (
    <nav className="breadcrumb" aria-label="Where this is filed">
      {scope.map((c, i) => (
        <Fragment key={`${c.kind}:${c.key}`}>
          {i > 0 && ' › '}
          <ContainerLink universe={universe} container={c} />
        </Fragment>
      ))}
    </nav>
  )
}

function ContainerLink({ universe, container }: { universe: string; container: ContainerRef }) {
  switch (container.kind) {
    case 'series':
      return (
        <Link to="/u/$universe/s/$series" params={{ universe, series: container.key }}>
          {container.name}
        </Link>
      )
    case 'book':
      return (
        <Link to="/u/$universe/b/$book" params={{ universe, book: container.key }}>
          {container.name}
        </Link>
      )
    default:
      return (
        <Link to="/u/$universe" params={{ universe }}>
          {container.name}
        </Link>
      )
  }
}

/** "Enters at Dracula chapter 5", or "From the start of Dracula", or "From the start of the story". */
export function entryWords(entity: Pick<EntityPage, 'entry' | 'scope' | 'scopeKind'>, positions: PositionView[]): string {
  const { entry } = entity
  if (!entry.inherited && entry.position) {
    return `Enters at ${describePosition(entry.position, positions)}`
  }

  const scope = entity.scope[entity.scope.length - 1]
  return entity.scopeKind === 'universe' || !scope ? 'From the start of the story' : `From the start of ${scope.name}`
}

/** "Dracula ch. 18", or "ch. 18" when the universe has one book, for a field entry's span. */
function shortPosition(position: string, positions: PositionView[]): string {
  const p = positions.find((x) => x.position === position)
  if (!p || p.chapter === null) {
    return describePosition(position, positions)
  }

  const oneBook = positions.every((x) => x.book === p.book)
  return `${oneBook ? '' : `${p.bookName} `}${p.number !== null ? `ch. ${p.number}` : chapterWords(p)}`
}

function Span({ from, until, positions }: { from: string | null; until: string | null; positions: PositionView[] }) {
  if (!from && !until) {
    return null
  }

  const text = from && until ? `${shortPosition(from, positions)} to ${shortPosition(until, positions)}` : from ? `from ${shortPosition(from, positions)}` : `until ${shortPosition(until!, positions)}`
  return <span className="badge span">{text}</span>
}

function Fields({ universe, fields, positions }: { universe: string; fields: PageField[]; positions: PositionView[] }) {
  // A field's entries are listed one after another; show them under one name.
  const groups: PageField[][] = []
  for (const f of fields) {
    const last = groups[groups.length - 1]
    if (last && last[0].name === f.name) {
      last.push(f)
    } else {
      groups.push([f])
    }
  }

  return (
    <dl className="fields">
      {groups.map((entries) => (
        <div key={`${entries[0].name}:${entries[0].line}`} className="field">
          <dt>{entries[0].name}</dt>
          {entries.map((f) => (
            <dd key={f.line} className={f.live === false ? 'not-live' : undefined} title={f.live === false ? 'No view of the story shows this entry.' : undefined}>
              <FieldValue value={f.value} links={f.links} universe={universe} />{' '}
              {f.kind === 'default' && <span className="badge">type default</span>}
              <Span from={f.from} until={f.until} positions={positions} />
              {f.pinned && <span className="badge">pinned</span>}
              <StatusBadge status={f.status} />
            </dd>
          ))}
        </div>
      ))}
    </dl>
  )
}

/** A field's value: text with its links, a list, or a mapping (such as a date's text and sort key). */
export function FieldValue({ value, links, universe }: { value: unknown; links: LinkView[]; universe: string }) {
  const match = useMemo(() => createLinkMatcher(links), [links])
  const render = (v: unknown): ReactNode => {
    if (v === null || v === undefined || v === '') {
      return <span className="muted">empty</span>
    }

    if (typeof v === 'string') {
      return <LinkedText text={v} match={match} universe={universe} />
    }

    if (Array.isArray(v)) {
      return (
        <ul className="inline-list">
          {v.map((item, i) => (
            <li key={i}>{render(item)}</li>
          ))}
        </ul>
      )
    }

    if (typeof v === 'object') {
      const o = v as Record<string, unknown>
      if (typeof o.text === 'string') {
        return render(o.text)
      }

      return (
        <dl className="mapping">
          {Object.entries(o).map(([k, item]) => (
            <div key={k}>
              <dt>{k}</dt>
              <dd>{render(item)}</dd>
            </div>
          ))}
        </dl>
      )
    }

    return String(v)
  }

  return <>{render(value)}</>
}

function Section({ universe, section, positions }: { universe: string; section: PageSection; positions: PositionView[] }) {
  const Heading = `h${Math.min(Math.max(section.level, 2), 6)}` as 'h2'
  return (
    <section className={`section level-${section.level}${section.bookkeeping ? ' bookkeeping' : ''}`}>
      <Heading id={sectionAnchor(section.name)}>
        {section.name} <StatusBadge status={section.status} />
        <Span from={section.from} until={section.until} positions={positions} />
        {section.pinned && <span className="badge">pinned</span>}
        {section.bookkeeping && <span className="badge">bookkeeping</span>}
      </Heading>
      {section.body.trim() && <Markdown text={section.body} links={section.links} universe={universe} />}
    </section>
  )
}

/** Where a backlink is written, in words. */
export function backlinkPlace(b: BacklinkView, positions: PositionView[]): string {
  switch (b.site) {
    case 'entity_lead':
      return 'in its opening'
    case 'entity_section':
      return `in “${b.section}”`
    case 'entity_field':
    case 'entity_field_entry':
      return `in ${b.field}`
    case 'chapter_field':
      return `${b.field} of ${b.chapter ? describePosition(b.chapter, positions) : 'a chapter'}`
    case 'chapter_default':
      return `chapter default ${b.field}`
    case 'outline_row':
      return `outline row ${b.outlineRow}${b.chapter ? `, ${describePosition(b.chapter, positions)}` : ''}`
    case 'outline_notes':
      return 'in the outline notes'
    case 'container_body':
      return 'in its notes'
    default:
      return b.site
  }
}

/** "Linked from": the links that reach this entity, grouped by the note or book they're in. */
function Backlinks({ universe, backlinks, positions }: { universe: string; backlinks: BacklinkView[]; positions: PositionView[] }) {
  const groups = new Map<string, BacklinkView[]>()
  for (const b of backlinks) {
    const key = b.source ? `e:${b.source.key}` : `c:${b.container?.kind}:${b.container?.key}:${b.path}`
    groups.set(key, [...(groups.get(key) ?? []), b])
  }

  return (
    <section className="backlinks">
      <h2>Linked from</h2>
      {groups.size === 0 ? (
        <p className="muted">Nothing links here{backlinks.length === 0 ? ' yet' : ''}.</p>
      ) : (
        <ul>
          {[...groups.values()].map((items) => {
            const first = items[0]
            return (
              <li key={`${first.path}:${first.line}`}>
                {first.source ? (
                  <>
                    <EntityLink universe={universe} entityKey={first.source.key}>
                      {first.source.name}
                    </EntityLink>{' '}
                    <span className="muted">{first.source.type}</span>
                  </>
                ) : first.container ? (
                  <>
                    <ContainerLink universe={universe} container={first.container} />
                    {first.site.startsWith('outline_') && <span className="muted"> outline</span>}
                  </>
                ) : (
                  first.path
                )}
                <ul className="places">
                  {items.map((b) => (
                    <li key={`${b.line}:${b.column}`}>
                      {backlinkPlace(b, positions)}
                      {b.bookkeeping && <span className="badge">bookkeeping</span>} <StatusBadge status={b.status} />
                    </li>
                  ))}
                </ul>
              </li>
            )
          })}
        </ul>
      )}
    </section>
  )
}
