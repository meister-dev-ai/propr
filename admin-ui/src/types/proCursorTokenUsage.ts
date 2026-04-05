// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { components } from '@/services/generated/openapi'

export type ProCursorTokenUsageResponse = components['schemas']['ProCursorTokenUsageResponse']
export type ProCursorTokenUsageTotalsDto = components['schemas']['ProCursorTokenUsageTotalsDto']
export type ProCursorTokenUsageSeriesPointDto = components['schemas']['ProCursorTokenUsageSeriesPointDto']
export type ProCursorTokenUsageBreakdownItemDto = components['schemas']['ProCursorTokenUsageBreakdownItemDto']
export type ProCursorTopSourceUsageDto = components['schemas']['ProCursorTopSourceUsageDto']
export type ProCursorTopSourcesResponse = components['schemas']['ProCursorTopSourcesResponse']
export type ProCursorSourceModelUsageDto = components['schemas']['ProCursorSourceModelUsageDto']
export type ProCursorSourceTokenUsageResponse = components['schemas']['ProCursorSourceTokenUsageResponse']
export type ProCursorTokenUsageEventDto = components['schemas']['ProCursorTokenUsageEventDto']
export type ProCursorTokenUsageEventsResponse = components['schemas']['ProCursorTokenUsageEventsResponse']
export type ProCursorTokenUsageFreshnessResponse = components['schemas']['ProCursorTokenUsageFreshnessResponse']
export type ProCursorTokenUsageGranularity = components['schemas']['ProCursorTokenUsageGranularity']
export type ProCursorTokenUsageRebuildRequest = components['schemas']['ProCursorTokenUsageRebuildRequest']
export type ProCursorTokenUsageRebuildResponse = components['schemas']['ProCursorTokenUsageRebuildResponse']
export type ProCursorTokenUsageGroupBy = 'source' | 'model'
export type ProCursorTopSourcesPeriod = '30d' | '90d' | '365d'

export interface ProCursorClientUsageQuery {
  from: string
  to: string
  granularity?: ProCursorTokenUsageGranularity
  groupBy?: ProCursorTokenUsageGroupBy
}

export interface ProCursorExportQuery {
  from: string
  to: string
  sourceId?: string
}

export interface ProCursorSourceUsageQuery {
  period?: string
  from?: string
  to?: string
  granularity?: ProCursorTokenUsageGranularity
}
