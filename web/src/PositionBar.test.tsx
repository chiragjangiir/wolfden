// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { atOf, renderApp, stubApi } from './test/harness'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

async function slider() {
  return (await screen.findByRole('slider', { name: 'Story position' })) as HTMLInputElement
}

describe('SC-WEB-11: the story-position bar', () => {
  it('starts at everything: no position in the URL or the API calls, and no reset', async () => {
    const { calls } = stubApi()
    renderApp('/u/dracula')

    const range = await slider()
    expect(range.value).toBe('6')
    expect(range.max).toBe('6')
    expect(range.getAttribute('aria-valuetext')).toBe('Everything (the end of the story)')
    expect(screen.queryByRole('button', { name: 'Show everything' })).toBeNull()
    await waitFor(() => expect(calls).toContain('/api/hierarchy?universe=dracula'))
    expect(calls.some((c) => c.includes('asOf'))).toBe(false)
  })

  it('reads the position from the URL and asks the API as of it', async () => {
    const { calls } = stubApi()
    renderApp('/u/dracula?at=dracula:3')

    const range = await slider()
    expect(range.value).toBe('2')
    expect(range.getAttribute('aria-valuetext')).toBe("Dracula, chapter 3: Jonathan Harker's Journal")
    expect((screen.getByRole('combobox', { name: 'Chapter' }) as HTMLSelectElement).value).toBe('dracula:3')
    await waitFor(() => expect(calls).toContain('/api/hierarchy?universe=dracula&asOf=dracula%3A3'))
  })

  it('steps chapter by chapter with the arrow keys and the buttons, and past the last chapter to everything', async () => {
    stubApi()
    const { history } = renderApp('/u/dracula?at=dracula:3')
    const range = await slider()

    fireEvent.keyDown(range, { key: 'ArrowRight' })
    await waitFor(() => expect(atOf(history)).toBe('dracula:4'))
    fireEvent.keyDown(range, { key: 'ArrowLeft' })
    await waitFor(() => expect(atOf(history)).toBe('dracula:3'))
    fireEvent.click(screen.getByRole('button', { name: 'Previous chapter' }))
    await waitFor(() => expect(atOf(history)).toBe('dracula:2'))

    fireEvent.keyDown(range, { key: 'End' })
    await waitFor(() => expect(atOf(history)).toBeNull())
    fireEvent.keyDown(range, { key: 'ArrowLeft' })
    await waitFor(() => expect(atOf(history)).toBe('dracula:note'))
    fireEvent.click(screen.getByRole('button', { name: 'Next chapter' }))
    await waitFor(() => expect(atOf(history)).toBeNull())
    expect(history.location.search).toBe('')
  })

  it('keeps the position readable in the URL', async () => {
    stubApi()
    const { history } = renderApp('/u/dracula')

    fireEvent.change(await slider(), { target: { value: '4' } })
    await waitFor(() => expect(history.location.search).toBe('?at=dracula:5'))
  })

  it('jumps with the book and chapter menus, and the reset shows everything again', async () => {
    stubApi()
    const { history } = renderApp('/u/dracula')
    await slider()

    fireEvent.change(screen.getByRole('combobox', { name: 'Book' }), { target: { value: 'dracula' } })
    await waitFor(() => expect(atOf(history)).toBe('dracula:1'))
    fireEvent.change(screen.getByRole('combobox', { name: 'Chapter' }), { target: { value: 'dracula:note' } })
    await waitFor(() => expect(atOf(history)).toBe('dracula:note'))

    fireEvent.click(screen.getByRole('button', { name: 'Show everything' }))
    await waitFor(() => expect(atOf(history)).toBeNull())
  })

  it('carries the position from page to page', async () => {
    stubApi()
    const { history } = renderApp('/u/dracula/b/dracula?at=dracula:3')

    const page = await screen.findByRole('main')
    fireEvent.click(await within(page).findByRole('link', { name: /Letters—Lucy and Mina/ }))
    await waitFor(() => expect(history.location.pathname).toBe('/u/dracula/b/dracula/c/5'))
    expect(atOf(history)).toBe('dracula:3')
  })

  it('offers to show everything for a position the story doesn’t have', async () => {
    stubApi({
      override: (_, params) =>
        params.get('asOf') === 'dracula:99'
          ? new Response(JSON.stringify({ code: 'bad_position', message: "'dracula:99' isn't a position." }), { status: 400 })
          : undefined,
    })
    const { history } = renderApp('/u/dracula?at=dracula:99')

    const alert = await within(await screen.findByRole('main')).findByRole('alert')
    expect(alert.textContent).toContain("'dracula:99' isn't a position.")
    fireEvent.click(within(alert).getByRole('button', { name: 'Show everything' }))
    await waitFor(() => expect(atOf(history)).toBeNull())
  })
})
