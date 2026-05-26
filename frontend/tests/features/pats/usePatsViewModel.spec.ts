// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { PatItem, PatsService } from '@/features/pats/view-models/usePatsViewModel'

const getAccessTokenMock = vi.fn<[], string | null>(() => 'token-abc')

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({ getAccessToken: getAccessTokenMock }),
}))

import { usePatsViewModel } from '@/features/pats/view-models/usePatsViewModel'

function makeService(): PatsService & {
  list: ReturnType<typeof vi.fn>
  create: ReturnType<typeof vi.fn>
  revoke: ReturnType<typeof vi.fn>
} {
  return {
    list: vi.fn(async () => []),
    create: vi.fn(async () => ({ token: 'pat-generated' })),
    revoke: vi.fn(async () => undefined),
  }
}

const samplePats: PatItem[] = [
  { id: '1', label: 'CI', createdAt: '2026-01-01T00:00:00Z', lastUsedAt: null, expiresAt: null, isRevoked: false },
  { id: '2', label: 'Local', createdAt: '2026-02-01T00:00:00Z', lastUsedAt: '2026-02-15T00:00:00Z', expiresAt: '2027-01-01T00:00:00Z', isRevoked: false },
]

describe('usePatsViewModel (FR-007, FR-008, FR-012)', () => {
  beforeEach(() => {
    getAccessTokenMock.mockReset()
    getAccessTokenMock.mockReturnValue('token-abc')
  })

  it('loadPats fetches tokens with Authorization header and populates list', async () => {
    const service = makeService()
    service.list.mockResolvedValueOnce(samplePats)
    const vm = usePatsViewModel({ service, autoLoad: false })

    await vm.loadPats()
    expect(service.list).toHaveBeenCalledWith({ Authorization: 'Bearer token-abc' })
    expect(vm.pats.value).toEqual(samplePats)
    expect(vm.loading.value).toBe(false)
    expect(vm.loadError.value).toBe('')
  })

  it('loadPats omits Authorization header when no access token is present', async () => {
    getAccessTokenMock.mockReturnValue(null)
    const service = makeService()
    const vm = usePatsViewModel({ service, autoLoad: false })

    await vm.loadPats()
    expect(service.list).toHaveBeenCalledWith({})
  })

  it('loadPats surfaces loadError on failure', async () => {
    const service = makeService()
    service.list.mockRejectedValueOnce(new Error('boom'))
    const vm = usePatsViewModel({ service, autoLoad: false })

    await vm.loadPats()
    expect(vm.loadError.value).toContain('boom')
    expect(vm.pats.value).toEqual([])
  })

  it('createToken rejects empty label with validation message and does not call service', async () => {
    const service = makeService()
    const vm = usePatsViewModel({ service, autoLoad: false })

    await vm.createToken()
    expect(vm.createError.value).toBe('Label is required.')
    expect(service.create).not.toHaveBeenCalled()
  })

  it('createToken sends label, sets generatedToken, clears form, and reloads', async () => {
    const service = makeService()
    service.create.mockResolvedValueOnce({ token: 'pat-new' })
    service.list.mockResolvedValueOnce(samplePats)
    const vm = usePatsViewModel({ service, autoLoad: false })
    vm.newLabel.value = 'CI pipeline'

    await vm.createToken()
    expect(service.create).toHaveBeenCalledWith({ Authorization: 'Bearer token-abc' }, { label: 'CI pipeline' })
    expect(vm.generatedToken.value).toBe('pat-new')
    expect(vm.newLabel.value).toBe('')
    expect(vm.pats.value).toEqual(samplePats)
  })

  it('createToken includes expiresAt ISO when newExpires is provided', async () => {
    const service = makeService()
    const vm = usePatsViewModel({ service, autoLoad: false })
    vm.newLabel.value = 'pat'
    vm.newExpires.value = '2027-01-01T12:00'

    await vm.createToken()
    expect(service.create).toHaveBeenCalledWith(
      { Authorization: 'Bearer token-abc' },
      expect.objectContaining({ label: 'pat', expiresAt: expect.stringMatching(/2027-01-01T1[12]:00:00\.000Z/) }),
    )
  })

  it('createToken surfaces createError on failure and clears creating flag', async () => {
    const service = makeService()
    service.create.mockRejectedValueOnce(new Error('blocked'))
    const vm = usePatsViewModel({ service, autoLoad: false })
    vm.newLabel.value = 'pat'

    await vm.createToken()
    expect(vm.createError.value).toContain('blocked')
    expect(vm.creating.value).toBe(false)
  })

  it('revokeToken calls service.revoke and removes the token from the list', async () => {
    const service = makeService()
    const vm = usePatsViewModel({ service, autoLoad: false })
    vm.pats.value = [...samplePats]

    await vm.revokeToken('1')
    expect(service.revoke).toHaveBeenCalledWith({ Authorization: 'Bearer token-abc' }, '1')
    expect(vm.pats.value.map((p) => p.id)).toEqual(['2'])
    expect(vm.revoking.value).toBeNull()
  })

  it('dismissGeneratedToken clears the revealed token', () => {
    const service = makeService()
    const vm = usePatsViewModel({ service, autoLoad: false })
    vm.generatedToken.value = 'pat-x'
    vm.dismissGeneratedToken()
    expect(vm.generatedToken.value).toBe('')
  })

  it('copyGeneratedToken writes the generated token to the injected clipboard helper', async () => {
    const service = makeService()
    const copy = vi.fn(async () => {})
    const vm = usePatsViewModel({ service, autoLoad: false, copyToClipboard: copy })
    vm.generatedToken.value = 'pat-x'

    await vm.copyGeneratedToken()
    expect(copy).toHaveBeenCalledWith('pat-x')
  })

  it('copyGeneratedToken is a no-op when no token is generated', async () => {
    const service = makeService()
    const copy = vi.fn(async () => {})
    const vm = usePatsViewModel({ service, autoLoad: false, copyToClipboard: copy })

    await vm.copyGeneratedToken()
    expect(copy).not.toHaveBeenCalled()
  })

  it('formatDate returns a locale string', () => {
    const service = makeService()
    const vm = usePatsViewModel({ service, autoLoad: false })
    const result = vm.formatDate('2026-05-25T12:34:56Z')
    expect(result).toContain('2026')
  })
})
