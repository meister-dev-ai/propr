// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { nextTick, onBeforeUnmount, ref, watch, type Ref } from 'vue'

function focusableControls(container: HTMLElement): HTMLElement[] {
  const candidates = container.querySelectorAll<HTMLElement>('button,input,select,textarea,a[href],[tabindex]')
  return [...candidates].filter(
    (element) =>
      element.tabIndex >= 0 &&
      !element.matches(':disabled,[type="hidden"]') &&
      !element.closest('[hidden]'),
  )
}

/**
 * The keyboard behaviour of a modal dialog on a native `<dialog>` element. When the dialog opens, it opens as a
 * modal, which makes the rest of the page inert in the browser, and moves the focus to `initialFocus` or to the first
 * control. While it is open, Tab and Shift+Tab stay inside the dialog and Escape closes it. When it closes, the focus
 * returns to the element that had it before the dialog opened.
 *
 * The component renders the `<dialog>` with the `dialog` ref while `isOpen` is true, binds `onKeydown` to its
 * `keydown` event and `onCancel` to its `cancel` event, and names it with `aria-labelledby`.
 */
export function useModalDialog(
  isOpen: () => boolean,
  close: () => void,
  initialFocus?: Ref<HTMLElement | null | undefined>,
) {
  const dialog = ref<HTMLDialogElement>()
  let returnFocusTo: HTMLElement | null = null

  watch(
    isOpen,
    async (open) => {
      if (!open) {
        restoreFocus()
        return
      }

      returnFocusTo = document.activeElement instanceof HTMLElement ? document.activeElement : null
      await nextTick()
      const element = dialog.value
      if (!element) {
        return
      }

      if (typeof element.showModal === 'function') {
        if (!element.open) {
          element.showModal()
        }
      } else {
        element.setAttribute('open', '')
      }

      ;(initialFocus?.value ?? focusableControls(element)[0] ?? element).focus()
    },
    { immediate: true },
  )

  onBeforeUnmount(restoreFocus)

  function restoreFocus(): void {
    // A modal dialog keeps the rest of the page inert until it is closed, and an inert element takes no focus.
    const element = dialog.value
    if (element?.open && typeof element.close === 'function') {
      element.close()
    }

    const target = returnFocusTo
    returnFocusTo = null
    if (target?.isConnected) {
      target.focus()
    }
  }

  function onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault()
      close()
      return
    }

    if (event.key !== 'Tab' || !dialog.value) {
      return
    }

    const controls = focusableControls(dialog.value)
    const first = controls[0]
    const last = controls.at(-1)
    if (!first || !last) {
      event.preventDefault()
      dialog.value.focus()
      return
    }

    const active = document.activeElement
    const focusIsOutside = !controls.includes(active as HTMLElement)
    if (event.shiftKey && (active === first || focusIsOutside)) {
      event.preventDefault()
      last.focus()
    } else if (!event.shiftKey && (active === last || focusIsOutside)) {
      event.preventDefault()
      first.focus()
    }
  }

  // The browser sends `cancel` when it closes a modal dialog on Escape. The dialog closes through the state of the
  // component, so the browser does not close it on its own.
  function onCancel(event: Event): void {
    event.preventDefault()
    close()
  }

  return { dialog, onKeydown, onCancel }
}
