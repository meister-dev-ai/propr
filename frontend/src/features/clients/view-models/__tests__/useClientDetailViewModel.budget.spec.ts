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

  it('a stored cap round-trips through the input and back unchanged', () => {
    expect(capFromInput(capToInput(80))).toBe(80)
    expect(capFromInput(capToInput(0))).toBe(0)
    expect(capFromInput(capToInput(null))).toBeNull()
  })
})
