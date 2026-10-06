// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, onBeforeUnmount, onMounted, ref, watch, type ComputedRef, type Ref } from 'vue'
import { navigateInspection, nearestInspection, type ChartInspectionEntry, type ChartPointSelection } from '../reviewerPerformanceChartInspection'
import { CHART_WIDTH, PLOT_LEFT, PLOT_RIGHT, PLOT_TOP, UNAVAILABLE_BOTTOM_MARGIN } from '../reviewerPerformanceChartGeometry'

const HOVER_EXIT_DELAY_MS = 160
const POINTER_HIT_MARGIN = 12
const NAVIGATION_KEYS = ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End']

interface InspectionOptions {
  entries: ComputedRef<ChartInspectionEntry[]>
  root: Ref<SVGSVGElement | undefined>
  height: () => number
  selectedDate: () => string | undefined
  focusSeries: () => string | undefined
  resetWhen: () => unknown[]
  select: (point: ChartPointSelection) => void
}

export function useReviewerPerformanceChartInspection(options: InspectionOptions) {
  const key = ref<string>()
  const mode = ref<'pointer' | 'keyboard' | 'touch'>('pointer')
  const host = ref<HTMLElement>()
  const canvasBounds = ref<DOMRect>()
  let dismissedPointerKey: string | undefined
  let pointerFocus = false
  let hideTimer: ReturnType<typeof setTimeout> | undefined
  const active = computed(() => options.entries.value.find((item) => item.key === key.value))
  const anchor = computed(() => {
    const bounds = canvasBounds.value
    const horizontalScale = (bounds?.width ?? 0) / CHART_WIDTH
    const verticalScale = (bounds?.height ?? 0) / options.height()
    return {
      x: (bounds?.left ?? 0) + (active.value?.x ?? 0) * horizontalScale,
      y: (bounds?.top ?? 0) + (active.value?.y ?? 0) * verticalScale,
    }
  })

  function keep(): void {
    clearTimeout(hideTimer)
    hideTimer = undefined
  }

  function dismiss(): void {
    keep()
    key.value = undefined
  }

  function leave(): void {
    keep()
    const keyboardFocusElsewhere = mode.value === 'keyboard' && document.activeElement !== options.root.value
    if (mode.value === 'pointer' || keyboardFocusElsewhere) {
      hideTimer = setTimeout(dismiss, HOVER_EXIT_DELAY_MS)
    }
  }

  function leaveCanvas(): void {
    dismissedPointerKey = undefined
    leave()
  }

  function show(item: ChartInspectionEntry, nextMode: typeof mode.value): void {
    keep()
    mode.value = nextMode
    key.value = item.key
    if (nextMode === 'keyboard') {
      reveal(item)
    }
    canvasBounds.value = options.root.value?.getBoundingClientRect()
  }

  function reveal(item: ChartInspectionEntry): void {
    const canvas = options.root.value?.parentElement
    const visible = canvas?.getBoundingClientRect()
    const drawing = options.root.value?.getBoundingClientRect()
    if (!canvas || !visible?.width || !drawing?.width) {
      return
    }
    const pointX = drawing.left + item.x * drawing.width / CHART_WIDTH
    const minimumX = visible.left + POINTER_HIT_MARGIN
    const maximumX = visible.right - POINTER_HIT_MARGIN
    if (pointX < minimumX) {
      canvas.scrollLeft += pointX - minimumX
    } else if (pointX > maximumX) {
      canvas.scrollLeft += pointX - maximumX
    }
  }

  function hover(item: ChartInspectionEntry, event: PointerEvent): void {
    if (event.pointerType === 'touch' || item.key === dismissedPointerKey) {
      return
    }
    dismissedPointerKey = undefined
    show(item, 'pointer')
  }

  function pointAt(event: MouseEvent): ChartInspectionEntry | undefined {
    const bounds = options.root.value?.getBoundingClientRect()
    if (!bounds?.width || !bounds.height) {
      return undefined
    }
    const point = {
      x: (event.clientX - bounds.left) * CHART_WIDTH / bounds.width,
      y: (event.clientY - bounds.top) * options.height() / bounds.height,
    }
    const outsideHorizontal = point.x < PLOT_LEFT - POINTER_HIT_MARGIN || point.x > PLOT_RIGHT + POINTER_HIT_MARGIN
    const outsideVertical = point.y < PLOT_TOP - POINTER_HIT_MARGIN ||
      point.y > options.height() - UNAVAILABLE_BOTTOM_MARGIN + POINTER_HIT_MARGIN
    if (outsideHorizontal || outsideVertical) {
      return undefined
    }
    return nearestInspection(options.entries.value, point)
  }

  function pointerMove(event: PointerEvent): void {
    if (event.pointerType === 'touch') {
      return
    }
    const item = pointAt(event)
    if (!item) {
      leave()
      return
    }
    hover(item, event)
  }

  function selectEntry(item: ChartInspectionEntry, event?: MouseEvent): void {
    let nextMode = mode.value
    if (event) {
      nextMode = (event as PointerEvent).pointerType === 'touch' ? 'touch' : 'pointer'
    }
    dismissedPointerKey = undefined
    show(item, nextMode)
    options.select({ seriesId: item.seriesId, date: item.date })
  }

  function pointerSelect(event: MouseEvent): void {
    const item = pointAt(event)
    if (item) {
      selectEntry(item, event)
    }
  }

  function preferred(): ChartInspectionEntry | undefined {
    const date = options.selectedDate()
    const selected = options.entries.value.find((item) => item.date === date && item.seriesId === options.focusSeries())
    if (selected || date) {
      return selected
    }
    return options.entries.value[0]
  }

  function focusPointer(): void {
    pointerFocus = true
    options.root.value?.focus({ preventScroll: true })
    pointerFocus = false
  }

  function focus(): void {
    if (pointerFocus) {
      return
    }
    const item = preferred()
    if (item) {
      show(item, 'keyboard')
    }
  }

  function blur(event: FocusEvent): void {
    const focusInsideDetails = event.relatedTarget instanceof Node && host.value?.contains(event.relatedTarget)
    if (!focusInsideDetails) {
      dismiss()
    }
  }

  function escape(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      if (key.value) {
        dismissedPointerKey = key.value
      }
      dismiss()
    }
  }

  function keydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      escape(event)
      return
    }
    if (active.value && host.value?.isConnected && (event.key === 'PageDown' || event.key === 'PageUp')) {
      event.preventDefault()
      const direction = event.key === 'PageDown' ? 1 : -1
      host.value.scrollTop += direction * host.value.clientHeight
      return
    }
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      if (active.value) {
        selectEntry(active.value)
      }
      return
    }
    if (!NAVIGATION_KEYS.includes(event.key)) {
      return
    }
    event.preventDefault()
    const current = active.value ?? preferred() ?? options.entries.value[0]
    if (!current) {
      return
    }
    let destination = current
    if (active.value || event.key === 'Home' || event.key === 'End') {
      destination = navigateInspection(options.entries.value, current, event.key)
    }
    dismissedPointerKey = undefined
    show(destination, 'keyboard')
  }

  function outside(event: PointerEvent): void {
    if (!(event.target instanceof Node)) {
      return
    }
    const insideChart = options.root.value?.contains(event.target)
    const insideDetails = host.value?.contains(event.target)
    if (!insideChart && !insideDetails) {
      dismiss()
    }
  }

  function scroll(event: Event): void {
    if (event.target instanceof Node && host.value?.contains(event.target)) {
      return
    }
    // Focusing an offscreen chart can scroll its container before details are read.
    if (mode.value === 'keyboard' && document.activeElement === options.root.value) {
      canvasBounds.value = options.root.value?.getBoundingClientRect()
      return
    }
    dismiss()
  }

  watch(options.resetWhen, dismiss)
  onMounted(() => {
    document.addEventListener('keydown', escape)
    document.addEventListener('pointerdown', outside)
    window.addEventListener('resize', dismiss)
    window.addEventListener('scroll', scroll, true)
  })
  onBeforeUnmount(() => {
    keep()
    document.removeEventListener('keydown', escape)
    document.removeEventListener('pointerdown', outside)
    window.removeEventListener('resize', dismiss)
    window.removeEventListener('scroll', scroll, true)
  })

  return {
    active,
    anchor,
    mode,
    host,
    hover,
    pointerMove,
    pointerSelect,
    selectEntry,
    focusPointer,
    focus,
    blur,
    keydown,
    dismiss,
    keep,
    leave,
    leaveCanvas,
  }
}
