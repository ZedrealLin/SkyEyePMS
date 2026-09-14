<template>
  <div v-if="d">
    <div class="page-head">
      <button class="btn ghost sm" @click="$router.push('/dashboard')">← 返回监控中心</button>
      <h1>{{ d.printer.name }}</h1>
      <span class="pill" :class="state"><span class="dot"></span>{{ stateText }}</span>
      <div class="spacer"></div>
      <button v-if="auth.role === 'admin'" class="btn ghost sm" @click="pollOne">立即采集</button>
    </div>

    <div class="grid2">
      <div class="card">
        <h2>设备信息</h2>
        <table class="info">
          <tr><td class="k">IP 地址</td><td class="mono">{{ d.printer.ip }}:{{ d.printer.port }}</td></tr>
          <tr><td class="k">SNMP</td><td>v{{ d.printer.snmpVersion }}（community {{ d.communityMasked }}）</td></tr>
          <tr><td class="k">位置 / 部门</td><td>{{ d.printer.location || '—' }} / {{ d.printer.department || '—' }}</td></tr>
          <tr><td class="k">型号</td><td>{{ d.printer.model || '—' }}</td></tr>
          <tr><td class="k">序列号</td><td class="mono">{{ d.printer.serialNumber || '待采集' }}</td></tr>
          <tr><td class="k">资产编号</td><td>{{ d.printer.assetTag || '—' }}</td></tr>
          <tr><td class="k">投运日期</td><td>{{ d.printer.installedAt ? String(d.printer.installedAt).slice(0, 10) : '—' }}</td></tr>
          <tr><td class="k">系统名称</td><td>{{ d.printer.sysName || '—' }}</td></tr>
          <tr><td class="k">系统描述</td><td class="muted">{{ d.printer.sysDescr || '—' }}</td></tr>
          <tr><td class="k">打印机状态</td><td>{{ d.printer.hrStatusText || '—' }}</td></tr>
          <tr><td class="k">累计页数</td><td class="mono">{{ d.printer.pageCount ?? '—' }}</td></tr>
          <tr><td class="k">最近采集</td><td>{{ fmtTime(d.printer.lastPollAt) }}</td></tr>
          <tr><td class="k">最近成功</td><td>{{ fmtTime(d.printer.lastSuccessAt) }}</td></tr>
          <tr><td class="k">连续失败</td><td>{{ d.printer.consecutiveFailures }} 次</td></tr>
        </table>
      </div>

      <div class="card">
        <h2>耗材余量</h2>
        <div v-if="d.supplies.length === 0" class="empty">暂无耗材数据（设备可能未开放标准 MIB）</div>
        <div v-for="s in d.supplies" :key="s.supplyIndex" class="supply-row">
          <SupplyBar :supply="{ ...s, percent: s.percent != null ? +s.percent : null }" />
          <div v-if="forecastText(s.supplyIndex)" class="faint forecast">{{ forecastText(s.supplyIndex) }}</div>
        </div>
        <div class="faint" style="margin-top:10px">数据更新时间：{{ fmtTime(d.printer.updatedAt) }}</div>
      </div>
    </div>

    <div class="card" style="margin-top:14px">
      <h2>在线状态 / 页数（近 {{ hours }} 小时）</h2>
      <div ref="statusChart" class="chart"></div>
    </div>

    <div class="card" style="margin-top:14px">
      <h2>耗材变化（近 {{ hours }} 小时）</h2>
      <div v-if="supplySeries.length === 0" class="empty">暂无耗材历史（数据仅在余量变化时记录）</div>
      <div v-show="supplySeries.length" ref="supplyChart" class="chart"></div>
    </div>

    <div class="card" style="margin-top:14px">
      <h2>告警记录</h2>
      <div v-if="allAlerts.length === 0" class="empty">暂无告警</div>
      <table v-else>
        <thead><tr><th>级别</th><th>内容</th><th>状态</th><th>首次触发</th><th>最近推送</th></tr></thead>
        <tbody>
          <tr v-for="a in allAlerts" :key="a.id">
            <td><span class="pill" :class="sevClass(a.severity)">{{ sevText(a.severity) }}</span></td>
            <td>{{ a.message }}</td>
            <td>{{ a.status === 'active' ? '告警中' : '已恢复' }}</td>
            <td class="muted">{{ fmtTime(a.firstTriggeredAt) }}</td>
            <td class="muted">{{ fmtTime(a.lastNotifiedAt) }}</td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<script setup>
import { ref, computed, onMounted, onUnmounted, nextTick } from 'vue'
import { useRoute } from 'vue-router'
import * as echarts from 'echarts/core'
import { LineChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, LegendComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { api, auth, fmtTime, errMsg } from '../api'
import { showToast } from '../components/ui'
import SupplyBar from '../components/SupplyBar.vue'
import { chartColors } from '../theme'

echarts.use([LineChart, GridComponent, TooltipComponent, LegendComponent, CanvasRenderer])

const route = useRoute()
const id = route.params.id
const hours = 24
const d = ref(null)
const statusChart = ref(null)
const supplyChart = ref(null)
const supplySeries = ref([])
const forecastByIndex = ref({})
let charts = []

function forecastText(supplyIndex) {
  const f = forecastByIndex.value[supplyIndex]
  if (!f) return ''
  if (f.daysLeft == null) return '消耗平稳或历史数据不足，暂无耗尽风险'
  if (f.daysLeft <= 0) return '按当前速率预计已耗尽，请立即更换'
  const date = new Date(f.estimatedEmptyAt)
  const pad = n => String(n).padStart(2, '0')
  const daysDesc = f.daysLeft < 1 ? `${Math.max(1, Math.round(f.daysLeft * 24))} 小时` : `约 ${f.daysLeft} 天`
  return `日均消耗 ${f.dailyConsumptionPercent}%/天 · 预计 ${daysDesc}后耗尽（${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}）`
}

const state = computed(() => {
  const p = d.value?.printer
  if (!p) return 'unknown'
  if (p.lastOnline === false || p.consecutiveFailures >= 3) return 'offline'
  if (d.value.activeAlerts.some(a => a.severity === 'Critical')) return 'error'
  if (d.value.activeAlerts.length) return 'warn'
  return p.lastPollAt ? 'ok' : 'unknown'
})
const stateText = computed(() => ({ ok: '正常', warn: '警告', error: '严重', offline: '离线', unknown: '未知' }[state.value]))

function sevText(s) { return { Critical: '严重', Warning: '警告', Info: '提示' }[s] || s }
function sevClass(s) { return { Critical: 'error', Warning: 'warn', Info: 'ok' }[s] || 'unknown' }

const allAlerts = computed(() => [...(d.value?.activeAlerts || []), ...(d.value?.recentAlerts || [])]
  .filter((a, i, arr) => arr.findIndex(x => x.id === a.id) === i)
  .sort((a, b) => new Date(b.firstTriggeredAt) - new Date(a.firstTriggeredAt)))

function lineOption(times, series) {
  const c = chartColors()
  return {
    backgroundColor: 'transparent',
    textStyle: { color: c.text },
    tooltip: { trigger: 'axis' },
    legend: { textStyle: { color: c.text }, top: 0 },
    grid: { left: 46, right: 46, top: 30, bottom: 26 },
    xAxis: {
      type: 'category',
      data: times,
      axisLine: { lineStyle: { color: c.line } },
      axisLabel: { color: c.faint }
    },
    yAxis: series.map((s, i) => ({
      type: 'value',
      name: s.name,
      nameTextStyle: { color: c.faint },
      axisLine: { show: false },
      splitLine: { lineStyle: { color: c.line } },
      axisLabel: { color: c.faint },
      ...(i === 1 ? { splitLine: { show: false } } : {})
    })),
    series: series.map(s => ({
      name: s.name,
      type: 'line',
      data: s.data,
      step: s.step,
      showSymbol: false,
      lineStyle: { color: s.color, width: 1.6 },
      itemStyle: { color: s.color },
      yAxisIndex: s.y ?? 0,
      areaStyle: s.area ? { color: c.area } : undefined
    }))
  }
}

function renderCharts() {
  if (!d.value) return
  const samples = d.value.recentSamples || []
  const times = samples.map(s => fmtTime(s.collectedAt).slice(11))
  const online = samples.map(s => (s.online ? 1 : 0))
  const pages = samples.map(s => s.pageCount)
  const c = chartColors()

  charts.forEach(old => old.dispose())
  charts = []
  nextTick(() => {
    if (statusChart.value) {
      const chart = echarts.init(statusChart.value)
      chart.setOption(lineOption(times, [
        { name: '在线', data: online, color: c.ok, step: true, area: true },
        { name: '累计页数', data: pages, color: c.accent, y: 1 }
      ]))
      charts.push(chart)
    }
    if (supplyChart.value && supplySeries.value.length) {
      const chart = echarts.init(supplyChart.value)
      const times2 = supplySeries.value[0].times
      chart.setOption(lineOption(times2, supplySeries.value.map((s, i) => ({
        name: s.name,
        data: s.data,
        color: [c.accent, c.ok, c.warn, c.error, c.violet][i % 5]
      }))))
      charts.push(chart)
    }
  })
}

async function load() {
  try {
    const detail = await api.get(`/printers/${id}/detail`, { params: { hours } })
    d.value = detail.data

    api.get(`/printers/${id}/forecast`)
      .then(({ data }) => {
        const map = {}
        for (const f of data.items || []) map[f.supplyIndex] = f
        forecastByIndex.value = map
      })
      .catch(() => { /* 预测失败不影响主数据展示 */ })

    const sup = await api.get(`/printers/${id}/history`, { params: { metric: 'supply', hours } })
    const bySupply = new Map()
    for (const s of sup.data) {
      if (!bySupply.has(s.supplyIndex)) bySupply.set(s.supplyIndex, [])
      bySupply.get(s.supplyIndex).push(s)
    }
    supplySeries.value = [...bySupply.entries()].map(([idx, rows]) => ({
      name: d.value.supplies.find(x => x.supplyIndex === idx)?.name || `耗材 #${idx}`,
      times: rows.map(r => fmtTime(r.t).slice(11)),
      data: rows.map(r => (r.percent == null ? null : +r.percent))
    }))
    renderCharts()
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

async function pollOne() {
  try {
    await api.post(`/printers/${id}/poll`)
    showToast('采集完成')
    await load()
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

onMounted(() => {
  load()
  window.addEventListener('themechange', renderCharts)
})
onUnmounted(() => {
  window.removeEventListener('themechange', renderCharts)
  charts.forEach(c => c.dispose())
})
</script>

<style scoped>
.page-head { display: flex; align-items: center; gap: 12px; margin-bottom: 16px; }
.grid2 { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
@media (max-width: 900px) { .grid2 { grid-template-columns: 1fr; } .chart { height: 220px; } }
.supply-row { margin-bottom: 4px; }
.forecast { margin: 2px 0 10px; }
.info td { padding: 6px 8px; }
.info .k { color: var(--text-faint); width: 110px; }
.chart { height: 240px; }
</style>
