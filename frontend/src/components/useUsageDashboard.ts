// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

// State + data-loading orchestration for UsageDashboard.vue. Pure helpers and
// chart-data builders live in usageDashboardFormatters.ts; the SFC stays thin.

import { computed, onMounted, ref } from 'vue'
import { useSession } from '@/composables/useSession'
import { getClientTokenUsage } from '@/services/clientTokenUsageService'
import {
  exportProCursorTokenUsageCsv,
  getProCursorClientTokenUsage,
  getProCursorTopSources,
} from '@/services/proCursorService'
import type { ClientTokenUsageResponse } from '@/types/clientTokenUsage'
import type {
  ProCursorTokenUsageGroupBy,
  ProCursorTokenUsageGranularity,
  ProCursorTokenUsageResponse,
  ProCursorTopSourceUsageDto,
  ProCursorTopSourcesPeriod,
  ProCursorTopSourcesResponse,
} from '@/types/proCursorTokenUsage'
import {
  PERIOD_PRESETS,
  buildProCursorChartData,
  buildReviewChartData,
  createChartOptions,
  daysAgoStr,
  formatDateTime,
  getInclusiveDayCount,
  todayStr,
} from '@/components/usageDashboardFormatters'

export function useUsageDashboard(props: { clientId: string }) {
  const { getAccessToken } = useSession()

  const fromDate = ref(daysAgoStr(29))
  const toDate = ref(todayStr())

  const loadingReview = ref(false)
  const reviewError = ref('')
  const reviewUsage = ref<ClientTokenUsageResponse | null>(null)

  const loadingProCursor = ref(false)
  const exportingProCursor = ref(false)
  const proCursorError = ref('')
  const proCursorUsage = ref<ProCursorTokenUsageResponse | null>(null)
  const proCursorTopSources = ref<ProCursorTopSourcesResponse | null>(null)
  const proCursorGranularity = ref<ProCursorTokenUsageGranularity>('daily')
  const proCursorGroupBy = ref<ProCursorTokenUsageGroupBy>('source')
  const selectedProCursorPreset = ref<ProCursorTopSourcesPeriod | 'custom'>('30d')

  const periodPresets = PERIOD_PRESETS
  const totalInputTokens = computed(() => reviewUsage.value?.totalInputTokens ?? 0)
  const totalOutputTokens = computed(() => reviewUsage.value?.totalOutputTokens ?? 0)
  const totalCachedInputTokens = computed(() => reviewUsage.value?.totalCachedInputTokens ?? 0)
  const totalReasoningTokens = computed(() => reviewUsage.value?.totalReasoningTokens ?? 0)
  const hasReviewSamples = computed(() => (reviewUsage.value?.samples.length ?? 0) > 0)
  const proCursorTotals = computed(() => proCursorUsage.value?.totals)
  const proCursorTotalTokens = computed(() => proCursorTotals.value?.totalTokens ?? 0)
  const proCursorEstimatedCost = computed(() => proCursorTotals.value?.estimatedCostUsd ?? null)
  const proCursorEstimatedEvents = computed(() => proCursorTotals.value?.estimatedEventCount ?? 0)
  const resolvedTopSources = computed<ProCursorTopSourceUsageDto[]>(() => {
    return proCursorTopSources.value?.items ?? proCursorUsage.value?.topSources ?? []
  })
  const hasProCursorData = computed(() => {
    return proCursorTotalTokens.value > 0 || (proCursorUsage.value?.series?.length ?? 0) > 0 || resolvedTopSources.value.length > 0
  })
  const showEstimatedBanner = computed(() => Boolean(proCursorUsage.value?.includesEstimatedUsage) && proCursorEstimatedEvents.value > 0)
  const showGapFillBanner = computed(() => Boolean(proCursorUsage.value?.includesGapFilledEvents))
  const inclusiveDayCount = computed(() => getInclusiveDayCount(fromDate.value, toDate.value))
  const currentPeriodLabel = computed(() => {
    return selectedProCursorPreset.value === 'custom' ? `${inclusiveDayCount.value}d` : selectedProCursorPreset.value
  })
  const lastRollupCompletedLabel = computed(() => formatDateTime(proCursorUsage.value?.lastRollupCompletedAtUtc))

  const reviewChartData = computed(() => buildReviewChartData(reviewUsage.value, hasReviewSamples.value))
  const reviewChartOptions = createChartOptions()

  const proCursorChartData = computed(() => buildProCursorChartData(proCursorUsage.value, proCursorGroupBy.value))
  const proCursorChartOptions = createChartOptions()

  function markCustomRange(): void {
    selectedProCursorPreset.value = 'custom'
  }

  function applyProCursorPeriod(period: ProCursorTopSourcesPeriod): void {
    selectedProCursorPreset.value = period
    const days = Number.parseInt(period, 10)
    fromDate.value = daysAgoStr(Math.max(days - 1, 0))
    toDate.value = todayStr()
    void handleRefresh()
  }

  async function loadReviewUsage(): Promise<void> {
    loadingReview.value = true
    reviewError.value = ''

    try {
      const token = getAccessToken()
      if (!token) {
        reviewUsage.value = null
        reviewError.value = 'Not authenticated.'
        return
      }

      reviewUsage.value = await getClientTokenUsage(props.clientId, fromDate.value, toDate.value, token)
    } catch (error) {
      reviewUsage.value = null
      reviewError.value = error instanceof Error ? error.message : 'Failed to load usage data.'
    } finally {
      loadingReview.value = false
    }
  }

  async function loadProCursorUsage(): Promise<void> {
    loadingProCursor.value = true
    proCursorError.value = ''

    try {
      const [usage, topSources] = await Promise.all([
        getProCursorClientTokenUsage(props.clientId, {
          from: fromDate.value,
          to: toDate.value,
          granularity: proCursorGranularity.value,
          groupBy: proCursorGroupBy.value,
        }),
        getProCursorTopSources(props.clientId, currentPeriodLabel.value, 5),
      ])

      proCursorUsage.value = usage
      proCursorTopSources.value = topSources
    } catch (error) {
      proCursorUsage.value = null
      proCursorTopSources.value = null
      proCursorError.value = error instanceof Error ? error.message : 'Failed to load ProCursor usage.'
    } finally {
      loadingProCursor.value = false
    }
  }

  async function handleRefresh(): Promise<void> {
    await Promise.all([loadReviewUsage(), loadProCursorUsage()])
  }

  async function handleExportProCursor(): Promise<void> {
    if (exportingProCursor.value || !hasProCursorData.value) {
      return
    }

    exportingProCursor.value = true

    try {
      const csv = await exportProCursorTokenUsageCsv(props.clientId, {
        from: fromDate.value,
        to: toDate.value,
      })

      const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' })
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `procursor-usage-${props.clientId}-${fromDate.value}-to-${toDate.value}.csv`
      link.click()
      URL.revokeObjectURL(url)
    } catch (error) {
      proCursorError.value = error instanceof Error ? error.message : 'Failed to export ProCursor usage CSV.'
    } finally {
      exportingProCursor.value = false
    }
  }

  onMounted(() => {
    void handleRefresh()
  })

  return {
    fromDate,
    toDate,
    loadingReview,
    reviewError,
    loadingProCursor,
    exportingProCursor,
    proCursorError,
    proCursorGranularity,
    proCursorGroupBy,
    selectedProCursorPreset,
    periodPresets,
    totalInputTokens,
    totalOutputTokens,
    totalCachedInputTokens,
    totalReasoningTokens,
    hasReviewSamples,
    proCursorTotalTokens,
    proCursorEstimatedCost,
    proCursorEstimatedEvents,
    resolvedTopSources,
    hasProCursorData,
    showEstimatedBanner,
    showGapFillBanner,
    currentPeriodLabel,
    lastRollupCompletedLabel,
    reviewChartData,
    reviewChartOptions,
    proCursorChartData,
    proCursorChartOptions,
    markCustomRange,
    applyProCursorPeriod,
    loadReviewUsage,
    loadProCursorUsage,
    handleRefresh,
    handleExportProCursor,
  }
}
