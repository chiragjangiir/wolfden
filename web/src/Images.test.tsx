// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, screen, waitFor, within } from '@testing-library/react'
import type { EntityPageResult, PageSection } from './api'
import { imageSource } from './components/Markdown'
import { den, entities, page, renderApp, stubApi } from './test/harness'

// Images in a note (#180): one from a website doesn't load unless den.yaml
// has `remote_images: true`; an image in the den is rendered as written.

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const jonathan = entities.find((e) => e.key === 'E-JONATHAN')!

function section(name: string, body: string): PageSection {
  return { name, level: 2, headingPath: [name], status: 'canon', pinned: false, from: null, until: null, line: 10, body, bookkeeping: false, links: [] }
}

function showNote(body: string, remoteImages = false) {
  return stubApi({
    den: { ...den, remoteImages },
    entityPage: (): EntityPageResult => ({
      version: 1,
      view: 'full',
      position: null,
      inView: true,
      note: null,
      entity: page(jonathan, { lead: '# Jonathan Harker\n\nA solicitor from Exeter.', sections: [section('Pictures', body)] }),
    }),
  })
}

async function pictures() {
  renderApp('/u/dracula/e/E-JONATHAN')
  const main = await screen.findByRole('main')
  await within(main).findByRole('heading', { name: 'Pictures' })
  return main
}

const remote = [
  '![Castle at dusk](https://example.com/castle.jpg)',
  '![Bistritz](http://example.org/bistritz.png "The town")',
  '![Map](//maps.example.net/carpathians.png)',
  '![Inn](https:inn.example.com/golden-krone.jpg)',
  '![][ref]\n\n[ref]: https://example.com/by-reference.jpg',
].join('\n\n')

describe('SC-WEB-35: an image from a website doesn’t load by default', () => {
  it('shows a placeholder with the alt text and the address as a link, and no image', async () => {
    showNote(remote)
    const main = await pictures()

    expect(main.querySelectorAll('img')).toHaveLength(0)
    expect(main.innerHTML).not.toMatch(/\ssrc=|srcset/)

    const castle = within(main).getByText('Image not loaded: Castle at dusk').closest('.blocked-image') as HTMLElement
    const link = within(castle).getByRole('link', { name: 'https://example.com/castle.jpg' })
    expect(link.getAttribute('href')).toBe('https://example.com/castle.jpg')
    expect(link.getAttribute('target')).toBe('_blank')
    expect(link.getAttribute('rel')).toBe('noopener noreferrer')
    expect(castle.getAttribute('title')).toContain('remote_images: true')

    expect(within(main).getByText('Image not loaded: Bistritz')).toBeTruthy()
    expect(within(main).getByRole('link', { name: 'http://example.org/bistritz.png' })).toBeTruthy()

    // A protocol-relative address, and one a browser reads as a website, are websites too.
    expect(within(main).getByRole('link', { name: '//maps.example.net/carpathians.png' }).getAttribute('href')).toBe('http://maps.example.net/carpathians.png')
    expect(within(main).getByRole('link', { name: 'https:inn.example.com/golden-krone.jpg' }).getAttribute('href')).toBe('https://inn.example.com/golden-krone.jpg')

    // No alt text: the placeholder still says what it is.
    expect(within(main).getByText('Image not loaded')).toBeTruthy()
    expect(within(main).getByRole('link', { name: 'https://example.com/by-reference.jpg' })).toBeTruthy()
  })

  it('doesn’t put a link inside a note’s link that holds the image', async () => {
    showNote('[![Cover](https://example.com/cover.jpg)](https://example.com/book)')
    const main = await pictures()

    const outer = within(main).getByRole('link', { name: /Image not loaded: Cover/ })
    expect(outer.getAttribute('href')).toBe('https://example.com/book')
    expect(outer.querySelector('a')).toBeNull()
    expect(within(outer).getByText('https://example.com/cover.jpg')).toBeTruthy()
    expect(main.querySelectorAll('img')).toHaveLength(0)
  })

  it('keeps raw HTML images and data: images out too', async () => {
    showNote('<img src="https://example.com/raw.jpg">\n\n![Pixel](data:image/png;base64,iVBORw0KGgo=)')
    const main = await pictures()

    expect(main.innerHTML).not.toContain('example.com/raw.jpg')
    expect(main.innerHTML).not.toContain('data:image')
  })
})

describe('SC-WEB-37: den.yaml lets images from websites load', () => {
  it('loads them, without sending the page as the referrer', async () => {
    showNote(remote, true)
    const main = await pictures()

    const castle = await within(main).findByRole('img', { name: 'Castle at dusk' })
    expect(castle.getAttribute('src')).toBe('https://example.com/castle.jpg')
    expect(castle.getAttribute('referrerpolicy')).toBe('no-referrer')
    expect(within(main).getByRole('img', { name: 'Bistritz' }).getAttribute('title')).toBe('The town')
    expect(main.querySelectorAll('img')).toHaveLength(5)
    expect(main.querySelector('.blocked-image')).toBeNull()
  })

  it('blocks them until the den’s setting is known', async () => {
    let answer: (value: unknown) => void = () => {}
    const pending = new Promise((resolve) => {
      answer = resolve
    })
    stubApi({
      override: (path) => (path === '/api/den' ? pending : undefined),
      entityPage: (): EntityPageResult => ({
        version: 1,
        view: 'full',
        position: null,
        inView: true,
        note: null,
        entity: page(jonathan, { sections: [section('Pictures', '![Castle at dusk](https://example.com/castle.jpg)')] }),
      }),
    })
    const main = await pictures()

    expect(within(main).getByText('Image not loaded: Castle at dusk')).toBeTruthy()
    expect(main.querySelectorAll('img')).toHaveLength(0)

    answer({ ...den, remoteImages: true })
    await waitFor(() => expect(within(main).getByRole('img', { name: 'Castle at dusk' })).toBeTruthy())
  })
})

describe('SC-WEB-37: a change to the setting while the page is open', () => {
  function stubChangingDen(first: boolean) {
    const setting = { remoteImages: first }
    stubApi({
      override: (path) => (path === '/api/den' ? { ...den, remoteImages: setting.remoteImages } : undefined),
      entityPage: (): EntityPageResult => ({
        version: 1,
        view: 'full',
        position: null,
        inView: true,
        note: null,
        entity: page(jonathan, { sections: [section('Pictures', '![Castle at dusk](https://example.com/castle.jpg)')] }),
      }),
    })
    return setting
  }

  async function open() {
    const app = renderApp('/u/dracula/e/E-JONATHAN')
    const main = await screen.findByRole('main')
    await within(main).findByRole('heading', { name: 'Pictures' })
    return { main, queryClient: app.queryClient }
  }

  it('turned on, asks for a reload rather than show an image the page’s policy blocks', async () => {
    const setting = stubChangingDen(false)
    const { main, queryClient } = await open()
    expect(within(main).getByText('Image not loaded: Castle at dusk')).toBeTruthy()

    setting.remoteImages = true
    await queryClient.refetchQueries({ queryKey: ['den'] })

    await within(main).findByText(/reload the page to load images/)
    expect(main.querySelectorAll('img')).toHaveLength(0)
    expect(within(main).getByText('Image not loaded: Castle at dusk')).toBeTruthy()
  })

  it('turned off, blocks images at once', async () => {
    const setting = stubChangingDen(true)
    const { main, queryClient } = await open()
    await within(main).findByRole('img', { name: 'Castle at dusk' })

    setting.remoteImages = false
    await queryClient.refetchQueries({ queryKey: ['den'] })

    await within(main).findByText('Image not loaded: Castle at dusk')
    expect(main.querySelectorAll('img')).toHaveLength(0)
    expect(within(main).queryByText(/reload the page/)).toBeNull()
  })
})

describe('SC-WEB-38: an image in the den is rendered as before', () => {
  it('keeps a relative address as written, whatever the setting', async () => {
    for (const remoteImages of [false, true]) {
      showNote('![Map of the castle](images/castle-map.png)\n\n![Seal](/seal.png)', remoteImages)
      const main = await pictures()

      expect(within(main).getByRole('img', { name: 'Map of the castle' }).getAttribute('src')).toBe('images/castle-map.png')
      expect(within(main).getByRole('img', { name: 'Seal' }).getAttribute('src')).toBe('/seal.png')
      expect(within(main).getByRole('img', { name: 'Seal' }).getAttribute('referrerpolicy')).toBeNull()
      expect(main.querySelector('.blocked-image')).toBeNull()
      cleanup()
      vi.unstubAllGlobals()
    }
  })
})

describe('imageSource', () => {
  const base = 'http://127.0.0.1:9653/u/dracula/e/E-JONATHAN'

  it.each([
    ['images/map.png'],
    ['./map.png'],
    ['../map.png'],
    ['/map.png'],
    ['map.png#part'],
    ['http://127.0.0.1:9653/map.png'],
  ])('%s is local', (src) => {
    expect(imageSource(src, base).kind).toBe('local')
  })

  it.each([
    ['https://example.com/a.png', 'https://example.com/a.png'],
    ['http://example.com/a.png', 'http://example.com/a.png'],
    ['HTTPS://EXAMPLE.COM/a.png', 'https://example.com/a.png'],
    ['//example.com/a.png', 'http://example.com/a.png'],
    ['\\\\example.com/a.png', 'http://example.com/a.png'],
    ['/\\example.com/a.png', 'http://example.com/a.png'],
    ['http://localhost:9653/a.png', 'http://localhost:9653/a.png'],
    ['http://127.0.0.1:5173/a.png', 'http://127.0.0.1:5173/a.png'],
  ])('%s is remote', (src, href) => {
    const source = imageSource(src, base)
    expect(source.kind).toBe('remote')
    expect(source.kind === 'remote' && source.url?.href).toBe(href)
  })

  it('counts an address that doesn’t parse as remote', () => {
    expect(imageSource('http://[not-a-host/a.png', base)).toEqual({ kind: 'remote', url: null })
  })
})
