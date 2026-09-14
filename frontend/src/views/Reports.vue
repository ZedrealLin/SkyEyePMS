<template>
  <div>
    <div class="page-head">
      <h1>报表统计</h1>
      <div class="spacer"></div>
      <select v-model.number="days" style="width:130px">
        <option :value="7">近 7 天</option>
        <option :value="30">近 30 天</option>
        <option :value="90">近 90 天</option>
      </select>
    </div>

    <div class="card">
      <div class="card-head">
        <h2>运维质量（近 {{ qualityDays }} 天）</h2>
        <div class="spacer"></div>
        <div class="chip-row">
          <button v-for="d in [7, 30]" :key="d" class="chip" :class="{ active: qualityDays === d }"
            @click="qualityDays = d">{{ d }} 天</button>
        </div>
      </div>
      <div v-if="!quality" class="empty">加载中…</div>
      <div v-else class="q-grid">
        <div class="q-item">
          <div class="q-num">{{ quality.mttaMinutes != null ? `${quality.mttaMinutes} 分` : '—' }}</div>
          <div class="q-label">平均确认响应（MTTA）</div>
        </div>
        <div class="q-item">
          <div class="q-num">{{ quality.ackRatePercent != null ? `${quality.ackRatePercent}%` : '—' }}</div>
          <div class="q-label">确认率（{{ quality.acknowledgedCount }}/{{ quality.triggeredCount }}）</div>
        </div>
        <div class="q-item">
          <div class="q-num" :class="{ low: quality.notifySuccessPercent != null && quality.notifySuccessPercent < 95 }">
            {{ quality.notifySuccessPercent != null ? `${quality.notifySuccessPercent}%` : '—' }}
          </div>
          <div class="q-label">通知成功率（{{ quality.notifySuccesses }}/{{ quality.notifyAttempts }}）</div>
        </div>
        <div class="q-item">
          <div class="q-num" :class="{ low: quality.currentlyCriticalActive > 0 }">{{ quality.currentlyActive }}</div>
          <div class="q-label">当前告警中{{ quality.currentlyCriticalActive > 0 ? `（严重 ${quality.currentlyCriticalActive}）` : '' }}</div>
        </div>
        <div class="q-item">
          <div class="q-num">{{ quality.criticalCount }}</div>
          <div class="q-label">期间严重告警</div>
        </div>
        <div class="q-item">
          <div class="q-num">{{ quality.resolvedCount }}</div>
          <div class="q-label">期间已恢复</div>
        </div>
      </div>
    </div>

    <div class="card" style="margin-top:14px">
      <div class="card-head">
        <h2>使用统计与可用率</h2>
        <div class="spacer"></div>
        <div class="chip-row">
          <button v-for="d in usageRanges" :key="d" class="chip" :class="{ active: usageDays === d }"
            @click="usageDays = d">{{ d }} 天</button>
        </div>
        <button class="btn ghost sm" @click="exportUsage">导出 CSV</button>
      </div>
      <div v-if="usage.length === 0" class="empty">暂无数据</div>
      <table v-else>
        <thead>
          <tr><th>打印机</th><th>位置</th><th>部门</th><th class="num">打印页数</th><th class="num">可用率</th><th class="num">采样数</th><th class="num">当前页数</th></tr>
        </thead>
        <tbody>
          <tr v-for="u in usage" :key="u.printerId">
            <td>
              {{ u.printerName }}
              <span v-if="!u.enabled" class="tag-disabled">停用</span>
            </td>
            <td class="muted">{{ u.location || '—' }}</td>
            <td class="muted">{{ u.department || '—' }}</td>
            <td class="mono num">{{ u.pagesPrinted }}</td>
            <td class="mono num">
              <span v-if="u.availabilityPercent == null" class="faint">—</span>
              <span v-else :class="availClass(u.availabilityPercent)">{{ u.availabilityPercent }}%</span>
            </td>
            <td class="mono num">{{ u.sampleCount }}</td>
            <td class="mono num">{{ u.currentPageCount ?? '—' }}</td>
          </tr>
        </tbody>
      </table>
    </div>

    <div class="card" style="margin-top:14px">
      <div class="card-head">
        <h2>耗材耗尽预测</h2>
        <div class="spacer"></div>
        <span class="faint">基于近 {{ forecastWindowDays }} 天消耗速率拟合（系统设置中可调）</span>
        <button class="btn ghost sm" @click="exportForecast">导出 CSV</button>
      </div>
      <div v-if="forecast.length === 0" class="empty">暂无耗材数据</div>
      <table v-else>
        <thead>
          <tr><th>打印机</th><th>耗材</th><th class="num">当前余量</th><th class="num">日均消耗</th><th class="num">预计剩余</th><th>预计耗尽时间</th></tr>
        </thead>
        <tbody>
          <tr v-for="f in forecast" :key="`${f.printerId}-${f.supplyIndex}`">
            <td>{{ f.printerName }} <span class="faint">{{ f.location }}</span></td>
            <td>{{ f.supplyName }}</td>
            <td class="mono num">
              <span v-if="f.percent == null" class="faint">无数据</span>
              <span v-else>{{ f.percent }}%</span>
            </td>
            <td class="mono num">{{ f.dailyConsumptionPercent != null ? `${f.dailyConsumptionPercent}%/天` : '—' }}</td>
            <td class="mono num">
              <span v-if="f.daysLeft == null" class="faint">平稳 / 数据不足</span>
              <span v-else :class="daysClass(f.daysLeft)">{{ daysText(f.daysLeft) }}</span>
            </td>
            <td class="muted">
              <template v-if="f.estimatedEmptyAt">
                {{ fmtDate(f.estimatedEmptyAt) }}
                <span v-if="f.daysLeft <= 3" class="pill error">尽快更换</span>
                <span v-else-if="f.daysLeft <= 7" class="pill warn">计划采购</span>
              </template>
              <template v-else>—</template>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div class="card" style="margin-top:14px">
      <div class="card-head">
        <h2>部门抄表</h2>
        <div class="spacer"></div>
        <div class="chip-row">
          <button v-for="d in meterRanges" :key="d" class="chip" :class="{ active: meterDays === d }"
            @click="meterDays = d">{{ d }} 天</button>
        </div>
        <button class="btn ghost sm" @click="exportMeter">导出 CSV（计费）</button>
      </div>
      <div v-if="meter.length === 0" class="empty">暂无数据</div>
      <table v-else>
        <thead><tr><th>部门</th><th class="num">打印机数</th><th class="num">累计页数</th><th class="num">平均可用率</th></tr></thead>
        <tbody>
          <tr v-for="m in meter" :key="m.department || '__none__'">
            <td>{{ m.department || '未分配' }}</td>
            <td class="mono num">{{ m.printerCount }}</td>
            <td class="mono num">{{ m.totalPages }}</td>
            <td class="mono num">{{ m.avgAvailabilityPercent != null ? `${m.avgAvailabilityPercent}%` : '—' }}</td>
          </tr>
        </tbody>
      </table>
    </div>

    <div class="card" style="margin-top:14px">
      <div class="card-head">
        <h2>打印量趋势</h2>
        <div class="spacer"></div>
        <div class="chip-row">
          <button v-for="d in trendRanges" :key="d" class="chip" :class="{ active: trendDays === d }"
            @click="trendDays = d">{{ d }} 天</button>
        </div>
      </div>
      <div v-show="trendHasData" ref="trendChart" class="trend-chart"></div>
      <div v-if="!trendHasData" class="empty">暂无日粒度数据（汇总任务每日运行，历史由保留策略决定）</div>
    </div>

    <div class="card" style="margin-top:14px">
      <div class="card-head">
        <h2>故障次数排行</h2>
        <div class="spacer"></div>
        <button class="btn ghost sm" @click="exportAlerts">导出告警 CSV</button>
      </div>
      <div v-if="ranking.length === 0" class="empty">所选时段内没有故障记录</div>
      <table v-else>
        <thead><tr><th>#</th><th>打印机</th><th>位置</th><th class="num">告警次数</th><th class="num">其中离线</th><th>分布</th></tr></thead>
        <tbody>
          <tr v-for="(r, i) in ranking" :key="r.printerId">
            <td class="mono">{{ i + 1 }}</td>
            <td>{{ r.printerName }}</td>
            <td class="muted">{{ r.location || '—' }}</td>
            <td class="mono num">{{ r.alertCount }}</td>
            <td class="mono num">{{ r.offlineCount }}</td>
            <td style="width:34%">
              <div class="bar high"><div class="fill" :style="{ width: pct(r.alertCount, maxCount) }"></div></div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div class="card" style="margin-top:14px">
      <h2>耗材更换频率（余量大幅回升次数）</h2>
      <div v-if="replacements.length === 0" class="empty">所选时段内未检测到耗材更换</div>
      <table v-else>
        <thead><tr><th>打印机</th><th>耗材</th><th class="num">更换次数</th><th>频率</th></tr></thead>
        <tbody>
          <tr v-for="r in replacements" :key="`${r.printerId}-${r.supplyIndex}`">
            <td>{{ r.printerName }}</td>
            <td>{{ r.supplyName || `耗材 #${r.supplyIndex}` }}</td>
            <td class="mono num">{{ r.replacements }}</td>
            <td style="width:40%">
              <div class="bar mid"><div class="fill" :style="{ width: pct(r.replacements, maxReplace) }"></div></div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<script setup>
import { ref, computed, onMounted, onUnmounted, watch, nextTick } from 'vue'
import * as echarts from 'echarts/core'
import { BarChart, LineChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, LegendComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { api, errMsg, downloadFile } from '../api'
import { showToast } from '../components/ui'
import { chartColors } from '../theme'

echarts.use([BarChart, LineChart, GridComponent, TooltipComponent, LegendComponent, CanvasRenderer])

const days = ref(30)
const ranking = ref([])
const replacements = ref([])

const usageRanges = [1, 7, 30]
const usageDays = ref(7)
const usage = ref([])
const forecast = ref([])
const forecastWindowDays = ref(14)

const qualityRanges = [7, 30]
const qualityDays = ref(7)
const quality = ref(null)

const meterRanges = [7, 30, 90]
const meterDays = ref(30)
const meter = ref([])

const trendRanges = [30, 90, 365]
const trendDays = ref(30)
const trendHasData = ref(false)
const trendChart = ref(null)
let trendInstance = null

const maxCount = computed(() => Math.max(1, ...ranking.value.map(r => r.alertCount)))
const maxReplace = computed(() => Math.max(1, ...replacements.value.map(r => r.replacements)))

function pct(v, max) { return `${Math.max(4, Math.round((v / max) * 100))}%` }

function availClass(v) {
  if (v >= 99) return 'avail ok'
  if (v >= 95) return 'avail mid'
  return 'avail low'
}

function daysText(d) {
  if (d <= 0) return '已耗尽'
  if (d < 1) return `${Math.max(1, Math.round(d * 24))} 小时`
  return `${d} 天`
}

function daysClass(d) {
  if (d <= 3) return 'days low'
  if (d <= 7) return 'days mid'
  return 'days ok'
}

function fmtDate(iso) {
  const d = new Date(iso)
  const pad = n => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

function stamp() {
  const d = new Date()
  const pad = n => String(n).padStart(2, '0')
  return `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}_${pad(d.getHours())}${pad(d.getMinutes())}`
}

async function loadSummary() {
  try {
    const { data } = await api.get('/reports/summary', { params: { days: days.value } })
    ranking.value = data.faultRanking
    replacements.value = data.supplyReplacements
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

async function loadUsage() {
  try {
    const { data } = await api.get('/reports/usage', { params: { days: usageDays.value } })
    usage.value = data.items
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

async function loadForecast() {
  try {
    const { data } = await api.get('/reports/forecast')
    forecast.value = data.items
    forecastWindowDays.value = data.windowDays || 14
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

async function loadQuality() {
  try {
    const { data } = await api.get('/reports/quality', { params: { days: qualityDays.value } })
    quality.value = data
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

async function loadMeter() {
  try {
    const { data } = await api.get('/reports/meter', { params: { days: meterDays.value } })
    meter.value = data.departments
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

async function loadTrend() {
  try {
    const { data } = await api.get('/reports/daily', { params: { days: trendDays.value } })
    trendHasData.value = (data.items || []).length > 0
    if (!trendHasData.value) return
    await nextTick()
    const c = chartColors()
    if (!trendInstance) trendInstance = echarts.init(trendChart.value)
    trendInstance.setOption({
      backgroundColor: 'transparent',
      tooltip: { trigger: 'axis' },
      legend: { textStyle: { color: c.text }, top: 0 },
      grid: { left: 50, right: 50, top: 30, bottom: 28 },
      xAxis: {
        type: 'category',
        data: data.items.map(i => i.date.slice(5)),
        axisLine: { lineStyle: { color: c.line } },
        axisLabel: { color: c.faint }
      },
      yAxis: [
        { type: 'value', name: '页数', nameTextStyle: { color: c.faint }, splitLine: { lineStyle: { color: c.line } }, axisLabel: { color: c.faint } },
        { type: 'value', name: '可用率%', max: 100, nameTextStyle: { color: c.faint }, splitLine: { show: false }, axisLabel: { color: c.faint } }
      ],
      series: [
        {
          name: '打印页数', type: 'bar', data: data.items.map(i => i.totalPages),
          itemStyle: { color: c.accent, borderRadius: [3, 3, 0, 0] }, barMaxWidth: 18
        },
        {
          name: '可用率%', type: 'line', yAxisIndex: 1, smooth: true,
          data: data.items.map(i => i.avgAvailability ?? null),
          lineStyle: { color: c.ok, width: 1.6 }, itemStyle: { color: c.ok }, showSymbol: false
        }
      ]
    })
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

function onResize() { trendInstance?.resize() }

async function exportMeter() {
  try {
    await downloadFile(`/api/reports/meter/export.csv?days=${meterDays.value}`, `skyeye_meter_${stamp()}.csv`)
    showToast('已导出部门抄表 CSV')
  } catch (e) { showToast(errMsg(e), true) }
}

async function exportUsage() {
  try {
    await downloadFile(`/api/reports/usage/export.csv?days=${usageDays.value}`, `skyeye_usage_${stamp()}.csv`)
    showToast('已导出使用统计 CSV')
  } catch (e) { showToast(errMsg(e), true) }
}

async function exportForecast() {
  try {
    await downloadFile('/api/reports/forecast/export.csv', `skyeye_forecast_${stamp()}.csv`)
    showToast('已导出耗尽预测 CSV')
  } catch (e) { showToast(errMsg(e), true) }
}

async function exportAlerts() {
  try {
    await downloadFile('/api/alerts/export.csv', `skyeye_alerts_${stamp()}.csv`)
    showToast('已导出告警 CSV')
  } catch (e) { showToast(errMsg(e), true) }
}

onMounted(() => { loadSummary(); loadUsage(); loadForecast(); loadQuality(); loadMeter(); loadTrend(); window.addEventListener('resize', onResize) })
onUnmounted(() => { window.removeEventListener('resize', onResize); trendInstance?.dispose(); trendInstance = null })
watch(days, loadSummary)
watch(usageDays, loadUsage)
watch(qualityDays, loadQuality)
watch(meterDays, loadMeter)
watch(trendDays, loadTrend)
</script>

<style scoped>
.page-head { display: flex; align-items: center; gap: 12px; margin-bottom: 16px; }
.card-head { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }
.card-head h2 { margin: 0; }
.num { text-align: right; }
.chip-row { display: flex; gap: 6px; }
.chip {
  background: var(--bg-soft);
  border: 1px solid var(--line);
  color: var(--text-dim);
  border-radius: 18px;
  padding: 3px 12px;
  font-size: 12.5px;
  cursor: pointer;
}
.chip.active { background: color-mix(in srgb, var(--accent) 18%, transparent); border-color: var(--accent); color: var(--accent-2); }
.avail.ok { color: var(--ok); }
.avail.mid { color: var(--warn); }
.avail.low { color: var(--error); }
.days.ok { color: var(--text-dim); }
.days.mid { color: var(--warn); }
.days.low { color: var(--error); font-weight: 600; }
.q-grid { display: grid; grid-template-columns: repeat(6, 1fr); gap: 10px; }
.trend-chart { height: 300px; width: 100%; }
.q-item { background: var(--bg-soft); border: 1px solid var(--line); border-radius: 10px; padding: 10px 14px; }
.q-num { font-size: 20px; font-weight: 700; }
.q-num.low { color: var(--error); }
.q-label { font-size: 12px; color: var(--text-faint); margin-top: 2px; }
@media (max-width: 1100px) { .q-grid { grid-template-columns: repeat(3, 1fr); } }
.tag-disabled {
  font-size: 11px;
  color: var(--text-faint);
  border: 1px solid var(--line);
  padding: 0 6px;
  border-radius: 4px;
  margin-left: 6px;
}
</style>
