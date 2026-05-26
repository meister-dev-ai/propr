// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

describe('credential safety', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    vi.resetModules()
  })

  it('redacts bearer tokens, PATs, ADO tokens, and explicit secret fields from diagnostic text', async () => {
    const { sanitizeSensitiveText } = await import('@/services/credentialSafety')

    const raw = 'Authorization: Bearer live.jwt.token X-Ado-Token=ado-secret X-User-Pat=pat-secret {"refreshToken":"refresh-secret","secret":"plain-secret"}'
    const sanitized = sanitizeSensitiveText(raw)

    expect(sanitized).not.toContain('live.jwt.token')
    expect(sanitized).not.toContain('ado-secret')
    expect(sanitized).not.toContain('pat-secret')
    expect(sanitized).not.toContain('refresh-secret')
    expect(sanitized).not.toContain('plain-secret')
    expect(sanitized).toContain('[redacted]')
  })

  it('does not echo bearer tokens back through token-usage service errors', async () => {
    const { getClientTokenUsage } = await import('@/services/clientTokenUsageService')
    vi.mocked(global.fetch).mockRejectedValueOnce(
      new Error('Authorization: Bearer bearer-secret should not leak'),
    )

    await expect(getClientTokenUsage('client-1', '2026-01-01', '2026-01-31', 'bearer-secret')).rejects.toThrow(
      '[redacted]',
    )
  })

  it('does not echo tokens from API problem payloads in password-change errors', async () => {
    vi.doMock('@/services/api', async () => {
      const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
      return {
        ...actual,
        createAdminClient: () => ({
          POST: vi.fn().mockResolvedValue({
            error: {
              detail: 'Authorization: Bearer session-secret and X-Ado-Token=ado-secret must never leak',
            },
            response: { ok: false, status: 400 },
          }),
        }),
      }
    })

    const { changeMyPassword } = await import('@/services/userSecurityService')

    await expect(changeMyPassword({ currentPassword: 'old-pass', newPassword: 'new-pass-123' })).rejects.toThrow(
      '[redacted]',
    )
  })
})
