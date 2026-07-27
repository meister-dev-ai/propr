// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { probeAiConnection } from '@/services/aiConnectionsService'
import { createAdminClient } from '@/services/api'

vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(),
}))

function respondWith(error: unknown) {
  vi.mocked(createAdminClient).mockReturnValue({
    POST: vi.fn().mockResolvedValue({ data: undefined, error, response: { ok: false, status: 400 } }),
  } as never)
}

describe('surfacing why the server refused an AI connection call', () => {
  beforeEach(() => {
    vi.mocked(createAdminClient).mockReset()
  })

  // A validation ProblemDetails always titles itself the same way, so reading the title first told the operator
  // only that something was invalid. The field entry is the part that names what to change.
  it('prefers the field error over the generic validation title', async () => {
    respondWith({
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: { baseUrl: ['An API key or Azure identity is required for this provider.'] },
    })

    await expect(probeAiConnection('c1', {} as never)).rejects.toThrow(
      'An API key or Azure identity is required for this provider.',
    )
  })

  it('still uses the title when the body carries no field errors', async () => {
    respondWith({ title: 'The provider is not permitted for this tenant.', status: 400 })

    await expect(probeAiConnection('c1', {} as never)).rejects.toThrow('not permitted for this tenant')
  })

  it('prefers an explicit detail over both', async () => {
    respondWith({
      title: 'One or more validation errors occurred.',
      detail: 'The endpoint refused the credential.',
      errors: { baseUrl: ['baseUrl must use https.'] },
    })

    await expect(probeAiConnection('c1', {} as never)).rejects.toThrow('The endpoint refused the credential.')
  })

  it('falls back to a usable message when the body explains nothing', async () => {
    respondWith({})

    await expect(probeAiConnection('c1', {} as never)).rejects.toThrow('Failed to probe the provider connection.')
  })
})
