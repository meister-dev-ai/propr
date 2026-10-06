// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { ref } from 'vue'
import { listPerformanceClients, type PerformanceClient } from '@/services/reviewerPerformanceService'

export function useReviewerPerformanceClients() {
  const clients = ref<PerformanceClient[]>([])
  const loading = ref(false)
  const error = ref('')

  async function load(): Promise<void> {
    loading.value = true
    error.value = ''
    try {
      clients.value = await listPerformanceClients()
    } catch (exception) {
      error.value = exception instanceof Error ? exception.message : 'Clients could not be loaded.'
    } finally {
      loading.value = false
    }
  }

  return { clients, loading, error, load }
}
