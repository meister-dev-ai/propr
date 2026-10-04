<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div id="app" :class="{ 'app-pr-workspace': isPrReview }">
    <AppHeader v-if="isAuthenticated">
      <template #notifications>
        <button
          v-if="isPrReview && hasInstallationNotices"
          ref="noticesTrigger"
          type="button"
          class="installation-notices-trigger"
          :class="{ 'installation-notices-warning': authorOverageNotice !== null }"
          aria-label="Installation notices"
          title="Installation notices"
          :aria-expanded="noticesOpen"
          :aria-controls="noticesId"
          data-testid="installation-notices-trigger"
          @click="noticesOpen = !noticesOpen"
        >
          <i class="fi fi-rr-info" aria-hidden="true"></i>
        </button>
      </template>
    </AppHeader>
    <div
      v-if="isAuthenticated"
      v-show="!isPrReview || noticesOpen"
      :id="noticesId"
      ref="noticesRegion"
      class="app-notices"
    >
      <AuthorOverageNotice />
      <UsageStatisticsNotice :presented="!isPrReview || noticesOpen" />
    </div>
    <div :class="{ 'app-pr-content': isPrReview }">
      <RouterView />
    </div>
    <AppNotification />
    <footer v-if="isAuthenticated && !isPrReview" class="app-footer">
      Powered by <a href="https://meister-dev.ai" target="_blank" rel="noopener noreferrer" class="footer-link">meister-dev.ai</a>
    </footer>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, useId, watch } from 'vue'
import { RouterView, useRoute } from 'vue-router'
import AppHeader from '@/components/navigation/AppHeader.vue'
import AppNotification from '@/components/feedback/AppNotification.vue'
import UsageStatisticsNotice from '@/features/usage-statistics/components/UsageStatisticsNotice.vue'
import AuthorOverageNotice from '@/features/licensing/components/AuthorOverageNotice.vue'
import { useSession } from '@/composables/useSession'
import { useLicensing } from '@/composables/useLicensing'
import { useUsageStatistics } from '@/composables/useUsageStatistics'

const { isAuthenticated, isAdmin } = useSession()
const { authorOverageNotice } = useLicensing()
const { settings, noticeRequired } = useUsageStatistics()
const route = useRoute()
const isPrReview = computed(() => route.name === 'pr-review')
const hasInstallationNotices = computed(() => isAuthenticated.value && isAdmin.value && (
  authorOverageNotice.value !== null
  || (noticeRequired.value && settings.value?.communityOptIn !== false)
))
const noticesOpen = ref(false)
const noticesId = useId()
const noticesTrigger = ref<HTMLButtonElement | null>(null)
const noticesRegion = ref<HTMLElement | null>(null)

watch([isPrReview, hasInstallationNotices], () => { noticesOpen.value = false })
onMounted(() => {
  document.addEventListener('click', onDocumentClick)
  document.addEventListener('keydown', onKeydown)
})
onUnmounted(() => {
  document.removeEventListener('click', onDocumentClick)
  document.removeEventListener('keydown', onKeydown)
})

function onDocumentClick(event: MouseEvent): void {
  if (event.target instanceof Node
    && !noticesTrigger.value?.contains(event.target)
    && !noticesRegion.value?.contains(event.target)) {
    noticesOpen.value = false
  }
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && noticesOpen.value) {
    noticesOpen.value = false
    noticesTrigger.value?.focus()
  }
}
</script>

<style scoped>
.app-pr-workspace {
  position: relative;
  display: flex;
  flex-direction: column;
  height: 100dvh;
  overflow: hidden;
}
.app-pr-workspace > :deep(.app-header) {
  flex: 0 0 auto;
}
.app-pr-workspace > .app-notices {
  position: absolute;
  top: 5rem;
  right: 1rem;
  z-index: 100;
  width: min(32rem, calc(100vw - 2rem));
  max-height: calc(100dvh - 6rem);
  overflow: auto;
  padding: 0.5rem;
  gap: 0.5rem;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  box-shadow: 0 6px 24px rgb(0 0 0 / 25%);
}
.app-pr-workspace > .app-notices > :first-child {
  margin-top: 0;
}
.installation-notices-trigger {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 2.5rem;
  height: 2.5rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  color: var(--color-accent);
  background: var(--color-surface);
  cursor: pointer;
}
.installation-notices-warning {
  color: var(--color-warning);
  border-color: var(--color-warning);
}
.installation-notices-trigger:focus-visible {
  outline: 2px solid var(--color-accent);
  outline-offset: 2px;
}
.app-pr-content {
  flex: 1;
  min-height: 0;
  min-width: 0;
  overflow: hidden;
}
.app-pr-workspace :deep(.app-header) {
  padding: 0.6rem 1rem;
}
@media (max-width: 1100px) {
  .app-pr-workspace :deep(.app-header) {
      flex-wrap: wrap;
      gap: 0.5rem;
  }
  .app-pr-workspace :deep(.app-nav) {
      order: 3;
      flex: 0 0 100%;
      flex-wrap: wrap;
      gap: 0.25rem 1rem;
      margin: 0;
  }
  .app-pr-workspace :deep(.header-actions) {
      margin-left: auto;
      gap: 0.35rem;
  }
}
@media (max-width: 700px) {
  .app-pr-workspace :deep(.header-actions .nav-link-button), .app-pr-workspace :deep(.logout-btn) {
      padding: 0.5rem;
  }
  .app-pr-workspace :deep(.header-actions .nav-link-button > span:not(.github-icon)), .app-pr-workspace :deep(.logout-btn > span) {
      display: none;
  }
  .app-pr-workspace :deep(.app-nav .nav-link) {
      font-size: 0.8rem;
  }
  .app-pr-workspace :deep(.app-brand) {
      gap: 0.4rem;
  }
  .app-pr-workspace :deep(.app-icon) {
      width: 1.8rem;
      height: 1.8rem;
  }
  .app-pr-workspace :deep(.app-title) {
      font-size: 1.1rem;
  }
  .app-pr-workspace :deep(.app-subtitle) {
      font-size: 0.6rem;
  }
  .app-pr-workspace :deep(.app-edition-badge) {
      font-size: 0.6rem;
  }
}
.app-footer {
  text-align: center;
  padding: 1rem 2rem;
  font-size: 0.78rem;
  color: var(--color-text-muted);
  opacity: 0.6;
  border-top: 1px solid rgba(255, 255, 255, 0.04);
  margin-top: auto;
}

.footer-link {
  color: var(--color-accent);
  text-decoration: none;
  opacity: 0.8;
  transition: opacity 0.2s;
}

.footer-link:hover {
  opacity: 1;
  text-decoration: underline;
}
</style>
