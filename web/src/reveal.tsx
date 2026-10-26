import { createContext, useContext, useState, type ReactNode } from 'react'

/**
 * "Show it anyway" (maintainer decision, #26), one per note: revealing a
 * note the reader hasn't met at the position reveals it on both its Note
 * and History tabs. The reveal belongs to the note in that universe at that
 * position, so leaving the note (even to come Back), a change of position,
 * or a reload hides it again.
 */
interface Reveal {
  revealed: boolean
  reveal: () => void
}

const RevealContext = createContext<Reveal>({ revealed: false, reveal: () => {} })

export function useReveal(): Reveal {
  return useContext(RevealContext)
}

/**
 * Holds the reveal for the note at `id` (its universe, key, and position).
 * When `id` changes, the reveal is reset at once, in the same render, so
 * nothing of the next note shows for a moment.
 */
export function RevealProvider({ id, children }: { id: string; children: ReactNode }) {
  const [state, setState] = useState({ id, revealed: false })
  let current = state
  if (state.id !== id) {
    current = { id, revealed: false }
    setState(current)
  }

  const value: Reveal = { revealed: current.revealed, reveal: () => setState({ id, revealed: true }) }
  return <RevealContext.Provider value={value}>{children}</RevealContext.Provider>
}
