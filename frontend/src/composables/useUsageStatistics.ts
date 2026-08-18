// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, ref } from 'vue'
import {
  dismissUsageStatisticsNotice,
  getUsageStatisticsSettings,
  recordUsageStatisticsNoticeShown,
  sendUsageStatisticsNow,
  setUsageStatisticsEnabled,
  type UsageStatisticsSendDecision,
  type UsageStatisticsSettings,
} from '@/services/usageStatisticsService'

/**
 * Shared anonymous usage statistics state.
 *
 * Three surfaces read it: the consent notice, the settings page and the update badge in the header. Holding
 * it once means a dismissal reaches all three in the same tick, and one request serves all of them.
 */
const settings = ref<UsageStatisticsSettings | null>(null)
const loading = ref(false)
let inFlight: Promise<void> | null = null

export function useUsageStatistics() {
  /** True while the consent notice still has to be shown. */
  const noticeRequired = computed(() => settings.value?.noticeRequired === true)

  const advisories = computed(() => settings.value?.update.advisories ?? [])

  /** True when the running version is behind, or an advisory applies to it. */
  const updateAvailable = computed(
    () => settings.value?.update.updateAvailable === true || advisories.value.length > 0,
  )

  /**
   * Loads the state once per session.
   *
   * Only platform administrators can read the endpoint, so callers check that first. A failure is swallowed:
   * none of the three surfaces is required, and an error toast for a background read is not useful.
   */
  async function load(force = false): Promise<void> {
    // A request already in flight serves this caller too, forced or not. Two components mount within a tick
    // of each other, and starting a second request would let the first request's completion handler clear the
    // shared handle belonging to the second.
    if (inFlight !== null) {
      await inFlight
      return
    }

    if (!force && settings.value !== null) {
      return
    }

    loading.value = true
    inFlight = getUsageStatisticsSettings()
      .then((loaded) => {
        settings.value = loaded
      })
      .catch(() => {
        settings.value = null
      })
      .finally(() => {
        loading.value = false
        inFlight = null
      })

    await inFlight
  }

  async function setEnabled(enabled: boolean): Promise<void> {
    settings.value = await setUsageStatisticsEnabled(enabled)
  }

  /** Runs a send cycle now and returns its decision, so the caller can report the outcome. */
  async function sendNow(): Promise<UsageStatisticsSendDecision> {
    const result = await sendUsageStatisticsNow()
    settings.value = result.settings

    return result.decision
  }

  /** Records that the notice was rendered, which is what opens the send gate in a community installation. */
  async function recordNoticeShown(): Promise<void> {
    settings.value = await recordUsageStatisticsNoticeShown()
  }

  async function dismissNotice(): Promise<void> {
    settings.value = await dismissUsageStatisticsNotice()
  }

  /**
   * Clears the cached state. Called when the app chrome unmounts, which happens on sign-out, so the next
   * session does not render the previous session's installation state.
   */
  function reset(): void {
    settings.value = null
    loading.value = false
    inFlight = null
  }

  return {
    settings,
    loading,
    noticeRequired,
    updateAvailable,
    advisories,
    load,
    setEnabled,
    sendNow,
    recordNoticeShown,
    dismissNotice,
    reset,
  }
}
