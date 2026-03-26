<template>
  <header class="app-header">
    <div class="app-brand">
      <img :src="icon" alt="" aria-hidden="true" class="app-icon"/>
      <span class="app-title">Meister ProPR Admin</span>
    </div>
    <nav class="app-nav">
      <RouterLink to="/" class="nav-link">Clients</RouterLink>
      <RouterLink to="/reviews" class="nav-link">Reviews</RouterLink>
      <RouterLink v-if="isAdmin" to="/users" class="nav-link">Users</RouterLink>
      <RouterLink to="/pats" class="nav-link">My PATs</RouterLink>
    </nav>
    <button class="btn-slide" @click="logout" aria-label="Logout">
      <div class="sign"><svg viewBox="0 0 512 512"><path d="M377.9 105.9L500.7 228.7c7.2 7.2 11.3 17.1 11.3 27.3s-4.1 20.1-11.3 27.3L377.9 406.1c-6.4 6.4-15 9.9-24 9.9c-18.7 0-33.9-15.2-33.9-33.9l0-62.1-128 0c-17.7 0-32-14.3-32-32l0-64c0-17.7 14.3-32 32-32l128 0 0-62.1c0-18.7 15.2-33.9 33.9-33.9c9 0 17.6 3.6 24 9.9zM160 96L96 96c-17.7 0-32 14.3-32 32l0 256c0 17.7 14.3 32 32 32l64 0c17.7 0 32 14.3 32 32s-14.3 32-32 32l-64 0c-53 0-96-43-96-96L0 128C0 75 43 32 96 32l64 0c17.7 0 32 14.3 32 32s-14.3 32-32 32z"></path></svg></div>
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
