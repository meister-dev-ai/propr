<template>
  <section class="section-card tenant-create-form-card">
    <div class="section-card-header">
      <div>
        <h3>Create Tenant</h3>
        <p class="section-subtitle">Bootstrap a new tenant and jump directly into tenant-scoped setup.</p>
      </div>
    </div>

    <div class="section-card-body">
      <form class="tenant-create-form" @submit.prevent="handleSubmit">
        <div class="tenant-create-grid">
          <div class="form-field">
            <label for="tenant-create-slug">Slug</label>
            <input
              id="tenant-create-slug"
              v-model="form.slug"
              data-testid="tenant-create-slug"
              type="text"
              placeholder="acme"
            />
          </div>

          <div class="form-field">
            <label for="tenant-create-display-name">Display name</label>
            <input
              id="tenant-create-display-name"
              v-model="form.displayName"
              data-testid="tenant-create-display-name"
              type="text"
              placeholder="Acme Corp"
            />
          </div>
        </div>

        <p v-if="error" class="error">{{ error }}</p>

        <div class="form-actions">
          <button class="btn-primary btn-sm" data-testid="tenant-create-submit" type="submit" :disabled="busy">
            {{ busy ? 'Creating…' : 'Create tenant' }}
          </button>
        </div>
      </form>
    </div>
  </section>
</template>

<script setup lang="ts">
import { reactive } from 'vue'
import type { CreateTenantRequest } from '@/services/tenantAdminService'

const props = withDefaults(defineProps<{
  busy?: boolean
  error?: string
}>(), {
  busy: false,
  error: '',
})

const emit = defineEmits<{
  (e: 'submit', request: CreateTenantRequest): void
}>()

const form = reactive<CreateTenantRequest>({
  slug: '',
  displayName: '',
})

function handleSubmit() {
  emit('submit', {
    slug: form.slug.trim(),
    displayName: form.displayName.trim(),
  })

  if (!props.busy) {
    form.slug = ''
    form.displayName = ''
  }
}
</script>

<style scoped>
.tenant-create-form {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.tenant-create-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 0.85rem;
}
</style>
