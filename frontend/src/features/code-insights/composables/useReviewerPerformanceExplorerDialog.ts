// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { onMounted, ref } from 'vue'

function focusableControls(dialog: HTMLDialogElement): HTMLElement[] {
  const candidates = dialog.querySelectorAll<HTMLElement>(
    'button,input,select,textarea,a[href],[tabindex]',
  )
  return [...candidates].filter(
    (element) =>
      element.tabIndex >= 0 &&
      !element.matches(':disabled,[type="hidden"]') &&
      !element.closest('[hidden]'),
  )
}

export function useReviewerPerformanceExplorerDialog() {
  const dialog = ref<HTMLDialogElement>()

  function preventMenuCancel(event: KeyboardEvent): void {
    if (event.key !== 'Escape' || !dialog.value?.querySelector('.v-menu.v-overlay--active')) {
      return
    }

    // Prevent native dialog cancellation while Vuetify receives Escape and closes its menu.
    event.preventDefault()
  }

  function trapFocus(event: KeyboardEvent): void {
    if (event.key !== 'Tab' || !dialog.value) return

    const controls = focusableControls(dialog.value)
    const first = controls[0]
    const last = controls.at(-1)
    const active = document.activeElement
    if (!first || !last) {
      event.preventDefault()
      dialog.value.focus()
      return
    }

    const focusIsOutside = !controls.includes(active as HTMLElement)
    if (event.shiftKey && (active === first || focusIsOutside)) {
      event.preventDefault()
      last.focus()
    } else if (!event.shiftKey && (active === last || focusIsOutside)) {
      event.preventDefault()
      first.focus()
    }
  }

  onMounted(() => {
    if (dialog.value?.showModal) dialog.value.showModal()
    else dialog.value?.setAttribute('open', '')
  })

  return { dialog, trapFocus, preventMenuCancel }
}
