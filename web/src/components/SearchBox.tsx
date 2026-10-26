import { useEffect, useRef, useState } from 'react'
import { useMatch, useNavigate } from '@tanstack/react-router'
import type { SearchPageSearch, StorySearch } from '../router'

/** How long typing pauses before the search runs, in milliseconds. */
export const SEARCH_DEBOUNCE_MS = 300

/**
 * True if a key pressed in `target` types into it, so `/` there is a
 * character rather than the shortcut.
 */
export function isTextEntry(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) {
    return false
  }

  if (target.isContentEditable || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT') {
    return true
  }

  return target instanceof HTMLInputElement && !['range', 'checkbox', 'radio', 'button', 'submit', 'reset', 'color', 'file'].includes(target.type)
}

/**
 * The search box in the top bar (#25). Typing searches once it pauses, and
 * Enter searches at once, on the search page (`/u/<universe>/search?q=`),
 * keeping the story position. `/` anywhere outside a text box focuses it.
 */
export function SearchBox({ universe }: { universe: string | undefined }) {
  if (!universe) {
    return null
  }

  return <UniverseSearchBox universe={universe} />
}

function UniverseSearchBox({ universe }: { universe: string }) {
  const navigate = useNavigate()
  const match = useMatch({ from: '/u/$universe/search', shouldThrow: false })
  const onSearchPage = match !== undefined
  const q = onSearchPage ? (match.search as SearchPageSearch).q : undefined
  const [text, setText] = useState(q ?? '')
  const input = useRef<HTMLInputElement>(null)
  // Set while the writer has typed something not yet searched for.
  const typed = useRef(false)
  // The words this box last sent, so the URL catching up doesn't overwrite newer typing.
  const sent = useRef(q)

  // The URL's words changed from elsewhere (Back, a link): show them.
  useEffect(() => {
    if (onSearchPage && q !== sent.current) {
      sent.current = q
      typed.current = false
      setText(q ?? '')
    }
  }, [q, onSearchPage])

  const go = (words: string) => {
    typed.current = false
    if (!words && !onSearchPage) {
      return
    }

    sent.current = words || undefined
    void navigate({
      to: '/u/$universe/search',
      params: { universe },
      search: (prev: SearchPageSearch & StorySearch) => (onSearchPage ? { ...prev, q: words || undefined } : { at: prev.at, q: words }),
      // Typing on the search page refines it rather than adding to history.
      replace: onSearchPage,
    })
  }

  useEffect(() => {
    if (!typed.current) {
      return
    }

    const timer = setTimeout(() => go(text.trim()), SEARCH_DEBOUNCE_MS)
    return () => clearTimeout(timer)
    // `go` is made anew each render; only the text starts the wait.
  }, [text])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey && !isTextEntry(e.target)) {
        e.preventDefault()
        input.current?.focus()
        input.current?.select()
      }
    }

    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [])

  return (
    <form
      className="search-box"
      role="search"
      onSubmit={(e) => {
        e.preventDefault()
        go(text.trim())
      }}
    >
      <input
        ref={input}
        type="search"
        aria-label="Search notes"
        aria-keyshortcuts="/"
        placeholder="Search  /"
        value={text}
        onChange={(e) => {
          typed.current = true
          setText(e.currentTarget.value)
        }}
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            e.currentTarget.blur()
          }
        }}
      />
    </form>
  )
}
