import { useState } from 'react'
import { useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { getRouteApi } from '@tanstack/react-router'
import { entityQuery, type ContainerRef } from '../api'
import { FileDiffView } from '../components/DiffView'
import { EntityTabs } from '../components/EntityTabs'
import { ErrorState, Loading } from '../components/States'
import type { EntitySearch } from '../entitySearch'
import { useDocumentTitle, useEntitiesInView, usePosition } from '../hooks'
import { historyDiffQuery, historyQuery, type DiffSelection, type HistoryDiffResult, type HistoryEntry, type HistoryResult } from '../historyApi'
import { Breadcrumb, NotInView } from './EntityPageView'
import '../history.css'

const route = getRouteApi('/u/$universe/e/$')

/** The uncommitted changes, in the compare boxes: the note as it is now. */
const NOW = 'now'

/**
 * An entity's history (#27): the checkpoints that changed its file, newest
 * first, a page at a time, with "Uncommitted changes" on top when the file
 * differs from the latest checkpoint; and the diff of the one selected, or
 * of two compared.
 *
 * History isn't a story view, so it ignores the position. But a note the
 * reader hasn't met at the position stays hidden, as on its Note tab: the
 * same banner, and its history, greyed, only after "Show it anyway", which
 * the two tabs share (maintainer decision, #26). Nothing of the history is
 * fetched until then.
 */
export function HistoryPage() {
  const { universe, _splat: key = '' } = route.useParams()
  const { at } = usePosition()
  const page = useQuery(entityQuery(universe, key, at))
  const view = useEntitiesInView(universe)
  useDocumentTitle(page.data ? (page.data.entity ? `${page.data.entity.name}: history` : 'Not met yet') : undefined)

  if (page.isPending) {
    return <Loading what="the note" />
  }

  if (page.isError) {
    return <ErrorState error={page.error} />
  }

  if (!page.data.entity) {
    const summary = view.all?.entities.find((e) => e.key === key || e.id === key || e.path === key)
    return (
      <NotInView
        universe={universe}
        entityKey={key}
        summary={summary}
        note={page.data.note}
        positions={view.positions ?? []}
        at={at}
        className="page-body history"
      >
        <History universe={universe} entityKey={key} scope={undefined} />
      </NotInView>
    )
  }

  return <History universe={universe} entityKey={key} scope={page.data.entity.scope} />
}

/**
 * The history itself. `scope` is the note's breadcrumb from its page at the
 * position; for a note shown anyway it's undefined, and the full view gives
 * it. Shown anyway, it sits inside the banner's page, so it isn't wrapped in
 * a page of its own.
 */
function History({ universe, entityKey, scope }: { universe: string; entityKey: string; scope: ContainerRef[] | undefined }) {
  const outer = scope === undefined ? 'history' : 'page-body history'
  const search = route.useSearch()
  const history = useInfiniteQuery(historyQuery(universe, entityKey))
  const uncommitted = useQuery(historyDiffQuery(universe, entityKey, {}))
  const full = useQuery({ ...entityQuery(universe, entityKey, undefined), enabled: scope === undefined })
  const crumbs = scope ?? full.data?.entity?.scope

  const entries = history.data?.pages.flatMap((p) => p.entries ?? []) ?? []
  const hasUncommitted = uncommitted.data?.available === true && (uncommitted.data.diff?.files.length ?? 0) > 0
  const selection = chooseSelection(search, hasUncommitted, entries, uncommitted.isPending)
  const available = history.data?.pages[0]?.available === true
  const diff = useQuery({ ...historyDiffQuery(universe, entityKey, selection ?? {}), enabled: selection !== null && available })

  if (history.isPending) {
    return <Loading what="the note's history" />
  }

  if (history.isError) {
    return <ErrorState error={history.error} />
  }

  const first = history.data.pages[0]
  const header = (
    <>
      {crumbs && <Breadcrumb universe={universe} scope={crumbs} />}
      <EntityTabs universe={universe} entityKey={entityKey} current="history" />
      <header>
        <p className="eyebrow">{first.entity.type}</p>
        <h1>{first.entity.name}</h1>
      </header>
    </>
  )

  if (!first.available) {
    return (
      <div className={outer}>
        {header}
        <NoHistory result={first} />
      </div>
    )
  }

  const resolved = resolveSelection(selection, diff.data)
  return (
    <div className={outer}>
      {header}
      <p className="muted history-note">History shows every saved version of this note, whatever the story position.</p>
      <div className="history-columns">
        <CheckpointList
          universe={universe}
          entityKey={entityKey}
          path={first.entity.path}
          entries={entries}
          hasUncommitted={hasUncommitted}
          search={search}
          selection={selection}
          resolved={resolved}
          more={history.hasNextPage ? () => void history.fetchNextPage() : null}
          loadingMore={history.isFetchingNextPage}
        />
        <div className="diff-pane">
          {selection !== null && (
            <DiffPane
              title={selectionTitle(selection, entries, resolved, diff.isError)}
              isPending={diff.isPending}
              error={diff.error}
              result={diff.data}
            />
          )}
        </div>
      </div>
    </div>
  )
}

/** The page for a den without history: the API's reason, and how to start one. */
function NoHistory({ result }: { result: HistoryResult }) {
  return (
    <div className="banner" role="status">
      <p>{result.reason ?? "This den's history isn't available."}</p>
      {result.backend === 'none' && (
        <p>
          Your notes are safe as they are. To keep a history of them, make the den's folder a git repository: run <code>git init</code> in it,
          then commit your notes with git. A new den can start with one: <code>wolfden init --git</code>.
        </p>
      )}
    </div>
  )
}

/**
 * The diff to show: the one in the URL; else the uncommitted changes, if
 * there are any; else the latest checkpoint's own change; else none. Null
 * while that can't be told yet, or when there's nothing to show.
 */
export function chooseSelection(search: EntitySearch, hasUncommitted: boolean, entries: HistoryEntry[], uncommittedPending: boolean): DiffSelection | null {
  if (search.from) {
    return { from: search.from, to: search.to }
  }

  if (search.checkpoint) {
    return { checkpoint: search.checkpoint }
  }

  if (uncommittedPending) {
    return null
  }

  if (hasUncommitted) {
    return {}
  }

  return entries.length > 0 ? { checkpoint: entries[0].id } : null
}

/**
 * The commit ids the API resolved the selection's revisions to, so a short
 * id, a tag, or `HEAD~1` in the URL still finds its row. `to` is null for the
 * files as they are now. Undefined for a revision not resolved (yet).
 */
export interface ResolvedSelection {
  checkpoint?: string
  from?: string
  to?: string | null
}

export function resolveSelection(selection: DiffSelection | null, result: HistoryDiffResult | undefined): ResolvedSelection {
  const diff = result?.available ? result.diff : null
  if (!selection || !diff) {
    return {}
  }

  if (selection.checkpoint) {
    return diff.to ? { checkpoint: diff.to } : {}
  }

  if (selection.from) {
    return { from: diff.from ?? undefined, to: selection.to ? (diff.to ?? undefined) : null }
  }

  return {}
}

/** "3 days ago", or "just now". */
export function relativeTime(when: string, now: number = Date.now()): string {
  const seconds = Math.round((new Date(when).getTime() - now) / 1000)
  if (Number.isNaN(seconds)) {
    return when
  }

  if (Math.abs(seconds) < 45) {
    return 'just now'
  }

  const format = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
  const units: [Intl.RelativeTimeFormatUnit, number][] = [
    ['year', 365 * 24 * 3600],
    ['month', 30 * 24 * 3600],
    ['week', 7 * 24 * 3600],
    ['day', 24 * 3600],
    ['hour', 3600],
    ['minute', 60],
  ]
  for (const [unit, size] of units) {
    if (Math.abs(seconds) >= size) {
      return format.format(Math.round(seconds / size), unit)
    }
  }

  return format.format(Math.round(seconds / 60), 'minute')
}

/** The date and time in full, for a tooltip. */
function absoluteTime(when: string): string {
  const date = new Date(when)
  return Number.isNaN(date.getTime()) ? when : date.toLocaleString(undefined, { dateStyle: 'full', timeStyle: 'long' })
}

const KIND_WORDS: Record<string, string> = { added: 'added', modified: 'modified', renamed: 'renamed', deleted: 'deleted' }

/** A checkpoint's first line, and the rest of its message. */
function splitMessage(message: string): [string, string] {
  const trimmed = message.trim()
  const at = trimmed.indexOf('\n')
  return at < 0 ? [trimmed, ''] : [trimmed.slice(0, at).trim(), trimmed.slice(at + 1).trim()]
}

const shortId = (id: string) => id.slice(0, 7)

interface ListProps {
  universe: string
  entityKey: string
  path: string
  entries: HistoryEntry[]
  hasUncommitted: boolean
  search: EntitySearch
  selection: DiffSelection | null
  resolved: ResolvedSelection
  more: (() => void) | null
  loadingMore: boolean
}

function CheckpointList({ universe, entityKey, path, entries, hasUncommitted, search, selection, resolved, more, loadingMore }: ListProps) {
  const navigate = route.useNavigate()
  const [pending, setPending] = useState<string | null>(null)
  // The rows the URL's compare names: by the ids the API resolved, else as written.
  const compared = search.from
    ? [resolved.from ?? search.from, search.to ? (resolved.to ?? search.to) : NOW]
    : pending
      ? [pending]
      : []
  const selectedId = selection?.checkpoint ? (resolved.checkpoint ?? selection.checkpoint) : undefined

  const go = (next: EntitySearch) => {
    void navigate({ to: '/u/$universe/e/$', params: { universe, _splat: entityKey }, search: (prev) => ({ at: prev.at, ...next }) })
  }

  // The list's order: the uncommitted changes, then the checkpoints, newest first.
  const order = (id: string) => (id === NOW ? -1 : entries.findIndex((e) => e.id === id))

  const toggle = (id: string) => {
    const next = compared.includes(id) ? compared.filter((x) => x !== id) : compared.length >= 2 ? [id] : [...compared, id]
    if (next.length === 2) {
      const [newer, older] = [...next].sort((a, b) => order(a) - order(b))
      setPending(null)
      go(newer === NOW ? { view: 'history', from: older } : { view: 'history', from: older, to: newer })
      return
    }

    setPending(next[0] ?? null)
    if (search.from) {
      go({ view: 'history' })
    }
  }

  const isUncommittedShown = selection !== null && !selection.checkpoint && !selection.from
  const nowRow = hasUncommitted || search.from !== undefined
  return (
    <div className="checkpoints">
      <h2>Checkpoints</h2>
      <p className="muted compare-hint">Tick two to compare them. Dates are when each change was written.</p>
      <ol>
        {nowRow && (
          <li className={`checkpoint uncommitted${isUncommittedShown ? ' selected' : ''}`} aria-current={isUncommittedShown ? 'true' : undefined}>
            <input type="checkbox" aria-label="Compare the note as it is now" checked={compared.includes(NOW)} onChange={() => toggle(NOW)} />
            <div>
              {hasUncommitted ? (
                <button type="button" className="checkpoint-open" onClick={() => go({ view: 'history' })}>
                  Uncommitted changes
                </button>
              ) : (
                <span className="checkpoint-open">The note now</span>
              )}
              <p className="muted checkpoint-meta">{hasUncommitted ? 'Edits since the latest checkpoint' : 'No edits since the latest checkpoint'}</p>
            </div>
          </li>
        )}
        {entries.map((entry) => {
          const [subject, body] = splitMessage(entry.message)
          const change = entry.changes.find((c) => c.path === path) ?? entry.changes[0]
          const selected = selectedId === entry.id
          return (
            <li key={entry.id} className={`checkpoint${selected ? ' selected' : ''}`} aria-current={selected ? 'true' : undefined}>
              <input
                type="checkbox"
                aria-label={`Compare ${subject || '(no message)'} (${shortId(entry.id)})`}
                checked={compared.includes(entry.id)}
                onChange={() => toggle(entry.id)}
              />
              <div>
                <button type="button" className="checkpoint-open" onClick={() => go({ view: 'history', checkpoint: entry.id })}>
                  {subject || '(no message)'}
                </button>
                {change && (
                  <span className={`badge change-${change.kind}`} title={change.oldPath ? `from ${change.oldPath}` : undefined}>
                    {KIND_WORDS[change.kind] ?? change.kind}
                  </span>
                )}
                <p className="muted checkpoint-meta">
                  <time dateTime={entry.when} title={`Written ${absoluteTime(entry.when)}`}>
                    {relativeTime(entry.when)}
                  </time>{' '}
                  · {entry.author} · <code>{shortId(entry.id)}</code>
                </p>
                {body && <p className="muted checkpoint-body">{body}</p>}
              </div>
            </li>
          )
        })}
      </ol>
      {entries.length === 0 && <p className="muted">No checkpoint has recorded this note yet.</p>}
      {more && (
        <button type="button" onClick={more} disabled={loadingMore}>
          {loadingMore ? 'Loading…' : 'Show older checkpoints'}
        </button>
      )}
    </div>
  )
}

/**
 * What the diff shows, in words. A checkpoint is named by its message when
 * the list has it, else by the id the API resolved it to; never by what the
 * URL says, so a revision the API refused is "This change".
 */
export function selectionTitle(selection: DiffSelection, entries: HistoryEntry[], resolved: ResolvedSelection, failed: boolean): string {
  if (!selection.checkpoint && !selection.from) {
    return 'Uncommitted changes, since the latest checkpoint'
  }

  if (failed) {
    return 'This change'
  }

  const label = (written: string, id: string | undefined): string | null => {
    const entry = entries.find((e) => e.id === id) ?? entries.find((e) => e.id === written)
    if (entry) {
      return `“${splitMessage(entry.message)[0] || shortId(entry.id)}”`
    }

    return id ? shortId(id) : null
  }

  if (selection.checkpoint) {
    const name = label(selection.checkpoint, resolved.checkpoint)
    return name ? `What ${name} changed` : 'This change'
  }

  const from = label(selection.from!, resolved.from)
  const to = selection.to ? label(selection.to, resolved.to ?? undefined) : 'now'
  if (!from || !to) {
    return 'This change'
  }

  return selection.to ? `From ${from} to ${to}` : `Since ${from}, up to now`
}

/** The diff, under its title, with a short status line for screen readers. */
function DiffPane({ title, isPending, error, result }: { title: string; isPending: boolean; error: Error | null; result: HistoryDiffResult | undefined }) {
  const status = isPending ? '' : error ? '' : `Showing: ${title}`
  return (
    <>
      <h2>{title}</h2>
      <p className="visually-hidden" aria-live="polite">
        {status}
      </p>
      {isPending ? (
        <Loading what="the changes" />
      ) : error ? (
        <ErrorState error={error} />
      ) : !result ? null : !result.available ? (
        <p className="muted">{result.reason}</p>
      ) : result.diff && result.diff.files.length > 0 ? (
        result.diff.files.map((f) => <FileDiffView key={`${f.change.kind}:${f.change.path}`} file={f} />)
      ) : (
        <p className="muted">No changes to this note.</p>
      )}
    </>
  )
}
