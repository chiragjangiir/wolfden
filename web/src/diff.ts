// A unified diff, as git writes it, parsed into lines for the history page
// (#27). The patch comes from /api/history/diff: one file's part of `git diff
// --patch`, with its `diff --git` header, any extended headers, and hunks.
// Lines are split on `\n` only, so a `\r` (a note saved with CRLF line
// endings, or a lone one) stays in its line, as git wrote it.

export type DiffLineKind = 'context' | 'added' | 'removed'

/** A line of a hunk: its text without the leading marker, and its numbers in the old and new file. */
export interface DiffLine {
  kind: DiffLineKind
  text: string
  oldNumber: number | null
  newNumber: number | null
  /** True when "\ No newline at end of file" follows this line. */
  noNewline: boolean
}

export interface DiffHunk {
  /** The `@@ -a,b +c,d @@` line, as written, with any function or heading git adds. */
  header: string
  oldStart: number
  newStart: number
  lines: DiffLine[]
}

/**
 * One part of a patch: its header lines (`diff --git`, `index`, `---`,
 * `+++`, `rename from`, `Binary files … differ`) and its hunks. A type change
 * (a file replaced by a link) has two parts under one `diff --git` line.
 */
export interface DiffPart {
  headers: string[]
  hunks: DiffHunk[]
}

const HUNK = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/

/** Parses a unified diff into parts and hunks. Text it can't place is kept as a header line, never dropped. */
export function parsePatch(patch: string): DiffPart[] {
  const parts: DiffPart[] = []
  let part: DiffPart | null = null
  let hunk: DiffHunk | null = null
  let oldLeft = 0
  let newLeft = 0
  let oldLine = 0
  let newLine = 0

  const lines = patch.split('\n')
  // The patch ends with a line break; that isn't an empty last line.
  if (lines.length > 0 && lines[lines.length - 1] === '') {
    lines.pop()
  }

  for (const line of lines) {
    const inHunk = hunk !== null && (oldLeft > 0 || newLeft > 0)
    if (inHunk && hunk) {
      const marker = line.charAt(0)
      // An empty line in a hunk is an empty context line whose space was lost.
      if (marker === ' ' || line === '') {
        hunk.lines.push({ kind: 'context', text: line.slice(1), oldNumber: oldLine++, newNumber: newLine++, noNewline: false })
        oldLeft--
        newLeft--
        continue
      }

      if (marker === '-') {
        hunk.lines.push({ kind: 'removed', text: line.slice(1), oldNumber: oldLine++, newNumber: null, noNewline: false })
        oldLeft--
        continue
      }

      if (marker === '+') {
        hunk.lines.push({ kind: 'added', text: line.slice(1), oldNumber: null, newNumber: newLine++, noNewline: false })
        newLeft--
        continue
      }

      if (marker === '\\') {
        markNoNewline(hunk)
        continue
      }
    }

    // "\ No newline at end of file" after a hunk's last line.
    if (line.startsWith('\\') && hunk && hunk.lines.length > 0) {
      markNoNewline(hunk)
      continue
    }

    const header = HUNK.exec(line)
    if (header) {
      if (!part) {
        part = { headers: [], hunks: [] }
        parts.push(part)
      }

      oldLine = Number(header[1])
      newLine = Number(header[3])
      oldLeft = header[2] === undefined ? 1 : Number(header[2])
      newLeft = header[4] === undefined ? 1 : Number(header[4])
      hunk = { header: line, oldStart: oldLine, newStart: newLine, lines: [] }
      part.hunks.push(hunk)
      continue
    }

    // A header line: a new part starts at `diff --git`, or at a header after hunks.
    if (!part || line.startsWith('diff --git ') || part.hunks.length > 0) {
      part = { headers: [], hunks: [] }
      parts.push(part)
    }

    hunk = null
    part.headers.push(line)
  }

  return parts
}

function markNoNewline(hunk: DiffHunk) {
  const last = hunk.lines[hunk.lines.length - 1]
  if (last) {
    last.noNewline = true
  }
}

/** A piece of a changed line, marked when it's what changed. */
export interface Segment {
  text: string
  changed: boolean
}

// Words, runs of spaces, and single other characters.
const TOKEN = /[\p{L}\p{N}_]+|\s+|[^\p{L}\p{N}_\s]/gu

/** The most tokens a pair of lines may have between them for a word-level highlight (it's quadratic). */
const WORD_DIFF_LIMIT = 400

/**
 * Which words changed between a removed line and the added line that
 * replaced it, by the longest common run of tokens. Null when the lines are
 * too long to compare cheaply, or have nothing in common, so the whole lines
 * are marked instead.
 */
export function wordDiff(before: string, after: string): { removed: Segment[]; added: Segment[] } | null {
  const a = before.match(TOKEN) ?? []
  const b = after.match(TOKEN) ?? []
  if (a.length + b.length > WORD_DIFF_LIMIT) {
    return null
  }

  // lengths[i][j]: the longest common subsequence of a[i..] and b[j..].
  const lengths: number[][] = Array.from({ length: a.length + 1 }, () => new Array<number>(b.length + 1).fill(0))
  for (let i = a.length - 1; i >= 0; i--) {
    for (let j = b.length - 1; j >= 0; j--) {
      lengths[i][j] = a[i] === b[j] ? lengths[i + 1][j + 1] + 1 : Math.max(lengths[i + 1][j], lengths[i][j + 1])
    }
  }

  const common = lengths[0][0]
  // Only whitespace in common isn't worth highlighting word by word.
  const sharedText = (() => {
    let n = 0
    let i = 0
    let j = 0
    while (i < a.length && j < b.length) {
      if (a[i] === b[j]) {
        if (a[i].trim()) n++
        i++
        j++
      } else if (lengths[i + 1][j] >= lengths[i][j + 1]) {
        i++
      } else {
        j++
      }
    }
    return n
  })()
  if (common === 0 || sharedText === 0) {
    return null
  }

  const removed: Segment[] = []
  const added: Segment[] = []
  const push = (into: Segment[], text: string, changed: boolean) => {
    const last = into[into.length - 1]
    if (last && last.changed === changed) {
      last.text += text
    } else {
      into.push({ text, changed })
    }
  }

  let i = 0
  let j = 0
  while (i < a.length || j < b.length) {
    if (i < a.length && j < b.length && a[i] === b[j]) {
      push(removed, a[i++], false)
      push(added, b[j++], false)
    } else if (j >= b.length || (i < a.length && lengths[i + 1][j] >= lengths[i][j + 1])) {
      push(removed, a[i++], true)
    } else {
      push(added, b[j++], true)
    }
  }

  return { removed, added }
}

/**
 * The word-level highlight of each changed line in a hunk: a run of removed
 * lines followed by a run of added lines of the same length is paired line
 * by line. Lines without a pair are marked whole.
 */
export function pairChanges(lines: readonly DiffLine[]): Map<DiffLine, Segment[]> {
  const segments = new Map<DiffLine, Segment[]>()
  let k = 0
  while (k < lines.length) {
    if (lines[k].kind !== 'removed') {
      k++
      continue
    }

    const start = k
    while (k < lines.length && lines[k].kind === 'removed') k++
    const middle = k
    while (k < lines.length && lines[k].kind === 'added') k++
    const removedRun = lines.slice(start, middle)
    const addedRun = lines.slice(middle, k)
    if (removedRun.length !== addedRun.length) {
      continue
    }

    removedRun.forEach((r, n) => {
      const pair = wordDiff(r.text, addedRun[n].text)
      if (pair) {
        segments.set(r, pair.removed)
        segments.set(addedRun[n], pair.added)
      }
    })
  }

  return segments
}
