/**
 * The search params of an entity's page beyond `at`: `view=history` for its
 * history (#27), and which diff that shows. `checkpoint` is one
 * checkpoint's own change; `from` and `to` compare two (`from` alone is the
 * change since it up to now); none of them is the uncommitted changes, or the
 * latest checkpoint when there are none. Ids are kept as written; the API
 * checks them.
 */
export interface EntitySearch {
  view?: 'history'
  checkpoint?: string
  from?: string
  to?: string
}

function text(value: unknown): string | undefined {
  const t = typeof value === 'string' ? value.trim() : ''
  return t || undefined
}

export function validateEntitySearch(search: Record<string, unknown>): EntitySearch {
  if (search.view !== 'history') {
    return {}
  }

  const checkpoint = text(search.checkpoint)
  const from = text(search.from)
  const to = from ? text(search.to) : undefined
  if (from) {
    return to ? { view: 'history', from, to } : { view: 'history', from }
  }

  return checkpoint ? { view: 'history', checkpoint } : { view: 'history' }
}
