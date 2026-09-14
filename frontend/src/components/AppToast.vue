<template>
  <div v-if="toast.text" class="toast" :class="{ err: toast.err }">{{ toast.text }}</div>
</template>

<script setup>
import { toast } from './ui'
</script>
