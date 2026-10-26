import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, Outlet, useRouterState } from '@tanstack/react-router'
import { denQuery } from '../api'
import { useUniverseKey } from '../hooks'
import { PositionBar } from './PositionBar'
import { SearchBox } from './SearchBox'
import { Sidebar } from './Sidebar'

/**
 * Every page: the den's name and the story-position bar across the top, the
 * hierarchy tree on the left, and the page on the right (#24, #26). On a
 * narrow screen the tree folds away behind a Contents button and opens over
 * the page, below the top bar: focus moves into it, and Escape closes it and
 * returns focus to the button.
 */
export function Layout() {
  const den = useQuery(denQuery)
  const universe = useUniverseKey()
  const [treeOpen, setTreeOpen] = useState(false)
  const pathname = useRouterState({ select: (s) => s.location.pathname })
  const appRef = useRef<HTMLDivElement>(null)
  const headerRef = useRef<HTMLElement>(null)
  const sidebarRef = useRef<HTMLElement>(null)
  const toggleRef = useRef<HTMLButtonElement>(null)

  // On a narrow screen, going to a page closes the tree.
  useEffect(() => setTreeOpen(false), [pathname])

  // The top bar wraps on a narrow screen, so the tree is placed from its
  // measured height rather than a fixed one.
  useLayoutEffect(() => {
    const header = headerRef.current
    const app = appRef.current
    if (!header || !app) {
      return
    }

    const measure = () => app.style.setProperty('--header-height', `${Math.ceil(header.getBoundingClientRect().height)}px`)
    measure()
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure)
    observer?.observe(header)
    window.addEventListener('resize', measure)
    return () => {
      observer?.disconnect()
      window.removeEventListener('resize', measure)
    }
  }, [])

  useEffect(() => {
    if (!treeOpen) {
      return
    }

    sidebarRef.current?.focus()
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setTreeOpen(false)
        toggleRef.current?.focus()
      }
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [treeOpen])

  return (
    <div className="app" ref={appRef}>
      <header className="topbar" ref={headerRef}>
        <button
          type="button"
          ref={toggleRef}
          className="tree-toggle"
          aria-expanded={treeOpen}
          aria-controls="sidebar"
          onClick={() => setTreeOpen(!treeOpen)}
        >
          Contents
        </button>
        <Link to="/" className="den-name">
          {den.data?.name ?? 'Wolfden'}
        </Link>
        <SearchBox universe={universe} />
        <PositionBar universe={universe} />
      </header>
      <div className="body">
        <aside id="sidebar" className="sidebar" data-open={treeOpen} ref={sidebarRef} tabIndex={-1}>
          <Sidebar universe={universe} />
        </aside>
        <main className="page">
          <Outlet />
        </main>
      </div>
    </div>
  )
}
