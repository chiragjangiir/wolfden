import { Link } from '@tanstack/react-router'

/**
 * The tabs of an entity's page: the note itself, and its history (#27). The
 * current one isn't a link. Both keep the story position (`at`), which
 * carries from page to page.
 */
export function EntityTabs({ universe, entityKey, current }: { universe: string; entityKey: string; current: 'note' | 'history' }) {
  return (
    <nav className="entity-tabs" aria-label="Note views">
      {current === 'note' ? (
        <span aria-current="page">Note</span>
      ) : (
        <Link to="/u/$universe/e/$" params={{ universe, _splat: entityKey }} search={{}}>
          Note
        </Link>
      )}
      {current === 'history' ? (
        <span aria-current="page">History</span>
      ) : (
        <Link to="/u/$universe/e/$" params={{ universe, _splat: entityKey }} search={{ view: 'history' }}>
          History
        </Link>
      )}
    </nav>
  )
}
