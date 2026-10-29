// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, ref, toValue, type MaybeRefOrGetter } from 'vue'
import { capFromInput, capToInput, MAX_ADMISSION_BOUND } from '@/features/clients/view-models/useClientDetailViewModel'
import {
  getTenant,
  updateTenantReviewLimits,
  type TenantDto,
  type TenantReviewExecutionLimits,
} from '@/services/tenantAdminService'

/** The shortest and the longest maximum review duration the backend accepts, in seconds. */
export const MIN_REVIEW_DURATION_SECONDS = 60
export const MAX_REVIEW_DURATION_SECONDS = 86_400

const NO_LIMITS: TenantReviewExecutionLimits = { maxConcurrentReviews: null, maxReviewDurationSeconds: null }

export interface UseTenantReviewLimitsOptions {
  /** Overridable tenant read for tests; defaults to the live tenant endpoint. */
  loader?: (tenantId: string) => Promise<TenantDto>
  /** Overridable write for tests; defaults to the live review-limits endpoint. */
  saver?: (tenantId: string, limits: TenantReviewExecutionLimits) => Promise<TenantReviewExecutionLimits>
}

/** Writes a cap on concurrent reviews for a reader, or "No limit". */
export function formatConcurrentReviews(value: number | null | undefined): string {
  if (value == null) {
    return 'No limit'
  }

  return value === 1 ? '1 review at a time' : `${value} reviews at a time`
}

/** Writes a maximum review duration in hours, minutes and seconds, or "No limit". */
export function formatReviewDuration(seconds: number | null | undefined): string {
  if (seconds == null) {
    return 'No limit'
  }

  const plural = (count: number, unit: string) => `${count} ${unit}${count === 1 ? '' : 's'}`
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const rest = seconds % 60
  const parts = [
    hours > 0 ? plural(hours, 'hour') : null,
    minutes > 0 ? plural(minutes, 'minute') : null,
    rest > 0 ? plural(rest, 'second') : null,
  ].filter((part): part is string => part !== null)
  return parts.join(' ')
}

/**
 * Reads the review limits of a tenant from the tenant record, which every tenant administrator may read, and
 * stores them through the review-limits endpoint, which only a platform administrator may call. The endpoint
 * stores both limits as a whole, so a field left blank clears that limit. The duration is edited in minutes and
 * stored in seconds.
 *
 * The tenant can change while the section stays mounted, because the tenant route reuses its view. A save
 * therefore goes to the tenant whose limits the form was loaded from.
 */
export function useTenantReviewLimits(tenantId: MaybeRefOrGetter<string>, options: UseTenantReviewLimitsOptions = {}) {
  const load = options.loader ?? getTenant
  const save = options.saver ?? updateTenantReviewLimits

  const limits = ref<TenantReviewExecutionLimits | null>(null)
  const loading = ref(false)
  const saving = ref(false)
  const error = ref('')
  const saved = ref(false)
  const editedConcurrentReviews = ref<string | number>('')
  const editedDurationMinutes = ref<string | number>('')

  // A navigation to another tenant starts a second read while the first may still be open. Only the response of
  // the latest read may fill the form, or one tenant's limits could be saved onto another.
  let loadGeneration = 0
  let loadedTenantId: string | null = null

  function apply(next: TenantReviewExecutionLimits): void {
    limits.value = next
    editedConcurrentReviews.value = capToInput(next.maxConcurrentReviews)
    editedDurationMinutes.value = next.maxReviewDurationSeconds == null
      ? ''
      : String(next.maxReviewDurationSeconds / 60)
  }

  async function loadLimits(): Promise<void> {
    const generation = ++loadGeneration
    const readTenantId = toValue(tenantId)
    loading.value = true
    error.value = ''
    saved.value = false
    // The values on screen belong to the tenant read before, so they are not shown while another tenant loads.
    limits.value = null
    loadedTenantId = null
    try {
      const tenant = await load(readTenantId)
      if (generation === loadGeneration) {
        loadedTenantId = readTenantId
        apply(tenant.reviewExecutionLimits ?? NO_LIMITS)
      }
    } catch {
      if (generation === loadGeneration) {
        error.value = 'The review limits of this tenant could not be loaded. Try again.'
      }
    } finally {
      if (generation === loadGeneration) {
        loading.value = false
      }
    }
  }

  /** The values the form would store: a number, or null for a blank field. */
  const requested = computed<TenantReviewExecutionLimits>(() => {
    const minutes = capFromInput(editedDurationMinutes.value)
    return {
      maxConcurrentReviews: capFromInput(editedConcurrentReviews.value),
      maxReviewDurationSeconds: minutes === null ? null : Math.round(minutes * 60),
    }
  })

  /**
   * The rules the backend enforces, checked here so a value it would refuse is reported on the form before the
   * request is sent. The number input's min and step attributes do not stop a typed value.
   */
  const validationError = computed(() => {
    const { maxConcurrentReviews, maxReviewDurationSeconds } = requested.value
    if (
      maxConcurrentReviews !== null
      && (!Number.isInteger(maxConcurrentReviews) || maxConcurrentReviews < 1 || maxConcurrentReviews > MAX_ADMISSION_BOUND)
    ) {
      return 'Concurrent reviews must be a whole number of at least 1, or blank for no limit.'
    }

    if (
      maxReviewDurationSeconds !== null
      && (!Number.isFinite(maxReviewDurationSeconds)
        || maxReviewDurationSeconds < MIN_REVIEW_DURATION_SECONDS
        || maxReviewDurationSeconds > MAX_REVIEW_DURATION_SECONDS)
    ) {
      return 'The maximum review duration must be from 1 to 1440 minutes, or blank for no limit.'
    }

    return ''
  })

  function isDirty(): boolean {
    if (limits.value === null) {
      return false
    }

    return (
      requested.value.maxConcurrentReviews !== limits.value.maxConcurrentReviews
      || requested.value.maxReviewDurationSeconds !== limits.value.maxReviewDurationSeconds
    )
  }

  function isSaveEnabled(): boolean {
    return !saving.value && limits.value !== null && isDirty() && validationError.value === ''
  }

  async function saveLimits(): Promise<void> {
    if (!isSaveEnabled() || loadedTenantId === null) {
      return
    }

    // A navigation to another tenant during the save starts a new read. The answer of the save then belongs to the
    // tenant shown before, so it may not fill the form of the tenant shown now.
    const generation = loadGeneration
    saving.value = true
    error.value = ''
    saved.value = false
    try {
      const stored = await save(loadedTenantId, requested.value)
      if (generation === loadGeneration) {
        apply(stored)
        saved.value = true
      }
    } catch (caught) {
      if (generation === loadGeneration) {
        error.value = caught instanceof Error && caught.message
          ? caught.message
          : 'The review limits could not be saved.'
      }
    } finally {
      saving.value = false
    }
  }

  return {
    limits,
    loading,
    saving,
    error,
    saved,
    editedConcurrentReviews,
    editedDurationMinutes,
    validationError,
    loadLimits,
    saveLimits,
    isSaveEnabled,
  }
}
