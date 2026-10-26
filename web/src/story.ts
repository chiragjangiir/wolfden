import type { EntitySummary, PositionView } from './api'

// Story positions as the pages show them, and which notes the reader has met.
// A position is what the API's asOf takes: "book:chapter", or a bare book key
// for the start of that book (format.md §8).

/**
 * A chapter in words: "chapter 5", or, for a chapter without a number (such
 * as Dracula's closing note), its title in quotes, else "chapter <key>".
 */
export function chapterWords(p: Pick<PositionView, 'chapter' | 'number' | 'title'>): string {
  if (p.number !== null) {
    return `chapter ${p.number}`
  }

  return p.title ? `“${p.title}”` : `chapter ${p.chapter}`
}

/** "Dracula, chapter 5: Letters—Lucy and Mina", or "Dracula, “Note”", for the position bar. */
export function positionLabel(p: PositionView): string {
  if (p.chapter === null) {
    return `${p.bookName}, start`
  }

  return `${p.bookName}, ${chapterWords(p)}${p.title && p.number !== null ? `: ${p.title}` : ''}`
}

/**
 * A position in words for a sentence: "Dracula chapter 11", "Dracula,
 * “Note”" for a chapter without a number, or "the start of Dracula" for a
 * bare book key. A position the list doesn't have is given as
 * written.
 */
export function describePosition(position: string, positions: readonly PositionView[]): string {
  const exact = positions.find((p) => p.position === position)
  if (exact) {
    if (exact.chapter === null) {
      return `the start of ${exact.bookName}`
    }

    return exact.number === null ? `${exact.bookName}, ${chapterWords(exact)}` : `${exact.bookName} ${chapterWords(exact)}`
  }

  const book = positions.find((p) => p.book === position)
  return book ? `the start of ${book.bookName}` : position
}

/**
 * Where a position falls in story order, as an index into `positions`: a
 * chapter's own index, or just before a book's first chapter for its bare
 * key. Null when the list doesn't have it.
 */
export function ordinalOf(position: string, positions: readonly PositionView[]): number | null {
  const exact = positions.findIndex((p) => p.position === position)
  if (exact >= 0) {
    return exact
  }

  const book = positions.findIndex((p) => p.book === position)
  return book >= 0 ? book - 0.5 : null
}

/**
 * Where an entity enters canon, as a position: its own `from`, or the start
 * of the scope it's filed in (format.md F-ENT-6). Null for the start of the
 * story (an entity filed in the universe) or a series with no books yet.
 */
export function entryOf(entity: Pick<EntitySummary, 'from' | 'scopeKind' | 'scopeKey'>, positions: readonly PositionView[]): string | null {
  if (entity.from) {
    return entity.from
  }

  switch (entity.scopeKind) {
    case 'book':
      return entity.scopeKey
    case 'series':
      return positions.find((p) => p.series === entity.scopeKey)?.book ?? null
    default:
      return null
  }
}

/** A note the reader hasn't met at a position, and why. */
export interface LaterEntity {
  entity: EntitySummary
  /** Where it enters, in words, or null when it's out of view for its status instead. */
  entersAt: string | null
  /** The position to jump to for it, if any. */
  entry: string | null
  /** True if it enters after the position; false if it's out of view for another reason, such as its status. */
  later: boolean
}

/**
 * Splits `all` (the unfiltered list) into what the reader has met at a
 * position (those in `inView`, the list as of it) and what they haven't. With
 * no position (`inView` null), everything is shown.
 */
export function splitByView(
  all: readonly EntitySummary[],
  inView: ReadonlySet<string> | null,
  at: string | undefined,
  positions: readonly PositionView[],
): { shown: EntitySummary[]; hidden: LaterEntity[] } {
  if (inView === null || at === undefined) {
    return { shown: [...all], hidden: [] }
  }

  const here = ordinalOf(at, positions)
  const shown: EntitySummary[] = []
  const hidden: LaterEntity[] = []
  for (const entity of all) {
    if (inView.has(entity.key)) {
      shown.push(entity)
      continue
    }

    const entry = entryOf(entity, positions)
    const entryOrdinal = entry === null ? -1 : ordinalOf(entry, positions)
    const later = here !== null && entryOrdinal !== null && entryOrdinal > here
    hidden.push({ entity, entry, entersAt: later && entry !== null ? describePosition(entry, positions) : null, later })
  }

  return { shown, hidden }
}
