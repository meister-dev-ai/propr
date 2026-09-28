// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { MAX_ADMISSION_BOUND, admissionBoundsError, capFromInput, capToInput } from '../useClientDetailViewModel'

// A blank budget field means "no limit" (null), never a $0 cap. These conversions guard that
// invariant in both directions so an unset cap round-trips as unset instead of silently becoming 0.
describe('budget cap input conversion', () => {
  it('capToInput renders a stored cap and treats null/undefined as a blank (no-limit) field', () => {
    expect(capToInput(100)).toBe('100')
    expect(capToInput(0)).toBe('0')
    expect(capToInput(null)).toBe('')
    expect(capToInput(undefined)).toBe('')
  })

  it('capFromInput parses a value and treats a blank field as null (no limit), never 0', () => {
    expect(capFromInput('100')).toBe(100)
    expect(capFromInput('12.50')).toBe(12.5)
    expect(capFromInput('')).toBeNull()
    expect(capFromInput('   ')).toBeNull()
    // An explicit zero is a real (block-everything) cap, distinct from a blank field.
    expect(capFromInput('0')).toBe(0)
  })

  // A <input type="number"> bound with v-model coerces its value to a number in the browser, so an edited cap
  // reaches capFromInput as a number rather than a string. It must handle that instead of throwing on .trim(),
  // otherwise entering any value throws during render and the Save button never activates.
  it('capFromInput accepts a number (as a number input yields) without throwing', () => {
    expect(capFromInput(50 as unknown as string)).toBe(50)
    expect(capFromInput(0 as unknown as string)).toBe(0)
    expect(capFromInput(12.5 as unknown as string)).toBe(12.5)
    expect(capFromInput(null as unknown as string)).toBeNull()
    expect(capFromInput(undefined as unknown as string)).toBeNull()
  })

  it('a stored cap round-trips through the input and back unchanged', () => {
    expect(capFromInput(capToInput(80))).toBe(80)
    expect(capFromInput(capToInput(0))).toBe(0)
    expect(capFromInput(capToInput(null))).toBeNull()
  })
})

// The number inputs carry min and step, which the browser applies to a submitted form. This editor saves
// through a button, so a decimal, a zero or a negative value typed by hand would otherwise travel to the
// backend and come back as a 400 with no field named.
describe('review admission bound validation', () => {
  it('accepts a whole number of at least 1 and a blank field', () => {
    expect(admissionBoundsError([['Changed files per review', '150']])).toBe('')
    expect(admissionBoundsError([['Changed files per review', '']])).toBe('')
    expect(admissionBoundsError([['Changed files per review', '1']])).toBe('')
  })

  it('names the field for a zero, a negative value and a decimal', () => {
    expect(admissionBoundsError([['Changed files per review', '0']])).toContain('Changed files per review')
    expect(admissionBoundsError([['Changed lines per review', '-1']])).toContain('Changed lines per review')
    expect(admissionBoundsError([['Repository size', '2.5']])).toContain('Repository size')
  })

  // The fields are bound with v-model on <input type="number">, so an edited bound reaches the check as a
  // number and not as the string it was loaded as.
  it('applies the same rule to a bound that arrives as a number', () => {
    expect(admissionBoundsError([['Changed files per review', 150 as unknown as string]])).toBe('')
    expect(admissionBoundsError([['Changed files per review', 0 as unknown as string]]))
      .toContain('Changed files per review')
    expect(admissionBoundsError([['Changed lines per review', -1 as unknown as string]]))
      .toContain('Changed lines per review')
    expect(admissionBoundsError([['Repository size', 2.5 as unknown as string]])).toContain('Repository size')
  })

  // The bounds are nullable 32-bit integers in the database. A larger value comes back as a 400 with no field
  // named, and one past JavaScript's safe-integer range is rounded before it is sent at all.
  it('refuses a bound above the 32-bit range and names the range it accepts', () => {
    expect(admissionBoundsError([['Diff size per review', String(MAX_ADMISSION_BOUND)]])).toBe('')

    const message = admissionBoundsError([['Diff size per review', String(MAX_ADMISSION_BOUND + 1)]])

    expect(message).toContain('Diff size per review')
    expect(message).toContain('1')
    expect(message).toContain(String(MAX_ADMISSION_BOUND))
  })

  it('refuses a bound beyond the safe-integer range, which would be rounded before it is sent', () => {
    expect(admissionBoundsError([['Diff size per review', '9007199254740993']])).toContain('Diff size per review')
  })

  it('reports the first bound that is wrong, leaving the ones that are fine out of the message', () => {
    const message = admissionBoundsError([
      ['Changed files per review', '150'],
      ['Diff size per review', '-4'],
      ['Repository size', '0'],
    ])

    expect(message).toContain('Diff size per review')
    expect(message).not.toContain('Repository size')
  })
})
