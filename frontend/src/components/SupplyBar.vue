<template>
  <div class="supply-row">
    <span class="s-name" :title="supply.name">{{ supply.name || `耗材 #${supply.supplyIndex}` }}</span>
    <div class="bar" :class="level">
      <div class="fill" :style="{ width: width }"></div>
    </div>
    <span class="s-pct" :class="{ dim: supply.percent == null }">{{ label }}</span>
  </div>
</template>

<script setup>
import { computed } from 'vue'

const props = defineProps({ supply: { type: Object, required: true } })

const level = computed(() => {
  const p = props.supply.percent
  if (p == null) return 'unknown'
  if (p <= 10) return 'low'
  if (p <= 25) return 'mid'
  return 'high'
})

const width = computed(() => (props.supply.percent == null ? '100%' : `${Math.min(100, props.supply.percent)}%`))

const label = computed(() => {
  const p = props.supply.percent
  if (p == null) return '未知'
  return `${p}%`
})
</script>

<style scoped>
.supply-row {
  display: grid;
  grid-template-columns: minmax(90px, 150px) 1fr 44px;
  gap: 10px;
  align-items: center;
  padding: 3px 0;
}
.s-name {
  font-size: 12.5px;
  color: var(--text-dim);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.s-pct { font-size: 12.5px; text-align: right; font-family: var(--mono); }
.s-pct.dim { color: var(--text-faint); }
</style>
