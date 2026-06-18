// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { ref, shallowRef } from 'vue'
import { useSession } from '@/composables/useSession'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type { FileDiffDto } from '../types'

/**
 * Owns the lazily-fetched file diff shown in the trace detail panel: the loaded
 * payload, its loading/error flags, and an in-memory cache keyed by job + file.
 * The orchestrator wires `resetDiff` into pass-change watches and `clearDiff`.
 */
export function useFileDiff() {
    const fileDiff = shallowRef<FileDiffDto | null>(null)
    const diffLoading = ref(false)
    const diffError = ref<string | null>(null)
    const diffCache = new Map<string, FileDiffDto>()

    function buildDiffCacheKey(jobId: string, fileResultId: string): string {
        return `${jobId}::${fileResultId}`
    }

    async function loadFileDiff(jobId: string, fileResultId: string): Promise<void> {
        if (!jobId || !fileResultId) {
            fileDiff.value = null
            diffError.value = null
            return
        }

        const cacheKey = buildDiffCacheKey(jobId, fileResultId)
        const cached = diffCache.get(cacheKey)
        if (cached) {
            fileDiff.value = cached
            diffError.value = null
            diffLoading.value = false
            return
        }

        diffLoading.value = true
        diffError.value = null
        try {
            const { getAccessToken } = useSession()
            const token = getAccessToken()
            const headers: Record<string, string> = { Accept: 'application/json' }
            if (token) {
                headers.Authorization = `Bearer ${token}`
            }

            const res = await fetch(
                `${getActiveRuntime().apiBaseUrl}/reviewing/jobs/${jobId}/files/${fileResultId}/diff`,
                { headers },
            )
            if (!res.ok) {
                fileDiff.value = null
                diffError.value = `Diff unavailable (HTTP ${res.status})`
                return
            }

            const payload = (await res.json()) as FileDiffDto
            diffCache.set(cacheKey, payload)
            fileDiff.value = payload
            diffError.value = null
        } catch (error) {
            fileDiff.value = null
            diffError.value = `Diff unavailable: ${error instanceof Error ? error.message : 'unknown error'}`
        } finally {
            diffLoading.value = false
        }
    }

    function resetDiff(): void {
        fileDiff.value = null
        diffError.value = null
    }

    return {
        fileDiff,
        diffLoading,
        diffError,
        loadFileDiff,
        resetDiff,
    }
}
