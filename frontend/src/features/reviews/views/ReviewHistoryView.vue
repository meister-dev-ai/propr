<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <PageWithSidebar>
        <template #sidebar>
            <AppNavDrawer>
                <div class="sidebar-nav">
                    <div class="sidebar-nav-group">
                        <h4>Reviews</h4>
                        <button class="sidebar-nav-link active">
                            <i class="fi fi-rr-time-past"></i> Global History
                        </button>
                    </div>
                </div>
            </AppNavDrawer>
        </template>

        <AppTopBar class="view-header">
            <h2 class="view-title">Review History</h2>
            <button class="btn-primary" @click="handleRefresh" :disabled="isRefreshing" title="Refresh review history">
                <i class="fi fi-rr-refresh"></i>
                {{ isRefreshing ? 'Refreshing…' : 'Refresh' }}
            </button>
        </AppTopBar>
        <ReviewHistorySection ref="historySection" />
    </PageWithSidebar>
</template>

<script lang="ts" setup>
import { ref } from 'vue'
import { AppNavDrawer, AppTopBar, PageWithSidebar } from '@/components'
import ReviewHistorySection from '@/features/reviews/components/ReviewHistorySection.vue'

const historySection = ref<InstanceType<typeof ReviewHistorySection>>()
const isRefreshing = ref(false)

async function handleRefresh() {
    isRefreshing.value = true
    try {
        await historySection.value?.refresh()
    } finally {
        isRefreshing.value = false
    }
}
</script>

<style scoped>
.view-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    margin-bottom: 2rem;
}

.view-title {
    margin: 0;
    flex: 1;
}

.btn-primary {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
    flex-shrink: 0;
}
</style>
