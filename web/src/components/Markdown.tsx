import { createContext, useContext, useMemo, useState, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import ReactMarkdown, { defaultUrlTransform, type Components } from 'react-markdown'
import { denQuery, remoteImagesAtLoad, type LinkView } from '../api'
import { WIKI_LINK, WikiLink, createLinkMatcher } from './Links'

// Notes' markdown, rendered safely. react-markdown builds React elements
// (never HTML strings), `skipHtml` drops any raw HTML in a note, and its URL
// check drops `javascript:` and other unsafe links. `[[...]]` links become
// in-app links through the API's resolved `links`. An image from a website
// doesn't load unless den.yaml has `remote_images: true` (#180), so opening
// a note doesn't tell that website the den was opened; the page's
// Content-Security-Policy blocks it too, as a second line of defence. An
// image kept in the den loads from the server's /den-files/, from a path
// relative to the note's own file (#186).

// The scheme of the links the plugin makes. It's random for each page load,
// so a link written in a note can't pass for one.
const WIKI_PROTOCOL = `wolfden-${Array.from(crypto.getRandomValues(new Uint8Array(12)), (b) => b.toString(16).padStart(2, '0')).join('')}:`

interface MdNode {
  type: string
  value?: string
  url?: string
  children?: MdNode[]
}

/** A remark plugin: turns each `[[...]]` in text into a link to the index of its resolution in `resolved`. */
function wikiLinks(links: readonly LinkView[], resolved: LinkView[]) {
  const rewrite = (node: MdNode, match: (text: string) => LinkView | undefined) => {
    if (!node.children || node.type === 'link' || node.type === 'linkReference') {
      return
    }

    const children: MdNode[] = []
    for (const child of node.children) {
      if (child.type !== 'text' || !child.value?.includes('[[')) {
        rewrite(child, match)
        children.push(child)
        continue
      }

      const text = child.value
      let last = 0
      for (const m of text.matchAll(WIKI_LINK)) {
        const link = match(m[0])
        if (!link) {
          continue
        }

        children.push({ type: 'text', value: text.slice(last, m.index) })
        resolved.push(link)
        children.push({ type: 'link', url: `${WIKI_PROTOCOL}${resolved.length - 1}`, children: [{ type: 'text', value: m[0] }] })
        last = m.index + m[0].length
      }

      children.push({ type: 'text', value: text.slice(last) })
    }

    node.children = children
  }

  // Each run starts afresh, so a re-render matches the links in the same order.
  return () => (tree: MdNode) => {
    resolved.length = 0
    rewrite(tree, createLinkMatcher(links))
  }
}

/** Keeps the plugin's own links (as an `href` only); every other URL gets react-markdown's check. */
function urlTransform(url: string, key: string): string {
  return key === 'href' && url.startsWith(WIKI_PROTOCOL) ? url : defaultUrlTransform(url)
}

/**
 * Where an image's address leads: `local` if it resolves to this page's own
 * server (a relative address, or one on this origin), or `remote`, with the
 * full address, if it's on another site (`https:`, `http:`, `//host/...`,
 * or a trick such as `\\host/...` that a browser reads as one). An address
 * that doesn't parse is `remote` without one, so it never loads.
 */
export function imageSource(src: string, base: string = window.location.href): { kind: 'local' } | { kind: 'remote'; url: URL | null } {
  let url: URL
  try {
    url = new URL(src, base)
  } catch {
    return { kind: 'remote', url: null }
  }

  return url.origin === new URL(base).origin ? { kind: 'local' } : { kind: 'remote', url }
}

/** Where `wolfden serve` serves the den's own images (DenFiles.Prefix). */
export const DEN_FILES = '/den-files/'

/** Where an image's local `src` leads (format.md F-LAY-9, #186): see {@link denImageUrl}. */
export type DenImageSource = { kind: 'den'; url: string } | { kind: 'outside' } | { kind: 'invalid' } | { kind: 'address' }

/**
 * The server address of an image kept in the den (format.md F-LAY-9, #186),
 * for a local `src` written in the den file at den-relative `file`:
 * relative to that file's folder, or to the den root if it starts with `/`.
 * Each name is decoded as written (`%20` is a space) and encoded again for
 * the address; `?query` and `#fragment` are dropped, as a file has neither.
 * The answer is `den`, with the address; `outside` for a path whose `../`
 * climbs above the den root, which is refused and never loads; `invalid`
 * for one that can't name a file in the den (empty, a `%` that isn't an
 * escape, as in `100%.png`, or a name holding an encoded `/`); or
 * `address` for a `src` with a scheme or a host (an address on this
 * server, such as `http://127.0.0.1:9653/x.png`), which isn't a den path
 * and is rendered as written.
 */
export function denImageUrl(src: string, file: string): DenImageSource {
  // `\` reads as `/`, as browsers read it in an address.
  const path = src.replace(/\\/g, '/').replace(/[?#].*$/s, '')
  if (/^[a-z][a-z\d+.-]*:/i.test(path) || path.startsWith('//')) {
    return { kind: 'address' }
  }

  if (path === '') {
    return { kind: 'invalid' }
  }

  const names = path.startsWith('/') ? [] : file.split('/').slice(0, -1)
  for (const written of path.split('/')) {
    let name: string
    try {
      name = decodeURIComponent(written)
    } catch {
      return { kind: 'invalid' }
    }

    if (name === '' || name === '.') {
      continue
    }

    if (name === '..') {
      if (names.length === 0) {
        return { kind: 'outside' }
      }

      names.pop()
      continue
    }

    if (name.includes('/') || name.includes('\\')) {
      return { kind: 'invalid' }
    }

    names.push(name)
  }

  return names.length === 0 ? { kind: 'invalid' } : { kind: 'den', url: DEN_FILES + names.map(encodeURIComponent).join('/') }
}

/** True inside a note's link, where a placeholder mustn't hold a link of its own. */
const InLink = createContext(false)

/** What shows in place of an image from a website that doesn't load. */
function BlockedImage({ src, alt, url, reload }: { src: string; alt: string | undefined; url: URL | null; reload: boolean }) {
  const inLink = useContext(InLink)
  const label = alt?.trim() ? `Image not loaded: ${alt.trim()}` : 'Image not loaded'
  const web = url !== null && (url.protocol === 'https:' || url.protocol === 'http:')
  const title = reload
    ? 'den.yaml now allows images from websites; reload the page to load them.'
    : "Images from websites don't load unless den.yaml has remote_images: true."
  return (
    <span className="blocked-image" title={title}>
      <span className="blocked-image-label">{label}</span>{' '}
      {web && !inLink ? (
        <a href={url.href} target="_blank" rel="noopener noreferrer">
          {src}
        </a>
      ) : (
        <span className="blocked-image-address">{src}</span>
      )}
      {reload && <span className="blocked-image-hint"> (reload the page to load images)</span>}
    </span>
  )
}

/**
 * Whether a note's images from websites load. The page's
 * Content-Security-Policy was fixed when it loaded, so they load only if
 * the den allowed them in its first answer and still does: turning the
 * setting off blocks them at once, and turning it on asks for a reload.
 */
function useRemoteImages(): { allowed: boolean; reload: boolean } {
  const client = useQueryClient()
  // Until the den's setting is known, images from websites stay blocked.
  const live = useQuery(denQuery).data?.remoteImages === true
  const atLoad = remoteImagesAtLoad(client) === true
  return { allowed: live && atLoad, reload: live && !atLoad }
}

/** What shows in place of an image kept in the den that isn't there, or that the server won't serve. */
const MISSING_TITLES = {
  missing:
    'No image at this path in the den, or it isn’t a kind Wolfden shows (png, jpg, gif, webp, avif, svg). Paths are relative to the note’s folder; one starting with / is from the den’s folder.',
  outside: 'This path goes above the den’s folder, so the image can’t be shown.',
  invalid:
    'This path can’t name a file in the den. A % in a file name is written %25, and a / can’t be part of a name; it’s simplest to rename the image without % or #.',
}

function MissingImage({ src, alt, why }: { src: string; alt: string | undefined; why: keyof typeof MISSING_TITLES }) {
  const label = alt?.trim() ? `Image not found: ${alt.trim()}` : 'Image not found'
  const title = MISSING_TITLES[why]
  return (
    <span className="blocked-image missing-image" title={title}>
      <span className="blocked-image-label">{label}</span> <span className="blocked-image-address">{src}</span>
    </span>
  )
}

/** An image kept in the den; if it doesn't load, the placeholder, with no retry. */
function DenImage({ src, url, alt, title }: { src: string; url: string; alt?: string; title?: string }) {
  // Keyed by address, so a failure is forgotten only when the address changes.
  const [failed, setFailed] = useState<string | null>(null)
  if (failed === url) {
    return <MissingImage src={src} alt={alt} why="missing" />
  }

  return <img src={url} alt={alt} title={title} onError={() => setFailed(url)} />
}

/** A note's image: from the den if it's local, and from a website only if the den allows it. */
function Image({
  src,
  alt,
  title,
  file,
  remoteImages,
}: {
  src: unknown
  alt?: string
  title?: string
  file: string
  remoteImages: { allowed: boolean; reload: boolean }
}) {
  if (typeof src !== 'string') {
    return null
  }

  const source = imageSource(src)
  if (source.kind === 'local') {
    const den = denImageUrl(src, file)
    switch (den.kind) {
      case 'address':
        return <img src={src} alt={alt} title={title} />
      case 'den':
        return <DenImage src={src} url={den.url} alt={alt} title={title} />
      default:
        return <MissingImage src={src} alt={alt} why={den.kind} />
    }
  }

  if (!remoteImages.allowed || !source.url) {
    return <BlockedImage src={src} alt={alt} url={source.url} reload={remoteImages.reload} />
  }

  return <img src={src} alt={alt} title={title} referrerPolicy="no-referrer" />
}

/** A link in a note, to a website; it opens in a new tab. */
function NoteLink({ href, children }: { href: string; children: ReactNode }) {
  return (
    <InLink.Provider value>
      <a href={href} target="_blank" rel="noopener noreferrer">
        {children}
      </a>
    </InLink.Provider>
  )
}

/**
 * A note's markdown, with its `[[...]]` links resolved as the API resolved
 * them. `file` is the den-relative path of the file the markdown is written
 * in, which its images' relative paths start from.
 */
export function Markdown({ text, links, universe, file }: { text: string; links: readonly LinkView[]; universe: string; file: string }) {
  const { allowed, reload } = useRemoteImages()
  const remoteImages = useMemo(() => ({ allowed, reload }), [allowed, reload])
  const { plugin, resolved } = useMemo(() => {
    const resolved: LinkView[] = []
    return { plugin: wikiLinks(links, resolved), resolved }
  }, [links])

  const components = useMemo<Components>(
    () => ({
      a: ({ href, children }) => {
        if (href?.startsWith(WIKI_PROTOCOL)) {
          const link = resolved[Number(href.slice(WIKI_PROTOCOL.length))]
          return link ? <WikiLink link={link} universe={universe} /> : <>{children}</>
        }

        return href ? <NoteLink href={href}>{children}</NoteLink> : <>{children}</>
      },
      img: ({ src, alt, title }) => <Image src={src} alt={alt} title={title} file={file} remoteImages={remoteImages} />,
      // A note's own headings sit under the section's heading.
      h1: ({ children }) => <h3>{children}</h3>,
      h2: ({ children }) => <h3>{children}</h3>,
    }),
    [resolved, universe, file, remoteImages],
  )

  return (
    <div className="markdown">
      <ReactMarkdown skipHtml remarkPlugins={[plugin]} urlTransform={urlTransform} components={components}>
        {text}
      </ReactMarkdown>
    </div>
  )
}
