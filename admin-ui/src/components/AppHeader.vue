<template>
  <header class="app-header">
      <div class="app-brand">
          <img :src="icon" alt="" aria-hidden="true" class="app-icon"/>
          <span class="app-title">Meister ProPR Admin</span>
      </div>
    <button class="logout-btn" @click="logout">Logout</button>
  </header>
</template>

<script setup lang="ts">
import {useRouter} from 'vue-router'
import {useSession} from '@/composables/useSession'
import icon from '@/assets/logo_standalone.png'

const router = useRouter()
const { clearAdminKey } = useSession()

function logout() {
  clearAdminKey()
  router.push('/login')
}
</script>
