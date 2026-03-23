<template>
    <div class="review-history-section">
        <p v-if="loading" class="loading">Loading…</p>
        <p v-else-if="error" class="error">{{ error }}</p>
        <p v-else-if="groups.length === 0" class="empty-state">No completed reviews yet.</p>

        <template v-else>
            <section
                v-for="group in groups"
                :key="group.key"
                class="pr-group"
            >
                <h3 class="pr-group-header">
                    <a :href="group.prUrl" target="_blank" rel="noopener noreferrer" class="pr-link">
                        PR #{{ group.pullRequestId }}
                    </a>
                    <span class="repo-name">{{ group.repositoryId }}</span>
                </h3>

                <table class="review-table">
                    <thead>
                        <tr>
                            <th>Completed</th>
                            <th>Iteration</th>
                            <th>Summary</th>
                        </tr>
                    </thead>
                    <tbody>
                        <tr v-for="item in group.items" :key="item.id">
                            <td class="date-cell">{{ formatDate(item.completedAt) }}</td>
                            <td class="iter-cell">#{{ item.iterationId }}</td>
                            <td class="summary-cell">{{ item.resultSummary ?? '—' }}</td>
                        </tr>
                    </tbody>
                </table>
            </section>
        </template>
    </div>
</template>

<script lang="ts" setup>
import { onMounted, ref } from 'vue'
import { createAdminClient } from '@/services/api'
import type { components } from '@/services/generated/openapi'

const props = withDefaults(defineProps<{ clientId?: string }>(), { clientId: undefined })

type JobListItem = components['schemas']['JobListItem']

interface PrGroup {
    key: string
    pullRequestId: number
    repositoryId: string
    prUrl: string
    latestCompletedAt: string
    items: JobListItem[]
}

const loading = ref(false)
const error = ref('')
const groups = ref<PrGroup[]>([])

onMounted(async () => {
    loading.value = true
    try {
        const { data } = await createAdminClient().GET('/jobs', {
            params: {
                query: {
                    status: 'completed',
                    limit: 500,
                    ...(props.clientId ? { clientId: props.clientId } : {}),
                },
            },
        })
        const items = (data as { items?: JobListItem[] })?.items ?? []
        groups.value = buildGroups(items)
    } catch {
        error.value = 'Failed to load review history.'
    } finally {
        loading.value = false
    }
})

function buildGroups(items: JobListItem[]): PrGroup[] {
    const map = new Map<string, PrGroup>()

    for (const item of items) {
        const orgUrl = item.organizationUrl ?? ''
        const project = item.projectId ?? ''
        const repo = item.repositoryId ?? ''
        const prId = item.pullRequestId ?? 0

        const key = `${orgUrl}|${project}|${repo}|${prId}`
        const prUrl = `${orgUrl}/${project}/_git/${repo}/pullrequest/${prId}`

        if (!map.has(key)) {
            map.set(key, {
                key,
                pullRequestId: prId,
                repositoryId: repo,
                prUrl,
                latestCompletedAt: item.completedAt ?? '',
                items: [],
            })
        }

        const group = map.get(key)!
        group.items.push(item)

        if ((item.completedAt ?? '') > group.latestCompletedAt) {
            group.latestCompletedAt = item.completedAt ?? ''
        }
    }

    for (const group of map.values()) {
        group.items.sort((a, b) =>
            (b.completedAt ?? '').localeCompare(a.completedAt ?? ''),
        )
    }

    return [...map.values()].sort((a, b) =>
        b.latestCompletedAt.localeCompare(a.latestCompletedAt),
    )
}

function formatDate(iso: string | null | undefined): string {
    if (!iso) return '—'
    return new Date(iso).toLocaleString()
}
</script>

<style scoped>
.empty-state,
.loading {
    color: #888;
    font-style: italic;
}

.pr-group {
    margin-bottom: 2rem;
    border: 1px solid #ddd;
    border-radius: 6px;
    overflow: hidden;
}

.pr-group-header {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin: 0;
    padding: 0.6rem 1rem;
    background: #f5f5f5;
    border-bottom: 1px solid #ddd;
    font-size: 1rem;
}

.pr-link {
    font-weight: bold;
    color: #0366d6;
    text-decoration: none;
}

.pr-link:hover {
    text-decoration: underline;
}

.repo-name {
    font-size: 0.85rem;
    color: #666;
    font-weight: normal;
}

.review-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.9rem;
}

.review-table th,
.review-table td {
    padding: 0.5rem 1rem;
    text-align: left;
    border-bottom: 1px solid #eee;
}

.review-table th {
    background: #fafafa;
    font-weight: 600;
    color: #444;
}

.review-table tbody tr:last-child td {
    border-bottom: none;
}

.date-cell {
    white-space: nowrap;
    color: #555;
    min-width: 12rem;
}

.iter-cell {
    white-space: nowrap;
    color: #777;
    width: 5rem;
}

.summary-cell {
    color: #333;
}
</style>
