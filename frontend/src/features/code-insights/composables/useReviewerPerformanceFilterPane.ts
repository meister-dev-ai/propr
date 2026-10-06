// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { nextTick, onBeforeUnmount, ref, useId, watch, type ComputedRef } from 'vue'

export function controlOwnsFocus(control: Element, focused: Element): boolean {
  for (const attribute of ['aria-controls', 'aria-owns']) {
    const referencedIds = (control.getAttribute(attribute) ?? '').split(/\s+/)
    for (const referencedId of referencedIds) {
      const ownedElement = control.ownerDocument.getElementById(referencedId)
      if (ownedElement?.contains(focused)) {
        return true
      }
    }
  }
  return false
}

function paneOwnsFocus(pane: HTMLElement | null): boolean {
  const focused = pane?.ownerDocument.activeElement
  if (!pane || !focused) {
    return false
  }
  if (pane.contains(focused)) {
    return true
  }

  // Selector menus can render outside the pane while retaining their control's ARIA reference.
  for (const control of pane.querySelectorAll('[aria-controls], [aria-owns]')) {
    if (controlOwnsFocus(control, focused)) {
      return true
    }
  }
  return false
}

export function useReviewerPerformanceFilterPane(viewCount: ComputedRef<number>) {
  const media = window.matchMedia('(max-width: 960px)')
  const narrow = ref(media.matches)
  const open = ref(!media.matches)
  const selectedView = ref(0)
  const trigger = ref<HTMLButtonElement | null>(null)
  const interactionRevision = ref(0)
  const id = `performance-filter-pane-${useId()}`

  async function hide(restoreFocus: boolean): Promise<void> {
    open.value = false
    await nextTick()
    if (restoreFocus) {
      trigger.value?.focus()
    }
  }

  async function close(restoreFocus = true): Promise<void> {
    interactionRevision.value++
    await hide(restoreFocus)
  }

  function toggle(): void {
    if (open.value) {
      void close()
    } else {
      interactionRevision.value++
      open.value = true
    }
  }

  function selectView(index: number): void {
    selectedView.value = Math.min(Math.max(index, 0), Math.max(viewCount.value - 1, 0))
  }

  async function revealChart(expectedRevision: number): Promise<boolean> {
    if (expectedRevision !== interactionRevision.value) {
      return false
    }
    if (narrow.value) {
      await hide(false)
    }
    await nextTick()
    return expectedRevision === interactionRevision.value
  }

  function resize(event: MediaQueryListEvent): void {
    narrow.value = event.matches
    if (event.matches) {
      const pane = document.getElementById(id)
      void close(paneOwnsFocus(pane))
    }
  }

  watch(viewCount, () => selectView(selectedView.value))
  media.addEventListener('change', resize)
  onBeforeUnmount(() => media.removeEventListener('change', resize))

  return {
    id,
    open,
    selectedView,
    trigger,
    interactionRevision,
    toggle,
    close,
    selectView,
    revealChart,
  }
}
