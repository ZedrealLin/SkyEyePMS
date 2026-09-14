<template>
  <div>
    <div class="page-head">
      <h1>审计日志</h1>
      <div class="spacer"></div>
      <select v-model="action" style="width:180px">
        <option value="">全部动作</option>
        <option v-for="a in actions" :key="a" :value="a">{{ a }}</option>
      </select>
    </div>

    <div class="card">
      <div v-if="loading" class="empty">加载中…</div>
      <div v-else-if="items.length === 0" class="empty">暂无审计记录</div>
      <table v-else>
        <thead><tr><th>时间</th><th>操作人</th><th>动作</th><th>对象</th><th>详情</th></tr></thead>
        <tbody>
          <tr v-for="a in items" :key="a.id">
            <td class="mono muted">{{ fmtTime(a.createdAt) }}</td>
            <td>{{ a.username }}</td>
            <td><span class="pill" :class="actionClass(a.action)">{{ a.action }}</span></td>
            <td>{{ a.target || '—' }}</td>
            <td class="muted">{{ a.detail || '—' }}</td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<script setup>
import { ref, onMounted, onUnmounted, watch } from 'vue'
import { api, fmtTime, errMsg } from '../api'
import { showToast } from '../components/ui'

const items = ref([])
const action = ref('')
const loading = ref(true)
let timer = null

// 常用动作前缀聚合（动作较多时下拉不至于过长）
const actions = [
  'printer.create', 'printer.update', 'printer.delete',
  'alert.ack', 'alert.resolve',
  'settings.save',
  'user.create', 'user.delete', 'user.reset-password'
]

function actionClass(a) {
  if (a.endsWith('.delete')) return 'error'
  if (a.endsWith('.create')) return 'ok'
  return 'unknown'
}

async function load() {
  try {
    const { data } = await api.get('/audit/', { params: { action: action.value || undefined, limit: 200 } })
    items.value = data
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  load()
  timer = setInterval(() => { if (!document.hidden) load() }, 30000)
})
onUnmounted(() => clearInterval(timer))
watch(action, load)
</script>

<style scoped>
.page-head { display: flex; align-items: center; gap: 12px; margin-bottom: 16px; }
</style>
