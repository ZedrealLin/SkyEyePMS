<template>
  <div>
    <div class="page-head">
      <h1>打印机管理</h1>
      <div class="spacer"></div>
      <button class="btn ghost" @click="discoveryOpen = true">网段扫描</button>
      <button class="btn" @click="openCreate">添加打印机</button>
    </div>

    <div class="card">
      <div v-if="loading" class="empty">加载中…</div>
      <div v-else-if="printers.length === 0" class="empty">还没有打印机，点击右上角「添加打印机」</div>
      <table v-else>
        <thead>
          <tr><th>名称</th><th>IP:端口</th><th>SNMP</th><th>位置</th><th>部门</th><th>状态</th><th>最近采集</th><th style="width:190px"></th></tr>
        </thead>
        <tbody>
          <tr v-for="p in printers" :key="p.id">
            <td>{{ p.name }}</td>
            <td class="mono">{{ p.ip }}:{{ p.port }}</td>
            <td>v{{ p.snmpVersion }}</td>
            <td>{{ p.location || '—' }}</td>
            <td>{{ p.department || '—' }}</td>
            <td>
              <span class="pill" :class="p.enabled ? 'ok' : 'unknown'">
                <span class="dot"></span>{{ p.enabled ? '启用' : '停用' }}
              </span>
            </td>
            <td class="muted">{{ relTime(p.lastPollAt) }}</td>
            <td style="text-align:right">
              <button class="btn ghost sm" @click="openEdit(p)">编辑</button>
              <button class="btn ghost sm" @click="del(p)">删除</button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- 新建 / 编辑 -->
    <div v-if="dialog" class="modal-mask" @click.self="dialog = false">
      <div class="modal">
        <h2>{{ form.id ? '编辑打印机' : '添加打印机' }}</h2>
        <div class="form-grid">
          <div class="full">
            <label>名称 *</label>
            <input v-model="form.name" placeholder="如：3楼东侧-财务部" style="width:100%" />
          </div>
          <div>
            <label>IP 地址 *</label>
            <input v-model="form.ip" placeholder="192.168.1.100" />
          </div>
          <div>
            <label>SNMP 端口</label>
            <input v-model.number="form.port" type="number" />
          </div>
          <div>
            <label>SNMP Community {{ form.id ? '' : '*' }}</label>
            <input v-model="form.community" :placeholder="form.id ? '留空保持不变' : 'public'" />
          </div>
          <div>
            <label>SNMP 版本</label>
            <select v-model.number="form.snmpVersion">
              <option :value="2">v2c（推荐）</option>
              <option :value="1">v1</option>
            </select>
          </div>
          <div>
            <label>位置</label>
            <input v-model="form.location" placeholder="如：3楼东侧" />
          </div>
          <div>
            <label>部门</label>
            <input v-model="form.department" placeholder="如：财务部" />
          </div>
          <div>
            <label>耗材低阈值覆盖（%）</label>
            <input v-model="form.lowSupplyPercentOverride" type="number" min="1" max="99" placeholder="留空用全局阈值" />
          </div>
          <div>
            <label>设备型号</label>
            <input v-model="form.model" placeholder="如：DocuCentre-V C3373" />
          </div>
          <div>
            <label>序列号（留空自动采集）</label>
            <input v-model="form.serialNumber" placeholder="SNP 序列号" />
          </div>
          <div>
            <label>资产编号</label>
            <input v-model="form.assetTag" placeholder="公司资产系统编号" />
          </div>
          <div>
            <label>投运日期</label>
            <input v-model="form.installedAt" type="date" />
          </div>
          <div class="full">
            <label class="chk"><input type="checkbox" v-model="form.enabled" /> 启用采集</label>
          </div>
        </div>
        <div v-if="testResult" class="test-result" :class="{ ok: testResult.success, fail: !testResult.success }">
          <template v-if="testResult.success">
            连接成功（{{ testResult.elapsedMs }}ms）：{{ testResult.sysDescr || testResult.sysName || '设备响应正常' }}
          </template>
          <template v-else>连接失败：{{ testResult.error }}</template>
        </div>
        <div class="modal-actions">
          <button class="btn ghost" :disabled="testing" @click="test">{{ testing ? '测试中…' : '测试连接' }}</button>
          <div class="spacer"></div>
          <button class="btn ghost" @click="dialog = false">取消</button>
          <button class="btn" :disabled="saving" @click="save">{{ saving ? '保存中…' : '保存' }}</button>
        </div>
      </div>
    </div>

    <!-- 网段扫描 -->
    <div v-if="discoveryOpen" class="modal-mask" @click.self="discoveryOpen = false">
      <div class="modal">
        <h2>网段 SNMP 自动发现</h2>
        <div class="form-grid">
          <div>
            <label>起始 IP</label>
            <input v-model="scan.startIp" placeholder="192.168.1.1" />
          </div>
          <div>
            <label>结束 IP</label>
            <input v-model="scan.endIp" placeholder="192.168.1.254" />
          </div>
          <div>
            <label>SNMP 端口</label>
            <input v-model.number="scan.port" type="number" />
          </div>
          <div>
            <label>Community</label>
            <input v-model="scan.community" />
          </div>
        </div>
        <div class="modal-actions" style="margin-top:12px">
          <span class="faint" v-if="scanning">
            已扫描 {{ scanProgress.scanned }} / {{ scanProgress.total }} 个地址（{{ pct }}）
          </span>
          <span class="faint" v-else>共 {{ ipCount }} 个地址，扫描在后台执行，可实时查看进度</span>
          <div class="spacer"></div>
          <button class="btn" :disabled="scanning || !scan.startIp || !scan.endIp" @click="doScan">
            {{ scanning ? '扫描中…' : (scanned ? '重新扫描' : '开始扫描') }}
          </button>
        </div>
        <div v-if="scanning || scanProgress.total" class="bar" style="margin-top:8px;min-width:0">
          <div class="fill high" :style="{ width: pct }"></div>
        </div>
        <div v-if="hits.length" style="margin-top:12px">
          <table>
            <thead><tr><th></th><th>IP</th><th>设备</th></tr></thead>
            <tbody>
              <tr v-for="h in hits" :key="h.ip">
                <td><input type="checkbox" v-model="h.checked" /></td>
                <td class="mono">{{ h.ip }}</td>
                <td class="muted">{{ h.sysName || '—' }} · {{ h.sysDescr }}</td>
              </tr>
            </tbody>
          </table>
          <div class="modal-actions">
            <button class="btn" @click="addSelected">添加选中的 {{ checkedCount }} 台</button>
          </div>
        </div>
        <div v-else-if="scanned && !hits.length" class="empty">范围内没有发现支持 SNMP 的设备</div>
      </div>
    </div>
  </div>
</template>

<script setup>
import { ref, reactive, computed, onMounted, onUnmounted } from 'vue'
import { api, errMsg, relTime } from '../api'
import { showToast } from '../components/ui'

const printers = ref([])
const loading = ref(true)
const dialog = ref(false)
const saving = ref(false)
const testing = ref(false)
const testResult = ref(null)
const discoveryOpen = ref(false)
const scanning = ref(false)
const scanned = ref(false)
const scanProgress = reactive({ scanned: 0, total: 0 })
let scanJobId = ''
let scanTimer = null

const pct = computed(() => {
  const total = Math.max(1, scanProgress.total)
  return `${Math.min(100, Math.round(scanProgress.scanned * 100 / total))}%`
})
const hits = ref([])

const blank = {
  id: 0, name: '', ip: '', port: 161, community: '', snmpVersion: 2, location: '', department: '',
  enabled: true, lowSupplyPercentOverride: null, model: '', serialNumber: '', assetTag: '', installedAt: ''
}
const form = reactive({ ...blank })
const scan = reactive({ startIp: '', endIp: '', port: 161, community: 'public' })

const ipCount = computed(() => {
  const s = scan.startIp.split('.').map(Number)
  const e = scan.endIp.split('.').map(Number)
  if (s.length !== 4 || e.length !== 4 || s.some(isNaN) || e.some(isNaN)) return 0
  return Math.min(4096, (e[3] - s[3]) + 1 + (e[2] - s[2]) * 256)
})
const checkedCount = computed(() => hits.value.filter(h => h.checked).length)

async function load() {
  loading.value = true
  try {
    const { data } = await api.get('/printers/', { params: { status: 'all' } })
    printers.value = data
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    loading.value = false
  }
}

function openCreate() {
  Object.assign(form, blank)
  testResult.value = null
  dialog.value = true
}

function openEdit(p) {
  Object.assign(form, {
    ...p,
    community: '',
    lowSupplyPercentOverride: p.supplyLowPercentOverride ?? null,
    model: p.model || '',
    serialNumber: p.serialNumber || '',
    assetTag: p.assetTag || '',
    installedAt: p.installedAt ? String(p.installedAt).slice(0, 10) : ''
  })
  testResult.value = null
  dialog.value = true
}

async function test() {
  if (!form.ip) { showToast('请先填写 IP', true); return }
  testing.value = true
  testResult.value = null
  try {
    const { data } = await api.post('/printers/test', {
      ip: form.ip, port: form.port, community: form.community || 'public', snmpVersion: form.snmpVersion
    })
    testResult.value = data
  } catch (e) {
    testResult.value = { success: false, error: errMsg(e) }
  } finally {
    testing.value = false
  }
}

async function save() {
  saving.value = true
  try {
    // 空字符串归一化为 null（= 使用全局阈值）
    const payload = {
      ...form,
      lowSupplyPercentOverride:
        form.lowSupplyPercentOverride === '' || form.lowSupplyPercentOverride == null
          ? null
          : Number(form.lowSupplyPercentOverride),
      model: form.model || null,
      serialNumber: form.serialNumber || null,
      assetTag: form.assetTag || null,
      installedAt: form.installedAt || null
    }
    if (form.id) {
      await api.put(`/printers/${form.id}`, payload)
      showToast('已保存')
    } else {
      await api.post('/printers', payload)
      showToast('已添加')
    }
    dialog.value = false
    load()
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    saving.value = false
  }
}

async function del(p) {
  if (!confirm(`确定删除「${p.name}」？历史数据将保留。`)) return
  try {
    await api.delete(`/printers/${p.id}`)
    showToast('已删除')
    load()
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

async function doScan() {
  scanning.value = true
  scanned.value = false
  hits.value = []
  scanProgress.scanned = 0
  scanProgress.total = ipCount.value
  try {
    const { data } = await api.post('/discovery/scan', { ...scan, snmpVersion: 2, timeoutMs: 1200 })
    scanJobId = data.jobId
    scanProgress.total = data.total || scanProgress.total
    scanTimer = setInterval(pollScan, 800)
    pollScan()
  } catch (e) {
    showToast(errMsg(e), true)
    scanning.value = false
  }
}

async function pollScan() {
  if (!scanJobId) return
  if (!discoveryOpen.value) { stopScan(); return }
  try {
    const { data } = await api.get(`/discovery/scan/${scanJobId}`)
    scanProgress.scanned = data.scanned
    scanProgress.total = data.total
    hits.value = data.hits.map(h => {
      const existing = hits.value.find(x => x.ip === h.ip)
      return existing || { ...h, checked: true }
    })
    if (data.error) {
      showToast(`扫描失败：${data.error}`, true)
      stopScan()
    } else if (data.done) {
      stopScan()
      scanned.value = true
      if (hits.value.length) showToast(`扫描完成，发现 ${hits.value.length} 台设备`)
    }
  } catch (e) {
    stopScan()
    showToast(errMsg(e), true)
  }
}

function stopScan() {
  scanning.value = false
  if (scanTimer) { clearInterval(scanTimer); scanTimer = null }
}

async function addSelected() {
  const picked = hits.value.filter(h => h.checked)
  let ok = 0
  for (const h of picked) {
    try {
      await api.post('/printers', {
        name: h.sysName || `打印机-${h.ip}`,
        ip: h.ip, port: scan.port, community: scan.community,
        snmpVersion: 2, enabled: true
      })
      ok++
    } catch { /* 重复等错误忽略 */ }
  }
  showToast(`已添加 ${ok} 台`)
  discoveryOpen.value = false
  load()
}

onMounted(load)
onUnmounted(stopScan)
</script>

<style scoped>
.page-head { display: flex; align-items: center; gap: 10px; margin-bottom: 16px; }
.chk { display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--text); cursor: pointer; }
.chk input { margin: 0; }
.test-result {
  margin-top: 12px;
  border-radius: 8px;
  padding: 9px 12px;
  font-size: 12.5px;
}
.test-result.ok { background: rgba(46, 204, 113, .1); color: var(--ok); }
.test-result.fail { background: rgba(255, 82, 82, .1); color: var(--error); }
</style>
