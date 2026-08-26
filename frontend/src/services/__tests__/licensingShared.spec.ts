// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { normalizeCapability } from '@/services/licensingShared'

describe('normalizeCapability', () => {
  it('exposes the reason an unavailable capability carries', () => {
    const capability = normalizeCapability({
      key: 'mention-answering',
      displayName: 'Mention answering',
      requiresCommercial: true,
      overrideState: 'default',
      isAvailable: false,
      message: "This installation's license does not cover mention answering.",
      reason: 'notInLicense',
    })

    expect(capability.isAvailable).toBe(false)
    expect(capability.reason).toBe('notInLicense')
    expect(capability.message).toBe("This installation's license does not cover mention answering.")
  })

  it('reports no reason for an available capability', () => {
    const capability = normalizeCapability({
      key: 'budgeting',
      displayName: 'Budgeting',
      requiresCommercial: true,
      overrideState: 'default',
      isAvailable: true,
      message: null,
    })

    expect(capability.isAvailable).toBe(true)
    expect(capability.reason).toBeNull()
  })

  it('reports no reason when the payload omits the field', () => {
    const capability = normalizeCapability({ key: 'crawl-configs', isAvailable: false })

    expect(capability.reason).toBeNull()
    expect(capability.displayName).toBe('crawl-configs')
  })

  it('falls back to a safe shape when there is no capability at all', () => {
    const capability = normalizeCapability(null)

    expect(capability.key).toBe('')
    expect(capability.isAvailable).toBe(false)
    expect(capability.overrideState).toBe('default')
    expect(capability.reason).toBeNull()
  })
})
