// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { components } from '@/services/generated/openapi'

export type FindingPostDestination = components['schemas']['FindingPostDestination']

/** The destination of a finding type, keyed by core type slug or by core type slug with a qualifier. */
export type FindingTypeDestinations = Partial<Record<string, FindingPostDestination>>

/** One row of the destination setting in the post configuration. */
export interface FindingTypeDestinationRow {
  /** The key the row writes: a core type slug, or a core type slug with a qualifier. */
  key: string
  label: string
}

const QUALIFIER_SEPARATOR = ':'

/**
 * The rows of the destination setting, in the order of the core taxonomy. The type "Documentation and tests" has two
 * rows: the key with the qualifier `missing` applies to requests for a test or document that does not exist yet, and
 * the key of the type applies to every other finding of the type. The server accepts a key with a qualifier for every
 * type; the interface offers one for this type only.
 */
export const FINDING_TYPE_DESTINATION_ROWS: readonly FindingTypeDestinationRow[] = [
  { key: 'logic-error', label: 'Logic error' },
  { key: 'data-validation', label: 'Data validation' },
  { key: 'resource-handling', label: 'Resource handling' },
  { key: 'concurrency', label: 'Concurrency' },
  { key: 'api-contract', label: 'API contract' },
  { key: 'security', label: 'Security' },
  { key: 'performance', label: 'Performance' },
  { key: 'error-handling-observability', label: 'Error handling and observability' },
  { key: 'design-structure', label: 'Design and structure' },
  { key: 'naming-clarity', label: 'Naming and clarity' },
  { key: 'documentation-tests:missing', label: 'Missing tests or documentation' },
  { key: 'documentation-tests', label: 'Incorrect tests or documentation' },
]

/** The destinations a row offers, from the most visible to the least visible. */
export const FINDING_POST_DESTINATION_OPTIONS: readonly { value: FindingPostDestination; label: string }[] = [
  { value: 'comment', label: 'Comment at the line' },
  { value: 'summary', label: 'Listed in the summary' },
  { value: 'reviewOnly', label: 'ProPR review only' },
]

/** A stored key with a qualifier that no row shows, for example a key set through the API. */
export interface QualifiedDestinationNote {
  key: string
  typeLabel: string
  qualifier: string
  destinationLabel: string
}

function typeKeyOf(key: string): string {
  const separator = key.indexOf(QUALIFIER_SEPARATOR)
  return separator < 0 ? key : key.slice(0, separator)
}

function hasQualifier(key: string): boolean {
  return key.includes(QUALIFIER_SEPARATOR)
}

/**
 * The destination that applies to the findings of one row: the entry of the row's key, for a key with a qualifier the
 * entry of its type next, and otherwise a comment. The server resolves a finding in the same order.
 */
export function destinationOfRow(stored: FindingTypeDestinations | null | undefined, key: string): FindingPostDestination {
  const map = stored ?? {}
  return map[key] ?? (hasQualifier(key) ? map[typeKeyOf(key)] : undefined) ?? 'comment'
}

/** Reads a stored destination map into one destination per row. */
export function rowDestinations(stored: FindingTypeDestinations | null | undefined): Record<string, FindingPostDestination> {
  return Object.fromEntries(FINDING_TYPE_DESTINATION_ROWS.map((row) => [row.key, destinationOfRow(stored, row.key)]))
}

/**
 * Writes the row destinations onto the stored map. Entries that no row shows, such as a key with a qualifier set
 * through the API, stay as they are. A type row set to a comment removes its entry, because a type without an entry
 * is posted as a comment. A row with a qualifier is always written, so that it keeps its destination when the row of
 * its type changes.
 */
export function applyRowDestinations(
  stored: FindingTypeDestinations | null | undefined,
  rows: Record<string, FindingPostDestination>,
): Record<string, FindingPostDestination> {
  const next: Record<string, FindingPostDestination> = {}
  for (const [key, destination] of Object.entries(stored ?? {})) {
    if (destination) {
      next[key] = destination
    }
  }
  for (const row of FINDING_TYPE_DESTINATION_ROWS) {
    const destination = rows[row.key] ?? 'comment'
    if (!hasQualifier(row.key) && destination === 'comment') {
      delete next[row.key]
    } else {
      next[row.key] = destination
    }
  }
  return next
}

// The name of a core type. The row of a type carries the name of the type, except for Documentation and tests, whose
// two rows are named after the findings they apply to.
function typeLabelOf(typeKey: string): string {
  if (typeKey === 'documentation-tests') {
    return 'Documentation and tests'
  }
  return FINDING_TYPE_DESTINATION_ROWS.find((row) => row.key === typeKey)?.label ?? typeKey
}

/**
 * The stored keys with a qualifier that no row shows, in the order of the rows of their types. The server looks up such a
 * key before the key of its type, so for the findings with that qualifier it takes precedence over the row of the type.
 * The rows do not change these keys.
 */
export function qualifiedDestinationNotes(stored: FindingTypeDestinations | null | undefined): QualifiedDestinationNote[] {
  const rowKeys = new Set(FINDING_TYPE_DESTINATION_ROWS.map((row) => row.key))
  const typeOrder = (typeKey: string) => {
    const index = FINDING_TYPE_DESTINATION_ROWS.findIndex((row) => row.key === typeKey)
    return index < 0 ? FINDING_TYPE_DESTINATION_ROWS.length : index
  }
  return Object.entries(stored ?? {})
    .filter((entry): entry is [string, FindingPostDestination] => entry[1] !== undefined)
    .filter(([key]) => hasQualifier(key) && !rowKeys.has(key))
    .sort(([left], [right]) => typeOrder(typeKeyOf(left)) - typeOrder(typeKeyOf(right)) || left.localeCompare(right))
    .map(([key, destination]) => ({
      key,
      typeLabel: typeLabelOf(typeKeyOf(key)),
      qualifier: key.slice(key.indexOf(QUALIFIER_SEPARATOR) + 1),
      destinationLabel: FINDING_POST_DESTINATION_OPTIONS.find((option) => option.value === destination)?.label ?? destination,
    }))
}

/** Whether any row shows a destination other than the one the stored map gives it. */
export function rowDestinationsChanged(
  stored: FindingTypeDestinations | null | undefined,
  rows: Record<string, FindingPostDestination>,
): boolean {
  return FINDING_TYPE_DESTINATION_ROWS.some((row) => (rows[row.key] ?? 'comment') !== destinationOfRow(stored, row.key))
}
