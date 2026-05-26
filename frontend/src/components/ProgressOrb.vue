<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="orb-container">
    <div class="blob"></div>
  </div>
</template>

<style scoped>
.orb-container {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
}

.blob {
  width: 16px;
  height: 16px;
  background: var(--color-accent);
  border-radius: 40% 60% 70% 30% / 40% 50% 60% 50%;
  animation: morph 2s linear infinite, glow 2s ease-in-out infinite alternate;
}

@keyframes morph {
  0%, 100% {
    border-radius: 40% 60% 70% 30% / 40% 50% 60% 50%;
    transform: rotate(0deg) scale(0.9);
  }
  33% {
    border-radius: 70% 30% 50% 50% / 30% 30% 70% 70%;
    transform: rotate(120deg) scale(1.1);
  }
  66% {
    border-radius: 100% 60% 60% 100% / 100% 100% 60% 60%;
    transform: rotate(240deg) scale(0.95);
  }
}

@keyframes glow {
  0% {
    box-shadow: 0 0 6px 0px rgba(34, 211, 238, 0.4);
    filter: hue-rotate(0deg);
  }
  100% {
    box-shadow: 0 0 12px 3px rgba(34, 211, 238, 0.8);
    filter: hue-rotate(45deg);
  }
}
</style>
