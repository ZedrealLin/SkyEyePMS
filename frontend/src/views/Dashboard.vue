<template>
  <div>
    <div class="page-head">
      <h1>监控中心</h1>
      <div class="spacer"></div>
      <span class="faint" v-if="sys.isRoundRunning">采集中…</span>
      <span class="faint" v-else-if="sys.lastRoundAt">上次采集：{{ relTime(sys.lastRoundAt) }}</span>
      <button v-if="auth.role === 'admin'" class="btn ghost sm" :disabled="polling" @click="pollNow">
        {{ polling ? '采集中…' : '立即采集' }}
      </button>
      <label class="auto"><input type="checkbox" v-model="autoRefresh" /> 自动刷新</label>
    </div>

    <div class="stats">
      <div class="stat card" style="--tint: var(--accent)">
        <div class="stat-num">{{ printers.length }}</div>
        <div class="stat-label">打印机总数</div>
      </div>
      <div class="stat card" style="--tint: var(--ok)">
        <div class="stat-num ok">{{ countBy('ok') }}</div>
        <div class="stat-label">正常</div>
      </div>
      <div class="stat card" style="--tint: var(--warn)">
        <div class="stat-num warn">{{ countBy('warn') + countBy('error') }}</div>
        <div class="stat-label">告警中</div>
      </div>
      <div class="stat card" style="--tint: var(--offline)">
        <div class="stat-num off">{{ countBy('offline') }}</div>
        <div class="stat-label">离线</div>
      </div>
    </div>

    <div class="filters card">
      <div class="chip-row">
        <button
          v-for="f in filters"
          :key="f.key"
          class="chip"
          :class="{ active: status === f.key }"
          @click="status = f.key"
        >{{ f.label }} <span class="chip-n">{{ f.key === 'all' ? printers.length : countBy(f.key) }}</span></button>
      </div>
      <select v-model="location"><option value="">全部位置</option><option v-for="l in groups.locations" :key="l" :value="l">{{ l }}</option></select>
      <select v-model="department"><option value="">全部部门</option><option v-for="d in groups.departments" :key="d" :value="d">{{ d }}</option></select>
      <input v-model="keyword" placeholder="搜索名称 / IP…" class="search" />
    </div>

    <div v-if="loading" class="card empty">加载中…</div>
    <div v-else-if="filtered.length === 0" class="card empty">
      没有符合条件的打印机。<span v-if="auth.role === 'admin'">&nbsp;到「打印机管理」添加设备。</span>
    </div>

    <div v-for="grp in grouped" :key="grp.key" class="group">
      <div class="group-title" v-if="grp.key">{{ grp.key }}</div>
      <div class="card table-card">
        <table>
          <thead>
            <tr>
              <th>状态</th><th>名称</th><th>IP</th><th>耗材</th><th class="num">累计页数</th><th>最近采集</th><th>告警</th><th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="p in grp.items" :key="p.id" class="rowlink" @click="goDetail(p)">
              <td><span class="pill" :class="p.state"><span class="dot"></span>{{ stateText(p.state) }}</span></td>
              <td>
                <div class="p-name">{{ p.name }} <span v-if="!p.enabled" class="tag-disabled">停用</span></div>
                <div class="faint">{{ p.department || '—' }}</div>
              </td>
              <td class="mono">{{ p.ip }}:{{ p.port }}</td>
              <td>
                <div v-if="p.supplies.length === 0" class="faint">无数据</div>
                <SupplyBar v-for="s in p.supplies" :key="s.supplyIndex" :supply="s" />
              </td>
              <td class="mono num">{{ p.pageCount ?? '—' }}</td>
              <td class="muted">{{ relTime(p.lastPollAt) }}</td>
              <td>
                <span v-if="p.criticalAlertCount" class="pill error">{{ p.criticalAlertCount }} 严重</span>
                <span v-else-if="p.activeAlertCount" class="pill warn">{{ p.activeAlertCount }}</span>
                <span v-else class="faint">—</span>
              </td>
              <td @click.stop>
                <button v-if="auth.role === 'admin'" class="btn ghost sm" :disabled="p.polling" @click="pollOne(p)">刷新</button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  </div>
</template>

<script setup>
import { ref, reactive, computed, onMounted, onUnmounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api, auth, relTime, errMsg } from '../api'
import { showToast } from '../components/ui'
import SupplyBar from '../components/SupplyBar.vue'

const router = useRouter()
const printers = ref([])
const groups = ref({ locations: [], departments: [] })
const sys = reactive({ lastRoundAt: null, isRoundRunning: false })
const loading = ref(true)
const polling = ref(false)
const autoRefresh = ref(true)

const status = ref('all')
const location = ref('')
const department = ref('')
const keyword = ref('')

const filters = [
  { key: 'all', label: '全部' },
  { key: 'ok', label: '正常' },
  { key: 'warn', label: '警告' },
  { key: 'error', label: '严重' },
  { key: 'offline', label: '离线' }
]

function countBy(state) {
  return printers.value.filter(p => p.state === state).length
}

const filtered = computed(() =>
  printers.value.filter(p => {
    if (status.value !== 'all' && p.state !== status.value) return false
    if (location.value && p.location !== location.value) return false
    if (department.value && p.department !== department.value) return false
    return true
  })
)

const grouped = computed(() => {
  const map = new Map()
  for (const p of filtered.value) {
    const key = p.location || '未分组'
    if (!map.has(key)) map.set(key, [])
    map.get(key).push(p)
  }
  return [...map.entries()].map(([key, items]) => ({ key, items }))
})

function stateText(s) {
  return { ok: '正常', warn: '警告', error: '严重', offline: '离线', unknown: '未知' }[s] || s
}

async function load() {
  try {
    const { data } = await api.get('/printers/', { params: { status: status.value } })
    printers.value = data
    const meta = await api.get('/printers/meta/groups')
    groups.value = meta.data
    const info = await api.get('/system/info')
    sys.lastRoundAt = info.data.lastRoundAt
    sys.isRoundRunning = info.data.isRoundRunning
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    loading.value = false
  }
}

async function pollNow() {
  polling.value = true
  try {
    await api.post('/system/poll-now')
    showToast('已触发全量采集，稍候自动刷新')
    setTimeout(load, 4000)
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    polling.value = false
  }
}

async function pollOne(p) {
  p.polling = true
  try {
    await api.post(`/printers/${p.id}/poll`)
    await load()
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    p.polling = false
  }
}

function goDetail(p) {
  router.push(`/printers/${p.id}`)
}

let timer = null
onMounted(() => {
  load()
  timer = setInterval(() => { if (autoRefresh.value && !document.hidden) load() }, 15000)
})
onUnmounted(() => clearInterval(timer))
watch([status], load)
</script>

<style scoped>
.page-head { display: flex; align-items: center; gap: 12px; margin-bottom: 16px; }
.auto { display: flex; align-items: center; gap: 6px; color: var(--text-dim); font-size: 12.5px; margin: 0; cursor: pointer; }
.auto input { margin: 0; }
.stats { display: grid; grid-template-columns: repeat(4, 1fr); gap: 14px; margin-bottom: 14px; }
.stat {
  position: relative;
  padding: 14px 18px;
  overflow: hidden;
  transition: transform .15s ease, border-color .15s ease, box-shadow .15s ease;
}
.stat::before {
  content: '';
  position: absolute;
  left: 0; top: 0; bottom: 0;
  width: 3px;
  background: var(--tint, var(--accent));
  opacity: .9;
}
.stat:hover {
  transform: translateY(-2px);
  border-color: color-mix(in srgb, var(--tint, var(--accent)) 40%, var(--line));
}
.stat-num { font-size: 26px; font-weight: 700; }
.stat-num.ok { color: var(--ok); }
.stat-num.warn { color: var(--warn); }
.stat-num.off { color: var(--offline); }
.stat-label { color: var(--text-faint); font-size: 12.5px; }
.filters { display: flex; gap: 12px; align-items: center; padding: 12px 16px; margin-bottom: 16px; flex-wrap: wrap; }
.chip-row { display: flex; gap: 6px; flex-wrap: wrap; }
.chip {
  background: var(--bg-soft);
  border: 1px solid var(--line);
  color: var(--text-dim);
  border-radius: 18px;
  padding: 4px 12px;
  font-size: 12.5px;
  cursor: pointer;
}
.chip.active {
  background: color-mix(in srgb, var(--accent) 18%, transparent);
  border-color: var(--accent);
  color: var(--accent-2);
}
.chip.active .chip-n { opacity: .9; }
.chip-n { opacity: .65; margin-left: 3px; font-size: 11.5px; }
.search { width: 200px; }
.filters select { min-width: 120px; }
.group { margin-bottom: 16px; }
.group-title { font-size: 13px; color: var(--text-dim); margin: 0 2px 8px; font-weight: 600; }
.p-name { font-weight: 500; }
.tag-disabled {
  font-size: 11px;
  color: var(--text-faint);
  border: 1px solid var(--line);
  padding: 0 6px;
  border-radius: 4px;
  margin-left: 6px;
}
.num { text-align: right; }
@media (max-width: 900px) {
  .stats { grid-template-columns: repeat(2, 1fr); }
  .filters select, .search { width: 100% !important; min-width: 0; }
  .filters { align-items: stretch; flex-direction: column; }
  .search { width: 100%; }
}
</style>
