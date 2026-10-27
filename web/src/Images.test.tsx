// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import type { EntityPageResult, PageSection } from './api'
import { denImageUrl, imageSource } from './components/Markdown'
import { den, entities, page, renderApp, stubApi } from './test/harness'

// Images in a note (#180): one from a website doesn't load unless den.yaml
// has `remote_images: true`; an image in the den loads from /den-files (#186).

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

describe('SC-WEB-38: an image in the den shows from the den', () => {
  it('loads a path from the note’s folder, or from the den root, whatever the setting', async () => {
    for (const remoteImages of [false, true]) {
      showNote('![Map of the castle](images/castle-map.png)\n\n![Seal](/seal.png)\n\n![Coach](../shared/coach%20and%20horses.jpg)', remoteImages)
      const main = await pictures()

      // Jonathan's note is universes/dracula/jonathan-harker.md.
      expect(within(main).getByRole('img', { name: 'Map of the castle' }).getAttribute('src')).toBe('/den-files/universes/dracula/images/castle-map.png')
      expect(within(main).getByRole('img', { name: 'Seal' }).getAttribute('src')).toBe('/den-files/seal.png')
      expect(within(main).getByRole('img', { name: 'Coach' }).getAttribute('src')).toBe('/den-files/universes/shared/coach%20and%20horses.jpg')
      expect(within(main).getByRole('img', { name: 'Seal' }).getAttribute('referrerpolicy')).toBeNull()
      expect(main.querySelector('.blocked-image')).toBeNull()
      cleanup()
      vi.unstubAllGlobals()
    }
  })
})

describe('SC-WEB-39: an image the den doesn’t have', () => {
  it('shows a placeholder with the alt text when the image doesn’t load, and doesn’t ask again', async () => {
    showNote('![Map of the castle](images/castle-map.png)\n\n![](images/gone.png)')
    const main = await pictures()

    const map = within(main).getByRole('img', { name: 'Map of the castle' })
    fireEvent.error(map)
    const placeholder = (await within(main).findByText('Image not found: Map of the castle')).closest('.blocked-image') as HTMLElement
    expect(placeholder.classList.contains('missing-image')).toBe(true)
    expect(within(placeholder).getByText('images/castle-map.png')).toBeTruthy()
    expect(placeholder.getAttribute('title')).toContain('relative to the note’s folder')
    expect(within(placeholder).queryByRole('link')).toBeNull()

    fireEvent.error(main.querySelector('img[src="/den-files/universes/dracula/images/gone.png"]')!)
    await within(main).findByText('Image not found')
    expect(main.querySelectorAll('img')).toHaveLength(0)
  })

  it('doesn’t load a path that climbs above the den root', async () => {
    showNote('![Outside](../../../outside.png)\n\n![Also outside](/../outside.png)')
    const main = await pictures()

    expect(main.querySelectorAll('img')).toHaveLength(0)
    const outside = within(main).getByText('Image not found: Outside').closest('.blocked-image') as HTMLElement
    expect(outside.getAttribute('title')).toContain('above the den’s folder')
    expect(within(main).getByText('Image not found: Also outside')).toBeTruthy()
  })

  it('says a path that can’t name a file is the problem, not the den’s folder', async () => {
    showNote('![Slash](images/a%2Fb.png)')
    const main = await pictures()

    expect(main.querySelectorAll('img')).toHaveLength(0)
    const placeholder = within(main).getByText('Image not found: Slash').closest('.blocked-image') as HTMLElement
    expect(placeholder.getAttribute('title')).toContain('can’t name a file')
    expect(placeholder.getAttribute('title')).not.toContain('above the den')
  })

  it('loads a name with a bare %, which the markdown parser writes as %25', async () => {
    showNote('![Chart](images/100%.png)')
    const main = await pictures()

    expect(within(main).getByRole('img', { name: 'Chart' }).getAttribute('src')).toBe('/den-files/universes/dracula/images/100%25.png')
  })
})

describe('denImageUrl', () => {
  const note = 'universes/dracula/books/dracula/entities/characters/mina-harker.md'

  it.each([
    ['castle.png', '/den-files/universes/dracula/books/dracula/entities/characters/castle.png'],
    ['./castle.png', '/den-files/universes/dracula/books/dracula/entities/characters/castle.png'],
    ['images/castle.png', '/den-files/universes/dracula/books/dracula/entities/characters/images/castle.png'],
    ['../../images/castle.png', '/den-files/universes/dracula/books/dracula/images/castle.png'],
    ['../../../../../../images/castle.png', '/den-files/images/castle.png'],
    ['/images/castle.png', '/den-files/images/castle.png'],
    ['/images/../maps/castle.png', '/den-files/maps/castle.png'],
    ['images\\castle.png', '/den-files/universes/dracula/books/dracula/entities/characters/images/castle.png'],
    ['/castle map.png', '/den-files/castle%20map.png'],
    ['/castle%20map.png', '/den-files/castle%20map.png'],
    ['/a#b/c?.png', '/den-files/a'],
    ['/notes/the year %231893.png', '/den-files/notes/the%20year%20%231893.png'],
    ['/Café/ünïcode.png', '/den-files/Caf%C3%A9/%C3%BCn%C3%AFcode.png'],
    ['/images/castle.png?v=2#top', '/den-files/images/castle.png'],
  ])('%s', (src, url) => {
    expect(denImageUrl(src, note)).toEqual({ kind: 'den', url })
  })

  it.each([['../../../../../../../castle.png'], ['/../castle.png'], ['/images/../../castle.png']])('%s climbs above the den root, so it’s refused', (src) => {
    expect(denImageUrl(src, note)).toEqual({ kind: 'outside' })
  })

  it.each([['/'], [''], ['?v=2'], ['/a%2Fb.png'], ['images/100%.png'], ['/bad%E0%A4%A.png']])('%s can’t name a file in the den', (src) => {
    expect(denImageUrl(src, note)).toEqual({ kind: 'invalid' })
  })

  it('reads %25 as a % in a name', () => {
    expect(denImageUrl('/images/100%25.png', note)).toEqual({ kind: 'den', url: '/den-files/images/100%25.png' })
  })

  it('leaves an address with a scheme or a host as written', () => {
    expect(denImageUrl('http://127.0.0.1:9653/castle.png', note)).toEqual({ kind: 'address' })
    expect(denImageUrl('//127.0.0.1:9653/castle.png', note)).toEqual({ kind: 'address' })
  })

  it('starts from the den root for a file at the root', () => {
    expect(denImageUrl('castle.png', 'README.md')).toEqual({ kind: 'den', url: '/den-files/castle.png' })
    expect(denImageUrl('../castle.png', 'README.md')).toEqual({ kind: 'outside' })
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
