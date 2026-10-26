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

/**
 * The router lays a route's validated search over the raw query string, so
 * every key is returned, undefined when it's dropped; a key left out would
 * keep its raw value (as for the search page, #177).
 */
export function validateEntitySearch(search: Record<string, unknown>): EntitySearch {
  const none: EntitySearch = { view: undefined, checkpoint: undefined, from: undefined, to: undefined }
  if (search.view !== 'history') {
    return none
  }

  const from = text(search.from)
  if (from) {
    return { ...none, view: 'history', from, to: text(search.to) }
  }

  return { ...none, view: 'history', checkpoint: text(search.checkpoint) }
}
