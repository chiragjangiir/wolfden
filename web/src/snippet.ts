// A search hit's snippet, split into plain text and the matches to mark.
//
// The API marks each match in square brackets (`[blood]`), but a note's own
// text can hold brackets too, such as a `[[...]]` link. So a bracketed run
// counts as a match only when its words are the query's words (ignoring case
// and accents, as the search does, with `word*` as a prefix); any other
// bracket stays as it was written. The parts are rendered as text, never as
// HTML.

export interface SnippetPart {
  text: string
  match: boolean
}

/** Lower case, without accents: how the search compares words. */
function fold(text: string): string {
  return text.normalize('NFD').replace(/\p{M}+/gu, '').toLowerCase()
}

const WORD = /[\p{L}\p{N}]+/gu

/** The query's words, folded, each whole or (for `word*`) a prefix. */
export function queryWords(query: string): { whole: Set<string>; prefixes: string[] } {
  const whole = new Set<string>()
  const prefixes: string[] = []
  for (const term of query.split(/\s+/)) {
    const prefix = term.endsWith('*')
    const words = fold(term).match(WORD) ?? []
    words.forEach((word, i) => (prefix && i === words.length - 1 ? prefixes.push(word) : whole.add(word)))
  }

  return { whole, prefixes }
}

/** Splits `snippet` into its text and the query's matches the API bracketed. */
export function snippetParts(snippet: string, query: string): SnippetPart[] {
  const { whole, prefixes } = queryWords(query)
  const isMatch = (inside: string) => {
    const words = fold(inside).match(WORD) ?? []
    return words.length > 0 && words.every((w) => whole.has(w) || prefixes.some((p) => w.startsWith(p)))
  }

  const parts: SnippetPart[] = []
  const push = (text: string, match: boolean) => {
    if (!text) {
      return
    }

    const last = parts[parts.length - 1]
    if (last && !last.match && !match) {
      last.text += text
    } else {
      parts.push({ text, match })
    }
  }

  let rest = 0
  for (const m of snippet.matchAll(/\[([^[\]]+)\]/g)) {
    if (!isMatch(m[1])) {
      continue
    }

    push(snippet.slice(rest, m.index), false)
    push(m[1], true)
    rest = m.index + m[0].length
  }

  push(snippet.slice(rest), false)
  return parts
}
