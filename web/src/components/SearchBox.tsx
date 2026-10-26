import { useEffect, useRef, useState } from 'react'
import { useMatch, useNavigate, useRouterState } from '@tanstack/react-router'
import type { SearchPageSearch } from '../router'

/** How long typing pauses before the search runs, in milliseconds. */
export const SEARCH_DEBOUNCE_MS = 300

/** The search page's path, `/u/<universe>/search`. */
const SEARCH_PATH = /^\/u\/[^/]+\/search\/?$/

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
  const location = useRouterState({ select: (s) => s.location })
  const [text, setText] = useState(q ?? '')
  const input = useRef<HTMLInputElement>(null)
  // The pending search while typing pauses, if any.
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  // The words this box last sent, so the URL catching up doesn't overwrite newer typing.
  const sent = useRef(q)
  // What a pending search reads when it runs: this render's, not the one it was typed in.
  const current = useRef({ universe, onSearchPage })
  current.current = { universe, onSearchPage }

  const cancel = () => {
    clearTimeout(timer.current)
    timer.current = undefined
  }

  const go = (words: string) => {
    cancel()
    const { universe: u, onSearchPage: on } = current.current
    if (!words && !on) {
      return
    }

    sent.current = words || undefined
    void navigate({
      to: '/u/$universe/search',
      params: { universe: u },
      search: (prev: SearchPageSearch) => (on ? { ...prev, q: words || undefined } : { at: prev.at, q: words }),
      // Typing on the search page refines it rather than adding to history.
      replace: on,
    })
  }

  // Going to another page (a link, Back) drops a search still waiting to
  // run, and so does a change of the search page's words from elsewhere.
  // Staying on the page keeps it: a filter or the position changed beside
  // it, which it reads from the URL when it runs. So does this box's own
  // search. It's decided from the location itself, since the route match
  // can lag the address by a render.
  const previousPath = useRef(location.pathname)
  useEffect(() => {
    const searchPath = SEARCH_PATH.test(location.pathname)
    const rawQ = (location.search as Record<string, unknown>).q
    const words = (typeof rawQ === 'string' ? rawQ.trim() : '') || undefined
    const samePage = location.pathname === previousPath.current
    previousPath.current = location.pathname
    const keep = searchPath ? words === sent.current : samePage
    if (!keep) {
      cancel()
    }
  }, [location])

  // The URL's words changed from elsewhere (Back, a link): show them. Leaving
  // the search page clears the box.
  useEffect(() => {
    if (!onSearchPage) {
      sent.current = undefined
      setText('')
    } else if (q !== sent.current) {
      sent.current = q
      setText(q ?? '')
    }
  }, [q, onSearchPage])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey && !isTextEntry(e.target)) {
        e.preventDefault()
        input.current?.focus()
        input.current?.select()
      }
    }

    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('keydown', onKey)
      cancel()
    }
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
          const value = e.currentTarget.value
          setText(value)
          cancel()
          timer.current = setTimeout(() => go(value.trim()), SEARCH_DEBOUNCE_MS)
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
