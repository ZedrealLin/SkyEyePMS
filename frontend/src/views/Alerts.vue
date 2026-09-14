<template>
  <div>
    <div class="page-head">
      <h1>告警中心</h1>
      <div class="spacer"></div>
      <div class="chip-row">
        <button class="chip" :class="{ active: status === 'active' }" @click="status = 'active'">告警中</button>
        <button class="chip" :class="{ active: status === 'resolved' }" @click="status = 'resolved'">已恢复</button>
        <button class="chip" :class="{ active: status === '' }" @click="status = ''">全部</button>
      </div>
      <div class="spacer"></div>
      <button class="btn ghost sm" @click="exportAlerts">导出 CSV</button>
    </div>

    <div class="card">
      <div v-if="loading" class="empty">加载中…</div>
      <div v-else-if="items.length === 0" class="empty">暂无告警记录</div>
      <table v-else>
        <thead>
          <tr><th>级别</th><th>打印机</th><th>内容</th><th>状态</th><th>确认</th><th>首次触发</th><th>最近推送</th><th>推送次数</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="a in items" :key="a.id">
            <td><span class="pill" :class="sevClass(a.severity)">{{ sevText(a.severity) }}</span></td>
            <td><router-link :to="`/printers/${a.printerId}`">{{ printerName(a.printerId) }}</router-link></td>
            <td>{{ a.message }}</td>
            <td>
              <span class="pill" :class="a.status === 'active' ? 'error' : 'ok'">
                <span class="dot"></span>{{ a.status === 'active' ? '告警中' : '已恢复' }}
              </span>
            </td>
            <td>
              <span v-if="a.acknowledgedAt" class="pill ok" :title="`${a.acknowledgedBy} 确认于 ${fmtTime(a.acknowledgedAt)}`">
                <span class="dot"></span>{{ a.acknowledgedBy }}
              </span>
              <span v-else class="faint">未确认</span>
            </td>
            <td class="muted">{{ fmtTime(a.firstTriggeredAt) }}</td>
            <td class="muted">{{ fmtTime(a.lastNotifiedAt) }}</td>
            <td class="mono">{{ a.notifyCount }}</td>
            <td>
              <template v-if="a.status === 'active' && auth.role === 'admin'">
                <button v-if="!a.acknowledgedAt" class="btn ghost sm" @click="ack(a)">确认</button>
                <button class="btn ghost sm" @click="resolve(a)">手动恢复</button>
              </template>
            </td>
          </tr>
        </tbody>
      </table>
      <div class="faint" style="margin-top:8px" v-if="total > items.length">
        显示 {{ items.length }} / {{ total }} 条
      </div>
    </div>
  </div>
</template>

<script setup>
import { ref, onMounted, onUnmounted, watch } from 'vue'
import { api, auth, fmtTime, errMsg, downloadFile } from '../api'
import { showToast } from '../components/ui'

const status = ref('active')
const items = ref([])
const total = ref(0)
const printers = ref([])
const loading = ref(true)
let timer = null

function sevText(s) { return { Critical: '严重', Warning: '警告', Info: '提示' }[s] || s }
function sevClass(s) { return { Critical: 'error', Warning: 'warn', Info: 'ok' }[s] || 'unknown' }
function printerName(pid) {
  return printers.value.find(p => p.id === pid)?.name || `#${pid}`
}

async function load() {
  try {
    const { data } = await api.get('/alerts/', { params: { status: status.value || undefined, limit: 200 } })
    items.value = data.items
    total.value = data.total
    if (printers.value.length === 0) {
      const ps = await api.get('/printers/')
      printers.value = ps.data
    }
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    loading.value = false
  }
}

async function resolve(a) {
  try {
    await api.post(`/alerts/${a.id}/resolve`)
    showToast('已标记恢复')
    load()
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

async function ack(a) {
  try {
    await api.post(`/alerts/${a.id}/ack`)
    showToast('已确认该告警')
    load()
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

async function exportAlerts() {
  try {
    const d = new Date()
    const pad = n => String(n).padStart(2, '0')
    await downloadFile('/api/alerts/export.csv', `skyeye_alerts_${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}_${pad(d.getHours())}${pad(d.getMinutes())}.csv`)
    showToast('已导出告警 CSV')
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

onMounted(() => {
  load()
  timer = setInterval(() => { if (!document.hidden) load() }, 20000)
})
onUnmounted(() => clearInterval(timer))
watch(status, load)
</script>

<style scoped>
.page-head { display: flex; align-items: center; gap: 12px; margin-bottom: 16px; }.chip-row { display: flex; gap: 6px; }
.chip {
  background: var(--bg-soft);
  border: 1px solid var(--line);
  color: var(--text-dim);
  border-radius: 18px;
  padding: 4px 14px;
  font-size: 12.5px;
  cursor: pointer;
}
.chip.active { background: color-mix(in srgb, var(--accent) 18%, transparent); border-color: var(--accent); color: var(--accent-2); }
</style>
