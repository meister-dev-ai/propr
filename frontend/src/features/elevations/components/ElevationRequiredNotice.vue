<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<script setup lang="ts">
/**
 * Shown in place of content the server refused because no elevation of the platform administrator covers the
 * client. It names the client and offers to start an elevation for it; the view reloads once the elevation started.
 * Without multi-tenancy it offers no start and states why. It also looks up no names then, because the client
 * endpoints show only the clients of the System tenant, and a refused client belongs to another tenant.
 */
import { onMounted, ref, watch } from 'vue'
import StartElevationDialog from '@/features/elevations/components/StartElevationDialog.vue'
import { usePlatformAdminElevations } from '@/composables/usePlatformAdminElevations'
import { getClientNames, type ElevationRequirement, type PlatformAdminElevation } from '@/services/elevationsService'

const props = defineProps<{
  requirement: ElevationRequirement
  /** What the page would show, as a noun phrase, for example "the protocol of this review". */
  subject?: string
}>()

const emit = defineEmits<{
  (e: 'started', elevation: PlatformAdminElevation): void
}>()

const { available } = usePlatformAdminElevations()
const clientName = ref<string | null>(null)
const tenantName = ref<string | null>(null)
const dialogOpen = ref(false)

onMounted(loadNames)
watch(() => props.requirement.clientId, loadNames)

async function loadNames(): Promise<void> {
  const clientId = props.requirement.clientId
  clientName.value = null
  tenantName.value = null
  if (!available.value) {
    return
  }

  const names = await getClientNames(clientId)
  // A lookup for a client the notice no longer shows must not name it.
  if (clientId !== props.requirement.clientId) {
    return
  }

  clientName.value = names.clientName
  tenantName.value = names.tenantName
}

function onStarted(elevation: PlatformAdminElevation): void {
  emit('started', elevation)
}
</script>

<template>
  <section class="elevation-required" role="status" data-testid="elevation-required">
    <i class="fi fi-rr-lock elevation-required-icon" aria-hidden="true"></i>
    <div class="elevation-required-body">
      <p class="elevation-required-title">An elevation is required</p>
      <p class="elevation-required-text">
        {{ subject ? `${subject.charAt(0).toUpperCase()}${subject.slice(1)} belongs` : 'This content belongs' }} to client
        {{ clientName ?? requirement.clientId }}{{ tenantName ? ` of the tenant ${tenantName}` : '' }}. Platform
        administrators read the content of a tenant only during an elevation, and ProPR records each read in the audit
        history of the tenant.
      </p>
      <button v-if="available" type="button" class="btn-primary" data-testid="elevation-required-start" @click="dialogOpen = true">
        Start an elevation
      </button>
      <p v-else class="elevation-required-text" data-testid="elevation-required-unlicensed">
        Elevations are part of multi-tenancy, which the license of this installation does not include.
      </p>
    </div>

    <StartElevationDialog
      v-if="available"
      v-model:is-open="dialogOpen"
      :tenant-id="requirement.tenantId"
      :tenant-name="tenantName"
      :client-id="requirement.clientId"
      :client-name="clientName"
      @started="onStarted"
    />
  </section>
</template>

<style scoped>
.elevation-required {
  display: flex;
  gap: 1rem;
  padding: 1.25rem 1.5rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--color-surface);
}

.elevation-required-icon {
  color: var(--color-warning);
  font-size: 1.25rem;
}

.elevation-required-body {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0.6rem;
}

.elevation-required-title {
  margin: 0;
  font-weight: 600;
}

.elevation-required-text {
  margin: 0;
  font-size: 0.9rem;
  line-height: 1.5;
  color: var(--color-text-muted);
}
</style>
