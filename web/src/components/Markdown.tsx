import { createContext, useContext, useMemo, type ReactNode } from 'react'
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
// Content-Security-Policy blocks it too, as a second line of defence.

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

/** A note's image: as written if it's local, and from a website only if the den allows it. */
function Image({ src, alt, title, remoteImages }: { src: unknown; alt?: string; title?: string; remoteImages: { allowed: boolean; reload: boolean } }) {
  if (typeof src !== 'string') {
    return null
  }

  const source = imageSource(src)
  if (source.kind === 'local') {
    return <img src={src} alt={alt} title={title} />
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

/** A note's markdown, with its `[[...]]` links resolved as the API resolved them. */
export function Markdown({ text, links, universe }: { text: string; links: readonly LinkView[]; universe: string }) {
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
      img: ({ src, alt, title }) => <Image src={src} alt={alt} title={title} remoteImages={remoteImages} />,
      // A note's own headings sit under the section's heading.
      h1: ({ children }) => <h3>{children}</h3>,
      h2: ({ children }) => <h3>{children}</h3>,
    }),
    [resolved, universe, remoteImages],
  )

  return (
    <div className="markdown">
      <ReactMarkdown skipHtml remarkPlugins={[plugin]} urlTransform={urlTransform} components={components}>
        {text}
      </ReactMarkdown>
    </div>
  )
}
