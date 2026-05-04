<template>
  <section class="section-card tenant-provider-list">
    <div class="section-card-header">
      <div>
        <h3>Tenant Providers</h3>
        <p class="section-subtitle">Manage the external providers available to users in this tenant.</p>
      </div>
    </div>

    <div class="section-card-body">
      <p v-if="providers.length === 0" class="muted-hint">No tenant providers configured yet.</p>

      <div v-else class="tenant-provider-items">
        <article v-for="provider in providers" :key="provider.id" class="tenant-provider-item">
          <div>
            <h4>{{ provider.displayName }}</h4>
            <p class="tenant-provider-meta">{{ provider.providerKind }} · {{ provider.protocolKind }}</p>
            <p class="tenant-provider-meta">{{ provider.allowedEmailDomains.join(', ') || 'All domains allowed' }}</p>
          </div>

          <div class="tenant-provider-actions">
            <span :class="['chip', provider.isEnabled ? 'chip-success' : 'chip-muted', 'chip-sm']">
              {{ provider.isEnabled ? 'Enabled' : 'Disabled' }}
            </span>
            <button
              class="btn-secondary btn-sm"
              :disabled="busyProviderId === provider.id"
              @click="$emit('delete', provider.id)"
            >
              {{ busyProviderId === provider.id ? 'Removing…' : 'Remove' }}
            </button>
          </div>
        </article>
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
import type { TenantSsoProviderDto } from '@/services/tenantSsoProvidersService'

defineProps<{
  providers: TenantSsoProviderDto[]
  busyProviderId?: string | null
}>()

defineEmits<{
  (e: 'delete', providerId: string): void
}>()
</script>

<style scoped>
.tenant-provider-items {
  display: flex;
  flex-direction: column;
  gap: 0.85rem;
}

.tenant-provider-item {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: 12px;
}

.tenant-provider-item h4,
.tenant-provider-meta {
  margin: 0;
}

.tenant-provider-meta {
  color: var(--color-text-muted);
  margin-top: 0.35rem;
}

.tenant-provider-actions {
  display: flex;
  align-items: center;
  gap: 0.6rem;
}

@media (max-width: 760px) {
  .tenant-provider-item {
    flex-direction: column;
  }

  .tenant-provider-actions {
    justify-content: space-between;
  }
}
</style>
