// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

import { computed, ref } from 'vue'
import {
  activateLicense,
  getLicenseActivationHistory,
  getLicensingSummary,
  LicenseStateUnavailableError,
  removeLicense,
  setCapabilityOverride,
  type AuthorOverage,
  type LicenseActivationEvent,
  type LicensingSummary,
  type PremiumCapabilityOverrideStateValue,
} from '@/services/licensingService'
import { isNoticeStage, type NoticeStage } from '@/features/licensing/licensingCopy'

/**
 * The result of a mutation. The write has committed by the time one of these is returned, so it reports only
 * on the state read back after it.
 */
export interface MutationOutcome {
  /**
   * True when the summary read that followed the mutation failed, so the state held here still describes the
   * installation as it was before the write.
   */
  isSummaryStale: boolean
}

/**
 * Shared installation licensing state.
 *
 * Two surfaces read it: the licensing panel and the expiry notice rendered beside every page. Holding it
 * once means an activation reaches both in the same tick, and one request serves both.
 */
const summary = ref<LicensingSummary | null>(null)
const history = ref<LicenseActivationEvent[]>([])
const loading = ref(false)
/**
 * Set when a mutation-triggered history refresh fails. The history list owns the error surface and the
 * retry control, so the mutation does not raise; but the list still has to know its records may no longer
 * match what was just written.
 */
const historyStale = ref(false)
/**
 * Whether the last summary read failed. It is what a mutation reports back on, because a read that fails
 * after the write leaves every surface showing the installation as it was before it.
 */
const summaryStale = ref(false)
let inFlight: Promise<void> | null = null
let historyRequest = 0

/**
 * Bumped on every reset. A read that was already in flight when the session ended finishes after it, and
 * without this its continuation would write the previous session's installation back into the shared state
 * that the next sign-in reads.
 */
let generation = 0

export function useLicensing() {
  const stage = computed(() => summary.value?.stage ?? 'none')

  /**
   * The stage the app-wide notice renders, or null when no notice is due. Reading it off the loaded summary
   * keeps the notice and the panel on one answer rather than two that can disagree.
   */
  const noticeStage = computed<NoticeStage | null>(() =>
    summary.value !== null && isNoticeStage(summary.value.stage) ? summary.value.stage : null,
  )

  /**
   * The author overage the app-wide notice renders, or null when no notice is due. Reading it off the loaded
   * summary keeps the notice and the panel on one answer, the same way the expiry notice's stage does.
   */
  const authorOverageNotice = computed<AuthorOverage | null>(() => {
    const overage = summary.value?.authorOverage ?? null

    return overage !== null && overage.isInOverage ? overage : null
  })

  const edition = computed(() => summary.value?.edition ?? 'community')

  const hasLicense = computed(() => summary.value !== null && summary.value.stage !== 'none')

  /**
   * Loads the state once per session.
   *
   * Only platform administrators can read the endpoint, so callers check that first. A failure leaves the
   * state empty: the notice is not required for the app to work, and an error toast for a background read is
   * not useful. The panel reports its own load failures.
   */
  async function load(force = false): Promise<void> {
    // A request already in flight serves this caller too, forced or not. Two components mount within a tick
    // of each other, and starting a second request would let the first request's completion handler clear
    // the shared handle belonging to the second.
    if (inFlight !== null) {
      await inFlight
      return
    }

    if (!force && summary.value !== null) {
      return
    }

    // Captured before the request, and compared in the continuation: a reset while it was in flight makes
    // this read belong to a session that has ended, and writing its answer would hand the next sign-in the
    // previous installation.
    const readGeneration = generation

    loading.value = true
    inFlight = getLicensingSummary()
      .then((loaded) => {
        if (readGeneration === generation) {
          summary.value = loaded
          summaryStale.value = false
        }
      })
      .catch(() => {
        if (readGeneration === generation) {
          summary.value = null
          summaryStale.value = true
        }
      })
      .finally(() => {
        if (readGeneration === generation) {
          loading.value = false
          inFlight = null
        }
      })

    await inFlight
  }

  /** Loads the recorded license changes. Kept apart from the summary so the panel pays for it only when open. */
  async function loadHistory(): Promise<void> {
    const request = ++historyRequest
    const readGeneration = generation
    // A newer read or a reset supersedes this one, and both its answer and its failure are held to that.
    // Committing records a newer read has already replaced would show the older answer, and raising a failure
    // the caller no longer waits on would have it mark records stale that a newer read has just loaded, or
    // raise the stale marker in the session after a sign-out.
    const isCurrentRead = (): boolean => readGeneration === generation && request === historyRequest

    let loaded: LicenseActivationEvent[]
    try {
      loaded = await getLicenseActivationHistory()
    } catch (error) {
      if (isCurrentRead()) {
        throw error
      }

      return
    }

    if (isCurrentRead()) {
      history.value = loaded
      historyStale.value = false
    }
  }

  /**
   * Every mutation below re-reads the summary rather than storing what the endpoint returned. The read is the
   * only response that carries the effective ceilings and the current counts beside the limits, so storing a
   * mutation's answer instead would leave the panel reporting every ceiling as unreported. Re-reading also
   * picks up the lifecycle stage the new license puts the installation in.
   *
   * Neither read that follows can fail the mutation. The license has already been written when they run, so
   * raising here would tell an operator the activation failed while the installation runs on the new license,
   * and the operator would submit the same document again. A failed summary read is reported on the returned
   * outcome instead; the history list reports and retries its own read.
   */
  async function activate(token: string): Promise<MutationOutcome> {
    try {
      await activateLicense(token)
    } catch (error) {
      // A license that was stored but whose state could not be read back is an activation that succeeded.
      // The reload below reads that state again and decides on its own whether it is current.
      if (!(error instanceof LicenseStateUnavailableError)) {
        throw error
      }
    }

    return await settleAfterMutation()
  }

  async function remove(): Promise<MutationOutcome> {
    await removeLicense()

    return await settleAfterMutation()
  }

  /** Re-reads what a committed mutation changed, and reports whether the summary read succeeded. */
  async function settleAfterMutation(): Promise<MutationOutcome> {
    await reload()
    await refreshHistoryQuietly()

    return { isSummaryStale: summaryStale.value }
  }

  async function refreshHistoryQuietly(): Promise<void> {
    try {
      await loadHistory()
    } catch {
      // The mutation has already committed, so the failure here cannot fail it. The history list owns the
      // error surface and the retry control, but it has to know the records may no longer match what was
      // just written.
      historyStale.value = true
    }
  }

  async function setOverride(key: string, overrideState: PremiumCapabilityOverrideStateValue): Promise<void> {
    await setCapabilityOverride(key, overrideState)
    await reload()
  }

  /** Re-reads the summary, waiting out any read already running so the answer is the one taken last. */
  async function reload(): Promise<void> {
    if (inFlight !== null) {
      await inFlight
    }

    await load(true)
  }

  /**
   * Clears the cached state. Called when the app chrome unmounts, which happens on sign-out, so the next
   * session does not render the previous session's installation state.
   */
  function reset(): void {
    generation += 1
    historyRequest += 1
    summary.value = null
    history.value = []
    historyStale.value = false
    summaryStale.value = false
    loading.value = false
    inFlight = null
  }

  return {
    summary,
    history,
    historyStale,
    summaryStale,
    loading,
    stage,
    noticeStage,
    authorOverageNotice,
    edition,
    hasLicense,
    load,
    reload,
    loadHistory,
    activate,
    remove,
    setOverride,
    reset,
  }
}
