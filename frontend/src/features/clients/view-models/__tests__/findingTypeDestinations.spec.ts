// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import {
  applyRowDestinations,
  destinationOfRow,
  qualifiedDestinationNotes,
  rowDestinations,
  rowDestinationsChanged,
} from '../findingTypeDestinations'

describe('finding type destinations', () => {
  it('reads a row with a qualifier from the entry of its type when the row has no entry of its own', () => {
    // The server looks up the key with the qualifier first and the key of the type second.
    expect(destinationOfRow({ 'documentation-tests': 'reviewOnly' }, 'documentation-tests:missing')).toBe('reviewOnly')
    expect(destinationOfRow({ 'documentation-tests:missing': 'summary', 'documentation-tests': 'reviewOnly' }, 'documentation-tests:missing'))
      .toBe('summary')
    expect(destinationOfRow({}, 'security')).toBe('comment')
  })

  it('writes every row and keeps the entries that no row shows', () => {
    const rows = rowDestinations({ 'documentation-tests:missing': 'summary' })
    rows.security = 'summary'

    expect(applyRowDestinations({ 'documentation-tests:missing': 'summary', 'logic-error:incorrect': 'summary' }, rows))
      .toEqual({
        'documentation-tests:missing': 'summary',
        'logic-error:incorrect': 'summary',
        security: 'summary',
      })
  })

  it('lists the keys with a qualifier that no row shows, in the order of the rows of their types', () => {
    const notes = qualifiedDestinationNotes({
      'documentation-tests:missing': 'summary',
      'documentation-tests': 'reviewOnly',
      'documentation-tests:extraneous': 'comment',
      'security:incorrect': 'summary',
      'logic-error:missing': 'reviewOnly',
    })

    expect(notes).toEqual([
      { key: 'logic-error:missing', typeLabel: 'Logic error', qualifier: 'missing', destinationLabel: 'ProPR review only' },
      { key: 'security:incorrect', typeLabel: 'Security', qualifier: 'incorrect', destinationLabel: 'Listed in the summary' },
      {
        key: 'documentation-tests:extraneous',
        typeLabel: 'Documentation and tests',
        qualifier: 'extraneous',
        destinationLabel: 'Comment at the line',
      },
    ])
    expect(qualifiedDestinationNotes({ 'documentation-tests:missing': 'summary' })).toEqual([])
    expect(qualifiedDestinationNotes(null)).toEqual([])
  })

  it('reports a change only when a row shows another destination than the stored map gives it', () => {
    const stored = { 'documentation-tests': 'reviewOnly' as const }
    const rows = rowDestinations(stored)

    expect(rowDestinationsChanged(stored, rows)).toBe(false)
    expect(rowDestinationsChanged(stored, { ...rows, 'documentation-tests:missing': 'summary' })).toBe(true)
  })
})
