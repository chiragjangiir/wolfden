import type { ReactNode } from 'react'
import { Link } from '@tanstack/react-router'
import type { LinkView } from '../api'

/** A link to an entity's page, by its key (its id, or its path while it has none). */
export function EntityLink({ universe, entityKey, className, children }: { universe: string; entityKey: string; className?: string; children: ReactNode }) {
  return (
    <Link to="/u/$universe/e/$" params={{ universe, _splat: entityKey }} className={className}>
      {children}
    </Link>
  )
}

/** "character" → "Characters", for a group of entities of one type. */
export function typePlural(type: string): string {
  const word = type.replace(/[-_]/g, ' ')
  const plural = /(s|x|z|ch|sh)$/.test(word) ? `${word}es` : /[^aeiou]y$/.test(word) ? `${word.slice(0, -1)}ies` : `${word}s`
  return plural.charAt(0).toUpperCase() + plural.slice(1)
}

/** The anchor of a section on an entity page. */
export function sectionAnchor(name: string): string {
  return `section-${name.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-|-$/g, '')}`
}

/** A `[[...]]` link as written in markdown or a field value. */
export const WIKI_LINK = /\[\[[^[\]\n]+?\]\]/g

/**
 * Finds each `[[...]]` link's resolution in the API's `links`, by exact text,
 * taking them in order, so two links with the same text get one each.
 */
export function createLinkMatcher(links: readonly LinkView[]): (text: string) => LinkView | undefined {
  const used = new Set<number>()
  return (text) => {
    let index = links.findIndex((l, i) => l.text === text && !used.has(i))
    if (index < 0) {
      index = links.findIndex((l) => l.text === text)
    }

    if (index < 0) {
      return undefined
    }

    used.add(index)
    return links[index]
  }
}

/** What a link shows: its label, else the entity it reaches, else its target as written. */
export function linkText(link: LinkView): string {
  const name = link.label ?? link.entity?.name ?? link.target
  return link.heading && !link.label ? `${name}: ${link.heading}` : name
}

/**
 * A `[[...]]` link, as the index resolved it: a link to the entity it
 * reaches; a broken link, marked so, with why; an ambiguous one, with the
 * entities it matches in its tooltip; or, as of a position, plain text for a
 * link to an entity the reader hasn't met yet, which doesn't say what it
 * reaches.
 */
export function WikiLink({ link, universe }: { link: LinkView; universe: string }) {
  const text = linkText(link)
  if (link.outcome === 'resolved' && link.entity) {
    return (
      <Link
        to="/u/$universe/e/$"
        params={{ universe, _splat: link.entity.key }}
        hash={link.section ? sectionAnchor(link.section) : undefined}
        className="wikilink"
      >
        {text}
      </Link>
    )
  }

  if (link.outcome === 'resolved') {
    return (
      <span className="wikilink wikilink-later" title="The reader hasn't met this yet at this point in the story.">
        {text}
      </span>
    )
  }

  if (link.outcome === 'ambiguous') {
    const matches = link.matches ?? []
    const title = matches.length
      ? `Ambiguous link: it matches ${matches.map((m) => `${m.name} (${m.type})`).join(', ')}.`
      : `Ambiguous link${link.problem ? `: ${link.problem}` : '.'}`
    return (
      <span className="wikilink wikilink-ambiguous" title={title} data-outcome="ambiguous">
        {text}
      </span>
    )
  }

  return (
    <span className="wikilink wikilink-broken" title={`Broken link: ${link.problem ?? `nothing is called '${link.target}'.`}`} data-outcome="broken">
      {text}
    </span>
  )
}

/** Plain text with its `[[...]]` links rendered as links. */
export function LinkedText({ text, match, universe }: { text: string; match: (text: string) => LinkView | undefined; universe: string }) {
  const parts: ReactNode[] = []
  let last = 0
  for (const m of text.matchAll(WIKI_LINK)) {
    const link = match(m[0])
    if (!link) {
      continue
    }

    parts.push(text.slice(last, m.index))
    parts.push(<WikiLink key={m.index} link={link} universe={universe} />)
    last = m.index + m[0].length
  }

  parts.push(text.slice(last))
  return <>{parts}</>
}
