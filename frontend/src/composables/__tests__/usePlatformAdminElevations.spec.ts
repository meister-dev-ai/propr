// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  formatRemaining,
  SYSTEM_TENANT_ID,
  tenantNeedsElevation,
  usePlatformAdminElevations,
} from '@/composables/usePlatformAdminElevations'
import type { ElevationStatus, PlatformAdminElevation } from '@/services/elevationsService'

const mocks = vi.hoisted(() => ({
  getElevationStatus: vi.fn(),
  startElevation: vi.fn(),
  endElevation: vi.fn(),
}))

const multiTenancy = ref(true)

vi.mock('@/services/elevationsService', () => mocks)
vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAdmin: computed(() => true),
    isAuthenticated: computed(() => true),
    isCapabilityAvailable: (key: string) => key === 'multi-tenancy' && multiTenancy.value,
  }),
}))

function elevation(tenantId: string, clientId: string | null): PlatformAdminElevation {
  return {
    id: `${tenantId}-${clientId ?? 'all'}`,
    administratorName: 'operator',
    tenantId,
    tenantDisplayName: tenantId,
    clientId,
    clientDisplayName: clientId,
    reason: 'SUP-1',
    startedAt: new Date(Date.now() - 60_000).toISOString(),
    expiresAt: new Date(Date.now() + 60 * 60_000).toISOString(),
    endedAt: null,
    endedByName: null,
    state: 'active',
    contentReads: 0,
  }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => {
    resolve = done
  })
  return { promise, resolve }
}

describe('usePlatformAdminElevations', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    multiTenancy.value = true
    usePlatformAdminElevations().reset()
  })

  it('sends no request and reports no elevation when the license does not include multi-tenancy', async () => {
    multiTenancy.value = false
    mocks.getElevationStatus.mockResolvedValue({ maxDurationMinutes: 240, active: [elevation('tenant-a', null)] })
    const elevations = usePlatformAdminElevations()

    await elevations.load()

    expect(elevations.available.value).toBe(false)
    expect(mocks.getElevationStatus).not.toHaveBeenCalled()
    expect(elevations.active.value).toEqual([])
    expect(elevations.loaded.value).toBe(false)
  })

  it('finds the elevation that covers a client, by its client or by its whole tenant', async () => {
    mocks.getElevationStatus.mockResolvedValue({
      maxDurationMinutes: 240,
      active: [elevation('tenant-a', 'client-1'), elevation('tenant-b', null)],
    })
    const elevations = usePlatformAdminElevations()

    await elevations.load()

    expect(elevations.covering('tenant-a', 'client-1')?.id).toBe('tenant-a-client-1')
    expect(elevations.covering('tenant-a', 'client-2')).toBeNull()
    expect(elevations.covering('tenant-b', 'client-9')?.id).toBe('tenant-b-all')
    expect(elevations.covering('tenant-c', 'client-1')).toBeNull()
    expect(elevations.maxDurationMinutes.value).toBe(240)
  })

  it('applies no answer that arrives after the session was reset, for example by a sign-out', async () => {
    const pending = deferred<ElevationStatus>()
    mocks.getElevationStatus.mockReturnValue(pending.promise)
    const elevations = usePlatformAdminElevations()

    const load = elevations.load()
    elevations.reset()
    pending.resolve({ maxDurationMinutes: 240, active: [elevation('tenant-a', null)] })
    await load

    expect(elevations.active.value).toEqual([])
    expect(elevations.loaded.value).toBe(false)
  })

  it('keeps the answer of the newest load when an older one arrives later', async () => {
    const older = deferred<ElevationStatus>()
    const newer = deferred<ElevationStatus>()
    mocks.getElevationStatus.mockReturnValueOnce(older.promise).mockReturnValueOnce(newer.promise)
    const elevations = usePlatformAdminElevations()

    const first = elevations.load()
    const second = elevations.load()
    newer.resolve({ maxDurationMinutes: 240, active: [elevation('tenant-new', null)] })
    await second
    older.resolve({ maxDurationMinutes: 240, active: [elevation('tenant-old', null)] })
    await first

    expect(elevations.active.value.map((active) => active.tenantId)).toEqual(['tenant-new'])
  })

  it('applies the answer of an older load when a newer load fails before it arrives', async () => {
    const older = deferred<ElevationStatus>()
    mocks.getElevationStatus
      .mockReturnValueOnce(older.promise)
      .mockRejectedValueOnce(new Error('The active elevations could not be loaded.'))
    const elevations = usePlatformAdminElevations()

    const first = elevations.load()
    await expect(elevations.load()).rejects.toThrow('could not be loaded')
    older.resolve({ maxDurationMinutes: 240, active: [elevation('tenant-old', null)] })
    await first

    expect(elevations.active.value.map((active) => active.tenantId)).toEqual(['tenant-old'])
    expect(elevations.loaded.value).toBe(true)
    expect(elevations.loadError.value).toBe('')
  })

  it('applies no answer of a load that started before an elevation was started', async () => {
    const older = deferred<ElevationStatus>()
    mocks.getElevationStatus
      .mockReturnValueOnce(older.promise)
      .mockRejectedValueOnce(new Error('The active elevations could not be loaded.'))
    mocks.startElevation.mockResolvedValue(elevation('tenant-a', 'client-1'))
    const elevations = usePlatformAdminElevations()

    const first = elevations.load()
    await elevations.start({ tenantId: 'tenant-a', clientId: 'client-1', reason: 'SUP-1', durationMinutes: 30 })
    older.resolve({ maxDurationMinutes: 240, active: [] })
    await first

    expect(elevations.covering('tenant-a', 'client-1')?.id).toBe('tenant-a-client-1')
  })

  it('reports a started elevation as started and active when the refresh after it fails', async () => {
    mocks.startElevation.mockResolvedValue(elevation('tenant-a', 'client-1'))
    mocks.getElevationStatus.mockRejectedValue(new Error('The active elevations could not be loaded.'))
    const elevations = usePlatformAdminElevations()

    const started = await elevations.start({ tenantId: 'tenant-a', clientId: 'client-1', reason: 'SUP-1', durationMinutes: 30 })

    expect(started.id).toBe('tenant-a-client-1')
    expect(elevations.covering('tenant-a', 'client-1')?.id).toBe('tenant-a-client-1')
    expect(elevations.loadError.value).toBe('The active elevations could not be loaded.')
  })

  it('removes an ended elevation from the active list when the refresh after it fails', async () => {
    mocks.getElevationStatus.mockResolvedValueOnce({ maxDurationMinutes: 240, active: [elevation('tenant-a', null)] })
    const elevations = usePlatformAdminElevations()
    await elevations.load()
    mocks.endElevation.mockResolvedValue({ ...elevation('tenant-a', null), state: 'ended' })
    mocks.getElevationStatus.mockRejectedValueOnce(new Error('The active elevations could not be loaded.'))

    await elevations.end('tenant-a-all')

    expect(elevations.active.value).toEqual([])
    expect(elevations.loadError.value).toBe('The active elevations could not be loaded.')
  })

  it('reports a refused start and changes nothing', async () => {
    mocks.startElevation.mockRejectedValue(new Error('The duration must be between 1 and 240 minutes.'))
    const elevations = usePlatformAdminElevations()

    await expect(
      elevations.start({ tenantId: 'tenant-a', reason: 'SUP-1', durationMinutes: 300 }),
    ).rejects.toThrow('between 1 and 240 minutes')

    expect(elevations.active.value).toEqual([])
    expect(mocks.getElevationStatus).not.toHaveBeenCalled()
  })

  it('needs an elevation for every tenant except the System tenant', () => {
    expect(tenantNeedsElevation('tenant-a')).toBe(true)
    expect(tenantNeedsElevation(SYSTEM_TENANT_ID)).toBe(false)
    expect(tenantNeedsElevation(null)).toBe(false)
  })

  it('counts down hours and minutes, and minutes and seconds in the last hour', () => {
    const now = Date.parse('2026-10-26T10:00:00Z')
    expect(formatRemaining('2026-10-26T12:05:00Z', now)).toBe('2 h 05 min')
    expect(formatRemaining('2026-10-26T10:04:07Z', now)).toBe('4 min 07 s')
    expect(formatRemaining('2026-10-26T09:00:00Z', now)).toBe('0 min 00 s')
  })
})
