// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import type { PerformanceMatrixCell, PerformanceRange } from '@/services/reviewerPerformanceService'
import { metricLabel, percent, type PerformanceMetric } from './reviewerPerformanceRanges'

export interface ExplorerAxisItem {
  id: string
  label: string
}

export interface ExplorerMatrixEntry {
  column: ExplorerAxisItem
  source: PerformanceMatrixCell | undefined
  median: string
  range: string
  publications: number
  measurementDescription: string
  accessibleLabel: string
}

interface ExplorerMatrixRow extends ExplorerAxisItem {
  cells: ExplorerMatrixEntry[]
}

export interface ExplorerMatrix {
  columns: ExplorerAxisItem[]
  rows: ExplorerMatrixRow[]
}

export const EXPLORER_DIMENSIONS: readonly ExplorerAxisItem[] = [
  { id: 'model', label: 'Model' },
  { id: 'type', label: 'Finding type' },
  { id: 'qualifier', label: 'Kind (qualifier)' },
  { id: 'client', label: 'Client' },
  { id: 'repository', label: 'Repository' },
]

export function dimensionLabel(dimension: string): string {
  return EXPLORER_DIMENSIONS.find((item) => item.id === dimension)?.label ?? dimension
}

export function cellRange(
  cell: PerformanceMatrixCell,
  metric: PerformanceMetric,
): PerformanceRange | undefined {
  return cell.measurement?.score?.summary?.[metric]
}

function axisItems(
  cells: readonly PerformanceMatrixCell[],
  axis: 'row' | 'column',
): ExplorerAxisItem[] {
  const items = cells.map((cell) => ({
    id: (axis === 'row' ? cell.rowId : cell.columnId) ?? '',
    label: (axis === 'row' ? cell.rowLabel : cell.columnLabel) ?? '',
  }))
  return [...new Map(items.map((item) => [item.id, item])).values()]
}

function prepareMatrixEntry(
  source: PerformanceMatrixCell | undefined,
  row: ExplorerAxisItem,
  column: ExplorerAxisItem,
  metric: PerformanceMetric,
): ExplorerMatrixEntry {
  const range = source ? cellRange(source, metric) : undefined
  const median = percent(range?.median)
  const fullRange = range
    ? `${percent(range.minimum)}–${percent(range.maximum)}`
    : 'Evidence unavailable'
  const middleHalf =
    range?.firstQuartile != null && range.thirdQuartile != null
      ? `${percent(range.firstQuartile)}–${percent(range.thirdQuartile)}`
      : 'unavailable'
  const publications = source?.measurement?.score?.counts?.outcomes?.total ?? 0
  const measurementDescription = range
    ? `${metricLabel(metric)} median ${median}, full range ${fullRange}, middle half ${middleHalf}; ${publications} published.`
    : `${metricLabel(metric)} unavailable, middle half ${middleHalf}; ${publications} published.`
  return {
    column,
    source,
    median,
    range: fullRange,
    publications,
    measurementDescription,
    accessibleLabel: `Filter timeline to ${row.label}, ${column.label}. ${measurementDescription}`,
  }
}

export function prepareExplorerMatrix(
  cells: readonly PerformanceMatrixCell[],
  metric: PerformanceMetric,
): ExplorerMatrix {
  const rows = axisItems(cells, 'row')
  const columns = axisItems(cells, 'column')

  return {
    columns,
    rows: rows.map((row) => ({
      ...row,
      cells: columns.map((column) => {
        const source = cells.find((cell) => cell.rowId === row.id && cell.columnId === column.id)
        return prepareMatrixEntry(source, row, column, metric)
      }),
    })),
  }
}
