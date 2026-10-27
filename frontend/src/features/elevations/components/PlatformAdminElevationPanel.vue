<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<script setup lang="ts">
/**
 * The elevation controls of a tenant page or a client page, for platform administrators. It shows the active
 * elevation that covers the tenant or the client, or offers to start one. While the active elevations or the clients
 * of the tenant are not loaded, it offers no start and shows a failed load with a retry. Without multi-tenancy the
 * panel shows nothing and sends no request.
 */
import { computed, onMounted, onUnmounted, ref } from 'vue'
import StartElevationDialog from '@/features/elevations/components/StartElevationDialog.vue'
import { describeScope, formatRemaining, usePlatformAdminElevations } from '@/composables/usePlatformAdminElevations'
import { listTenantClients } from '@/services/elevationsService'

const props = defineProps<{
  tenantId: string
  tenantName?: string | null
  clientId?: string | null
  clientName?: string | null
  clients?: { id: string; displayName: string }[]
  compact?: boolean
}>()

const { available, covering, loaded, loadError, now, refresh, startClock, stopClock } = usePlatformAdminElevations()
const dialogOpen = ref(false)
const tenantClients = ref<{ id: string; displayName: string }[]>([])
const clientsLoading = ref(false)
const clientsError = ref('')

const elevation = computed(() => covering(props.tenantId, props.clientId ?? null))
const clientChoices = computed(() => props.clients ?? tenantClients.value)
// On a tenant page the scope can be one of its clients, so the dialog lists them. Until the list is loaded the panel
// offers no start, because the dialog would offer the whole tenant as the only scope.
const needsClients = computed(() => !props.clientId && !props.clients)
const clientsReady = computed(() => !needsClients.value || (!clientsLoading.value && !clientsError.value))

onMounted(() => {
  startClock()
  if (!available.value) {
    return
  }

  void refresh()
  if (needsClients.value) {
    void loadClients()
  }
})
onUnmounted(stopClock)

async function loadClients(): Promise<void> {
  clientsLoading.value = true
  clientsError.value = ''
  try {
    tenantClients.value = await listTenantClients(props.tenantId)
  } catch (failure) {
    clientsError.value = failure instanceof Error ? failure.message : 'The clients of the tenant could not be loaded.'
  } finally {
    clientsLoading.value = false
  }
}
</script>

<template>
  <section v-if="available" :class="['elevation-panel', { 'elevation-panel--compact': compact }]" data-testid="elevation-panel">
    <template v-if="elevation">
      <p class="elevation-panel-text">
        <i class="fi fi-rr-eye" aria-hidden="true"></i>
        You are elevated for {{ describeScope(elevation) }} for
        <span data-testid="elevation-panel-remaining">{{ formatRemaining(elevation.expiresAt, now) }}</span>.
      </p>
    </template>
    <template v-else-if="loadError">
      <p class="error elevation-panel-error" role="alert" data-testid="elevation-panel-load-error">
        {{ loadError }} Whether an elevation covers {{ clientId ? 'this client' : 'this tenant' }} is not known.
      </p>
      <button type="button" class="btn-secondary" data-testid="elevation-panel-retry" @click="refresh">
        <i class="fi fi-rr-refresh"></i> Try again
      </button>
    </template>
    <p v-else-if="!loaded" class="elevation-panel-text" data-testid="elevation-panel-loading">
      Loading your active elevations…
    </p>
    <template v-else>
      <p class="elevation-panel-text">
        Platform administrators read the content of {{ clientId ? 'this client' : 'this tenant' }} only during an
        elevation, which ProPR records in the audit history of the tenant.
      </p>
      <template v-if="clientsError">
        <p class="error elevation-panel-error" role="alert" data-testid="elevation-panel-clients-error">
          {{ clientsError }} Load them again to choose the scope of an elevation.
        </p>
        <button type="button" class="btn-secondary" data-testid="elevation-panel-clients-retry" @click="loadClients">
          <i class="fi fi-rr-refresh"></i> Load the clients again
        </button>
      </template>
      <button
        v-else
        type="button"
        class="btn-secondary"
        :disabled="!clientsReady"
        data-testid="elevation-panel-start"
        @click="dialogOpen = true"
      >
        <i class="fi fi-rr-eye"></i> {{ clientsReady ? 'Start an elevation' : 'Loading the clients…' }}
      </button>
    </template>

    <StartElevationDialog
      v-model:is-open="dialogOpen"
      :tenant-id="tenantId"
      :tenant-name="tenantName"
      :client-id="clientId"
      :client-name="clientName"
      :clients="clientChoices"
    />
  </section>
</template>

<style scoped>
.elevation-panel {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0.6rem;
  padding: 1rem 1.25rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--surface-subtle);
}

.elevation-panel--compact {
  padding: 0.75rem;
  margin-bottom: 1.5rem;
}

.elevation-panel-error {
  margin: 0;
  font-size: 0.85rem;
}

.elevation-panel-text {
  margin: 0;
  font-size: 0.85rem;
  line-height: 1.5;
  color: var(--color-text-muted);
}
</style>
