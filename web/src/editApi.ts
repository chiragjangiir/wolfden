import { queryOptions } from '@tanstack/react-query'
import { ApiRequestError, apiPath, getJson, type EntitySummary } from './api'
import type { Json } from './fieldModel'

// The editing API (#28; src/Wolfden.Api/WriteEndpoints.cs): an entity's
// source, and edits of its lead, sections, fields, and whole file. Every edit carries
// the hash of the file it was based on; if the file changed since, the API
// refuses it with `stale_file` and sends the file as it is now.

/** The lead of an entity's file: the text before its first section, title heading included. */
export interface SourceLead {
  line: number
  text: string
}

/** One section of an entity's file, as an editor needs it. */
export interface SourceSection {
  /** Its place among the file's sections, from 0, subsections included. An edit names it by this and its name. */
  index: number
  name: string
  level: number
  line: number
  /** Its section comment (`<!-- wolfden: ... -->`), which isn't part of `body` and is kept by an edit of it. */
  comment: string | null
  /** Its own text as written, with LF line breaks, subsections not included. */
  body: string
  pinned: boolean
  parent: number | null
}

/** An entity's file as an editor needs it (GET /api/entity/source). */
export interface EntitySource {
  version: number
  entity: EntitySummary
  hash: string
  /** The whole file, with LF line breaks. */
  text: string
  lineEndings: 'lf' | 'crlf' | 'mixed'
  byteOrderMark: boolean
  /** Why the note can't be edited (it's in a final book), or null. */
  frozen: string | null
  lead: SourceLead
  sections: SourceSection[]
  standardSections: string[]
  /** Each field the file sets, in file order, with its value as written. */
  fields: SourceField[]
  /** The fields the type defines, in field order, then the universal `placeholder`. */
  fieldTypes: SourceFieldType[]
}

/** A field the file sets: its value as written, a plain bool or number as one and every other scalar as text. */
export interface SourceField {
  name: string
  line: number
  value: Json
}

/** A field the entity's type defines (types.md §5). */
export interface SourceFieldType {
  name: string
  type: 'string' | 'text' | 'number' | 'bool' | 'date' | 'enum' | 'ref' | 'position'
  list: boolean
  temporal: boolean
  required: boolean
  /** An enum's values. */
  values: string[]
  /** The types a ref links to, or null for any. */
  to: string[] | null
  description: string | null
  default: Json | null
}

export interface DiagnosticView {
  path: string
  line: number | null
  severity: string
  rule: string
  message: string
}

/** What an edit did (the MCP write tools' WriteResult). */
export interface WriteResult {
  version: number
  changed: boolean
  path: string
  hash: string
  entity: EntitySummary | null
  effects: string[]
  pinnedCanon: string[]
  warnings: DiagnosticView[]
  files: { path: string; hash: string }[] | null
  idAdded: string | null
}

/** An edit the API refused. For `stale_file`, `current` is the file as it is now, if it can still be read. */
export class EditError extends ApiRequestError {
  readonly current: EntitySource | null

  constructor(code: string, message: string, current: EntitySource | null) {
    super(code, message)
    this.current = current
  }
}

export function sourceQuery(universe: string, entity: string) {
  return queryOptions({
    queryKey: ['source', universe, entity],
    queryFn: () => getJson<EntitySource>(apiPath('/api/entity/source', { entity, universe })),
    // An editor always starts from the file as it is.
    staleTime: 0,
  })
}

/** The edits, by their path under /api/edit. */
export type EditPath = 'lead' | 'section' | 'section/add' | 'section/rename' | 'section/remove' | 'file' | 'field'

/** Sends an edit as JSON (the API takes nothing else), and returns its result, or throws an EditError. */
export async function postEdit(path: EditPath, body: Record<string, unknown>): Promise<WriteResult> {
  const response = await fetch(`/api/edit/${path}`, {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const answer: unknown = await response.json().catch(() => undefined)
  const json = typeof answer === 'object' && answer !== null ? (answer as Record<string, unknown>) : null
  if (!response.ok) {
    throw new EditError(
      typeof json?.code === 'string' ? json.code : 'http_error',
      typeof json?.message === 'string' ? json.message : `The server answered ${response.status}.`,
      (json?.current as EntitySource | undefined) ?? null,
    )
  }

  if (!json) {
    throw new EditError('bad_response', "The server's answer to the edit wasn't the JSON the page expects.", null)
  }

  return json as unknown as WriteResult
}
