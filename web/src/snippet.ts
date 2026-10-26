// A search hit's snippet, split into plain text and the matches to mark.
//
// The API gives each hit its excerpt as plain `text` and the matches as
// ranges in it (#179), so a note's own brackets, such as a `[[...]]` link,
// can never pass for a match. The parts are rendered as text, never as HTML.

export interface SnippetPart {
  text: string
  match: boolean
}

/** A match: where it starts in the text, and how long it is (UTF-16 code units, as JavaScript indexes strings). */
export interface TextMatch {
  start: number
  length: number
}

/** Splits `text` into its plain parts and its matches. Ranges out of order, overlapping, or past the end are clipped or skipped. */
export function snippetParts(text: string, matches: readonly TextMatch[]): SnippetPart[] {
  const parts: SnippetPart[] = []
  const push = (part: string, match: boolean) => {
    if (part) {
      parts.push({ text: part, match })
    }
  }

  let rest = 0
  for (const { start, length } of [...matches].sort((a, b) => a.start - b.start)) {
    const from = Math.max(start, rest)
    const to = Math.min(start + length, text.length)
    if (to <= from) {
      continue
    }

    push(text.slice(rest, from), false)
    push(text.slice(from, to), true)
    rest = to
  }

  push(text.slice(rest), false)
  return parts
}
