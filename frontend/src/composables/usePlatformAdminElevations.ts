// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, ref } from 'vue'
import { useSession } from '@/composables/useSession'
import {
  endElevation,
  getElevationStatus,
  startElevation,
  type PlatformAdminElevation,
  type StartElevationRequest,
} from '@/services/elevationsService'

/**
 * The licensed capability that elevations belong to. An elevation covers a tenant other than the System tenant, and an
 * installation without multi-tenancy uses the System tenant only.
 */
export const ELEVATION_CAPABILITY = 'multi-tenancy'

/** The System tenant belongs to the installation; platform administrators read its content without an elevation. */
export const SYSTEM_TENANT_ID = '11111111-1111-1111-1111-111111111111'

/** Whether the content of a client of this tenant needs an elevation when a platform administrator reads it. */
export function tenantNeedsElevation(tenantId: string | null | undefined): boolean {
  return !!tenantId && tenantId !== SYSTEM_TENANT_ID && tenantId !== '00000000-0000-0000-0000-000000000000'
}

// Module state, shared by every component of the session: the banner, the start dialog and the content views read
// the same active elevations.
const elevations = ref<PlatformAdminElevation[]>([])
const maxDurationMinutes = ref(240)
const loaded = ref(false)
const loadError = ref('')
const now = ref(Date.now())
let clock: ReturnType<typeof setInterval> | null = null
let clockUsers = 0

// Every load, every applied start or end and every reset takes the next generation, and `applied` holds the generation
// that wrote the current state. A load writes its answer only when its generation is higher, so an answer that arrives
// after a sign-out, after a start or an end, or after the answer of a newer load changes nothing. A newer load that
// fails writes nothing, so it does not keep an older load from writing its answer.
let generation = 0
let applied = 0
// Counts the resets. A start or an end writes its answer only in the session it began in.
let session = 0

/** The time left until an expiry, as hours and minutes, or minutes and seconds in the last hour. */
export function formatRemaining(expiresAt: string, at: number): string {
  const seconds = Math.max(0, Math.floor((Date.parse(expiresAt) - at) / 1000))
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  if (hours > 0) {
    return `${hours} h ${String(minutes).padStart(2, '0')} min`
  }

  return `${minutes} min ${String(seconds % 60).padStart(2, '0')} s`
}

/** The scope of an elevation in words, for the banner and the lists. */
export function describeScope(elevation: PlatformAdminElevation): string {
  return elevation.clientId
    ? `client ${elevation.clientDisplayName ?? elevation.clientId} of ${elevation.tenantDisplayName}`
    : `every client of ${elevation.tenantDisplayName}`
}

/**
 * The elevations of the signed-in platform administrator. Loads them for a platform administrator of an installation
 * whose license includes multi-tenancy, ticks a clock every second while a component shows a remaining time, and drops
 * an elevation from the active list once its expiry has passed, because the server refuses its content from then on.
 */
export function usePlatformAdminElevations() {
  const { isAdmin, isAuthenticated, isCapabilityAvailable } = useSession()

  /**
   * Whether the signed-in user can use elevations. Without multi-tenancy the components show no elevation controls and
   * send no elevation request, because the server refuses every elevation endpoint.
   */
  const available = computed(() =>
    isAuthenticated.value && isAdmin.value && isCapabilityAvailable(ELEVATION_CAPABILITY),
  )

  const active = computed(() =>
    elevations.value.filter((elevation) => Date.parse(elevation.expiresAt) > now.value),
  )

  /**
   * Loads the active elevations. A failure is kept in `loadError` until a later load succeeds, and the promise
   * rejects with it.
   */
  async function load(): Promise<void> {
    if (!available.value) {
      reset()
      return
    }

    const request = ++generation
    try {
      const status = await getElevationStatus()
      if (request <= applied) {
        return
      }

      applied = request
      elevations.value = status.active
      maxDurationMinutes.value = status.maxDurationMinutes
      now.value = Date.now()
      loaded.value = true
      loadError.value = ''
    } catch (failure) {
      if (request > applied) {
        loadError.value = failure instanceof Error ? failure.message : 'The active elevations could not be loaded.'
      }

      throw failure
    }
  }

  /** Loads the active elevations and keeps a failure in `loadError` only. */
  async function refresh(): Promise<void> {
    try {
      await load()
    } catch {
      // The failure is in loadError, where the banner and the panels show it.
    }
  }

  /**
   * Starts an elevation. The started elevation counts as active at once; the refresh that follows only updates the
   * list, so a failed refresh does not report the start as failed.
   */
  async function start(request: StartElevationRequest): Promise<PlatformAdminElevation> {
    const startedIn = session
    const elevation = await startElevation(request)
    if (startedIn === session) {
      applied = ++generation
      elevations.value = [...elevations.value.filter((existing) => existing.id !== elevation.id), elevation]
      await refresh()
    }

    return elevation
  }

  /** Ends an elevation. It leaves the active list at once, also when the refresh that follows fails. */
  async function end(elevationId: string): Promise<void> {
    const startedIn = session
    await endElevation(elevationId)
    if (startedIn === session) {
      applied = ++generation
      elevations.value = elevations.value.filter((existing) => existing.id !== elevationId)
      await refresh()
    }
  }

  function reset(): void {
    applied = ++generation
    session++
    elevations.value = []
    loaded.value = false
    loadError.value = ''
  }

  /** The active elevation that covers the client, or the tenant when no client is named. */
  function covering(tenantId: string | null | undefined, clientId?: string | null): PlatformAdminElevation | null {
    if (!tenantId) {
      return null
    }

    return active.value.find((elevation) =>
      elevation.tenantId === tenantId && (!elevation.clientId || elevation.clientId === clientId)) ?? null
  }

  function startClock(): void {
    clockUsers++
    if (clock === null) {
      clock = setInterval(() => {
        now.value = Date.now()
      }, 1000)
    }
  }

  function stopClock(): void {
    clockUsers = Math.max(0, clockUsers - 1)
    if (clockUsers === 0 && clock !== null) {
      clearInterval(clock)
      clock = null
    }
  }

  return {
    active,
    available,
    maxDurationMinutes,
    loaded,
    loadError,
    now,
    load,
    refresh,
    start,
    end,
    reset,
    covering,
    startClock,
    stopClock,
  }
}
