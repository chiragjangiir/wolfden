import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, Outlet, useRouterState } from '@tanstack/react-router'
import { denQuery } from '../api'
import { useUniverseKey } from '../hooks'
import { PositionBar } from './PositionBar'
import { Sidebar } from './Sidebar'

/**
 * Every page: the den's name and the story-position bar across the top, the
 * hierarchy tree on the left, and the page on the right (#24, #26). On a
 * narrow screen the tree folds away behind a Contents button.
 */
export function Layout() {
  const den = useQuery(denQuery)
  const universe = useUniverseKey()
  const [treeOpen, setTreeOpen] = useState(false)
  const pathname = useRouterState({ select: (s) => s.location.pathname })

  // On a narrow screen, going to a page closes the tree.
  useEffect(() => setTreeOpen(false), [pathname])

  return (
    <div className="app">
      <header className="topbar">
        <button type="button" className="tree-toggle" aria-expanded={treeOpen} aria-controls="sidebar" onClick={() => setTreeOpen(!treeOpen)}>
          Contents
        </button>
        <Link to="/" className="den-name">
          {den.data?.name ?? 'Wolfden'}
        </Link>
        <PositionBar universe={universe} />
      </header>
      <div className="body">
        <aside id="sidebar" className="sidebar" data-open={treeOpen}>
          <Sidebar universe={universe} />
        </aside>
        <main className="page">
          <Outlet />
        </main>
      </div>
    </div>
  )
}
