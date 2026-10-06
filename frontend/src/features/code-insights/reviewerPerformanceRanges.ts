// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

export type PerformanceMetric = 'precision' | 'recall' | 'f1'
type PartialPoint = {
  date?: string
  score?: { summary?: Partial<Record<PerformanceMetric, { median?: number } | null>> }
}
type PartialScenario = {
  id?: string | null
  precision?: number | null
  recall?: number | null
  f1?: number | null
}

export function connectedSegments<T extends PartialPoint>(
  points: readonly T[],
  metric: PerformanceMetric,
): T[][] {
  const segments: T[][] = []
  let current: T[] = []
  for (const point of points) {
    if (point.score?.summary?.[metric]?.median == null) {
      if (current.length) segments.push(current)
      current = []
    } else current.push(point)
  }
  if (current.length) segments.push(current)
  return segments
}

const SERIES_COLORS = [
  'var(--chart-1)',
  'var(--chart-2)',
  'var(--chart-3)',
  'var(--chart-4)',
  'var(--color-suggestion)',
  'var(--color-accent)',
  'var(--color-success)',
  'var(--color-warning)',
]
export function stableSeriesColor(id: string): string {
  let hash = 2166136261
  for (const character of id) {
    hash ^= character.charCodeAt(0)
    hash = Math.imul(hash, 16777619)
  }
  return SERIES_COLORS[(hash >>> 0) % SERIES_COLORS.length]!
}

export function pairedDelta(
  a: readonly PartialScenario[],
  b: readonly PartialScenario[],
  metric: PerformanceMetric,
): { minimum: number; median: number; maximum: number; pairs: number } | null {
  const left = new Map(a.map((item) => [item.id, item[metric]]))
  const deltas = b
    .flatMap((item) => {
      const previous = left.get(item.id)
      const next = item[metric]
      return previous == null || next == null ? [] : [next - previous]
    })
    .sort((a, b) => a - b)
  if (!deltas.length) return null
  const middle = (deltas.length - 1) / 2
  return {
    minimum: deltas[0]!,
    median: (deltas[Math.floor(middle)]! + deltas[Math.ceil(middle)]!) / 2,
    maximum: deltas.at(-1)!,
    pairs: deltas.length,
  }
}

export function annotationLayout<T extends { id: string; anchorY: number }>(
  items: readonly T[],
  top: number,
  bottom: number,
): (T & { labelY: number })[] {
  const sorted = items
    .map((item) => ({ ...item, labelY: item.anchorY }))
    .sort((a, b) => a.anchorY - b.anchorY)
  const gap = Math.min(40, (bottom - top) / Math.max(1, sorted.length))
  for (let index = 0; index < sorted.length; index++)
    sorted[index]!.labelY = Math.max(
      top,
      sorted[index]!.anchorY,
      index ? sorted[index - 1]!.labelY + gap : top,
    )
  if ((sorted.at(-1)?.labelY ?? bottom) > bottom) {
    sorted.at(-1)!.labelY = bottom
    for (let index = sorted.length - 2; index >= 0; index--)
      sorted[index]!.labelY = Math.min(sorted[index]!.labelY, sorted[index + 1]!.labelY - gap)
  }
  return sorted
}
export const metricLabel = (metric: PerformanceMetric): string =>
  metric === 'f1' ? 'F1' : metric === 'precision' ? 'Precision' : 'Observed recall'
export const percent = (value: number | null | undefined): string =>
  value == null ? 'Unavailable' : `${(value * 100).toFixed(1)}%`
