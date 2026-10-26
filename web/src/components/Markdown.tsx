import { useMemo } from 'react'
import ReactMarkdown, { defaultUrlTransform, type Components } from 'react-markdown'
import type { LinkView } from '../api'
import { WIKI_LINK, WikiLink, createLinkMatcher } from './Links'

// Notes' markdown, rendered safely. react-markdown builds React elements
// (never HTML strings), `skipHtml` drops any raw HTML in a note, and its URL
// check drops `javascript:` and other unsafe links. `[[...]]` links become
// in-app links through the API's resolved `links`.

const WIKI_PROTOCOL = 'wolfden-link:'

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

function urlTransform(url: string): string {
  return url.startsWith(WIKI_PROTOCOL) ? url : defaultUrlTransform(url)
}

/** A note's markdown, with its `[[...]]` links resolved as the API resolved them. */
export function Markdown({ text, links, universe }: { text: string; links: readonly LinkView[]; universe: string }) {
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

        return href ? (
          <a href={href} target="_blank" rel="noopener noreferrer">
            {children}
          </a>
        ) : (
          <>{children}</>
        )
      },
      // A note's own headings sit under the section's heading.
      h1: ({ children }) => <h3>{children}</h3>,
      h2: ({ children }) => <h3>{children}</h3>,
    }),
    [resolved, universe],
  )

  return (
    <div className="markdown">
      <ReactMarkdown skipHtml remarkPlugins={[plugin]} urlTransform={urlTransform} components={components}>
        {text}
      </ReactMarkdown>
    </div>
  )
}
