// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { capFromInput, capToInput } from '../useClientDetailViewModel'

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
