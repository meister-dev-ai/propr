// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { ref } from 'vue'

export interface Notification {
  message: string
  type: 'success' | 'error'
}

const notification = ref<Notification | null>(null)
let dismissTimer: ReturnType<typeof setTimeout> | null = null

export function useNotification() {
  function notify(message: string, type: 'success' | 'error' = 'success') {
    if (dismissTimer) clearTimeout(dismissTimer)
    notification.value = { message, type }
    dismissTimer = setTimeout(() => {
      notification.value = null
    }, 4000)
  }

  function dismiss() {
    if (dismissTimer) clearTimeout(dismissTimer)
    notification.value = null
  }

  return { notification, notify, dismiss }
}
