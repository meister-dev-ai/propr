<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented. -->

<script setup lang="ts">
/**
 * The author allowance notice, shown on every page to a platform administrator.
 *
 * It is rendered beside the page rather than only on the licensing panel, because the allowance is metered and
 * not enforced: nothing stops working when the month goes above the licensed number, so an administrator who
 * never opens the panel would have no other way to find out.
 *
 * It renders while the current month's counted authors are above the number the license states, and it says
 * that nothing has been withheld. Nothing renders once the month is at or below the number.
 */
import { computed, onMounted, onUnmounted, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useSession } from '@/composables/useSession'
import { useLicensing } from '@/composables/useLicensing'

const { isAdmin, isAuthenticated } = useSession()
const { authorOverageNotice, load, reset } = useLicensing()

const visible = computed(() => isAuthenticated.value && isAdmin.value && authorOverageNotice.value !== null)

const observedCount = computed(() => authorOverageNotice.value?.observedCount ?? 0)

const licensedCount = computed(() => authorOverageNotice.value?.licensedCount ?? 0)

onMounted(ensureLoaded)
watch(() => isAuthenticated.value && isAdmin.value, ensureLoaded)

// The notice renders only while a session is active, so this unmounts on sign-out. Clearing here stops the
// next session from rendering the previous session's licensing state.
onUnmounted(reset)

async function ensureLoaded(): Promise<void> {
  if (!isAuthenticated.value || !isAdmin.value) {
    return
  }

  await load()
}
</script>

<template>
  <aside v-if="visible" class="licensing-notice" role="status" data-testid="author-overage-notice">
    <i class="fi fi-rr-users licensing-notice-icon" aria-hidden="true"></i>

    <p class="licensing-notice-text">
      This installation counted {{ observedCount }} pull request author(s) this month, above the
      {{ licensedCount }} its license states. Nothing has been withheld, delayed or degraded: the author
      allowance is recorded and reported, not enforced. Arrange a license that covers the number of authors
      this installation has. <RouterLink :to="{ name: 'licensing' }">Open licensing</RouterLink>.
    </p>
  </aside>
</template>

<style scoped>
/* Width and outer spacing come from the notice region that mounts this, so only the appearance is set here. */
.licensing-notice {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  padding: 0.8rem 1rem;
  border: 1px solid var(--color-warning);
  border-radius: var(--radius-lg);
  background: var(--surface-subtle);
}

.licensing-notice-icon {
  color: var(--color-warning);
  margin-top: 0.15rem;
}

.licensing-notice-text {
  flex: 1;
  margin: 0;
  font-size: 0.85rem;
  line-height: 1.55;
  color: var(--color-text-muted);
}
</style>
