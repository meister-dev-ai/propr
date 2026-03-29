<template>
  <header class="app-header">
    <div class="app-brand">
      <img :src="icon" alt="" aria-hidden="true" class="app-icon"/>
      <span class="app-title">Meister ProPR Admin</span>
    </div>
    <nav class="app-nav">
      <RouterLink to="/" class="nav-link"><i class="fi fi-rr-users"></i> Clients</RouterLink>
      <RouterLink to="/reviews" class="nav-link" :class="{ 'router-link-active': $route.name === 'job-protocol' }"><i class="fi fi-rr-search"></i> Reviews</RouterLink>
      <RouterLink v-if="isAdmin" to="/users" class="nav-link"><i class="fi fi-rr-user"></i> Users</RouterLink>
      <RouterLink to="/pats" class="nav-link"><i class="fi fi-rr-key"></i> My PATs</RouterLink>
      <RouterLink to="/crawl-configs" class="nav-link"><i class="fi fi-rr-settings"></i> Crawl Configs</RouterLink>
    </nav>
    <button class="btn-slide" @click="logout" aria-label="Logout">
      <div class="sign"><i class="fi fi-rr-exit"></i></div>
      <div class="text">Logout</div>
    </button>
  </header>
</template>

<script setup lang="ts">
import { RouterLink, useRouter } from 'vue-router'
import { useSession } from '@/composables/useSession'
import icon from '@/assets/logo_standalone.png'

const router = useRouter()
const { clearTokens, isAdmin } = useSession()

function logout() {
  clearTokens()
  router.push('/login')
}
</script>

<style scoped>
.app-nav {
  display: flex;
  gap: 2rem;
  align-items: center;
  justify-content: center;
  flex: 1;
  margin: 0 2rem;
}

.nav-link {
  color: var(--color-text);
  text-decoration: none;
  font-size: 0.95rem;
  font-weight: 500;
  opacity: 0.7;
  transition: all 0.2s;
  padding: 0.5rem 0;
  border-bottom: 2px solid transparent;
}

.nav-link:hover,
.nav-link.router-link-active {
  opacity: 1;
  color: var(--color-text);
  border-bottom-color: var(--color-accent);
}

.btn-slide {
  display: flex;
  align-items: center;
  justify-content: flex-start;
  width: 45px;
  height: 45px;
  border: none;
  border-radius: 50%;
  cursor: pointer;
  position: relative;
  overflow: hidden;
  transition-duration: .3s;
  box-shadow: 2px 2px 10px rgba(0, 0, 0, 0.199);
  background-color: var(--color-surface);
  color: var(--color-text);
}

.btn-slide:hover {
  background-color: rgba(239, 68, 68, 0.1); 
  border: 1px solid var(--color-danger);
}

.btn-slide .sign {
  transition-duration: .3s;
  display: flex;
  align-items: center;
  justify-content: center;
}

.btn-slide .sign svg {
  width: 16px;
  margin-left: -2px;
}

.btn-slide .sign svg path {
  fill: var(--color-danger);
}

.btn-slide .text {
  width: 0%;
  opacity: 0;
  color: var(--color-danger);
  font-size: 1.1em;
  font-weight: 600;
  transition-duration: .3s;
}

.btn-slide:hover {
  width: 125px;
  border-radius: 40px;
  transition-duration: .3s;
}

.btn-slide:hover .sign {
  width: 30%;
  transition-duration: .3s;
  padding-left: 10px;
}

.btn-slide:hover .text {
  opacity: 1;
  width: 70%;
  transition-duration: .3s;
  padding-right: 15px;
}

.btn-slide:active {
  transform: translate(2px ,2px);
}
</style>
