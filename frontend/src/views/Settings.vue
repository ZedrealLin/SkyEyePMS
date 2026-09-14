<template>
  <div>
    <div class="page-head"><h1>系统设置</h1></div>
    <div v-if="!s" class="card empty">加载中…</div>
    <template v-else>
      <div class="grid2">
        <div class="card">
          <h2>告警阈值</h2>
          <div class="field">
            <label>耗材低告警阈值（%）</label>
            <input v-model.number="s.thresholds.supplyLowPercent" type="number" min="1" max="99" style="width:120px" />
          </div>
          <div class="field">
            <label>耗材严重告警阈值（%，低于此值升级为严重）</label>
            <input v-model.number="s.thresholds.supplyCriticalPercent" type="number" min="0" max="99" style="width:120px" />
          </div>
          <div class="field">
            <label>连续采集失败多少次判定离线</label>
            <input v-model.number="s.thresholds.offlineConsecutiveFailures" type="number" min="1" max="60" style="width:120px" />
          </div>
          <div class="field">
            <label>同一告警静默期（分钟，期内不重复推送）</label>
            <input v-model.number="s.thresholds.alertSilenceMinutes" type="number" min="1" max="1440" style="width:120px" />
          </div>
          <button class="btn" :disabled="savingThresholds" @click="saveThresholds">保存阈值</button>
        </div>

        <div class="card">
          <h2>轮询与采集</h2>
          <div class="ret-grid">
            <div class="field"><label>轮询间隔（分钟，下轮生效）</label><input v-model.number="s.polling.intervalMinutes" type="number" min="1" max="1440" /></div>
            <div class="field"><label>并发采集台数上限</label><input v-model.number="s.polling.concurrency" type="number" min="1" max="100" /></div>
            <div class="field full-w"><label>耗材采样最小变化（百分点，变化达到才记录历史）</label><input v-model.number="s.sampling.supplySampleMinChangePercent" type="number" min="1" max="50" /></div>
          </div>
          <button class="btn" :disabled="savingPolling" @click="savePolling">保存轮询与采集参数</button>
        </div>
      </div>

      <div class="grid2" style="margin-top:14px">
        <div class="card">
          <h2>分析与报表</h2>
          <div class="field">
            <label>耗材耗尽预测窗口（天，回看多久的历史）</label>
            <input v-model.number="s.analysis.forecastWindowDays" type="number" min="2" max="90" style="width:120px" />
          </div>
          <div class="field">
            <label>耗材更换判定（余量回升百分点数）</label>
            <input v-model.number="s.analysis.supplyReplacementRisePercent" type="number" min="2" max="100" style="width:120px" />
          </div>
          <button class="btn" :disabled="savingAnalysis" @click="saveAnalysis">保存分析参数</button>
        </div>

        <div class="card">
          <h2>历史数据保留</h2>
          <div class="ret-grid">
            <div class="field"><label>状态采样（天）</label><input v-model.number="s.retention.statusHistoryDays" type="number" min="1" /></div>
            <div class="field"><label>耗材采样（天）</label><input v-model.number="s.retention.supplyHistoryDays" type="number" min="1" /></div>
            <div class="field"><label>已恢复告警（天）</label><input v-model.number="s.retention.resolvedAlertDays" type="number" min="1" /></div>
            <div class="field"><label>通知日志（天）</label><input v-model.number="s.retention.notificationLogDays" type="number" min="1" /></div>
            <div class="field"><label>审计日志（天，最少 7）</label><input v-model.number="s.retention.auditLogDays" type="number" min="7" max="3650" /></div>
          </div>
          <button class="btn" :disabled="savingRetention" @click="saveRetention">保存保留策略</button>
        </div>
      </div>

      <div class="grid2" style="margin-top:14px">
        <div class="card">
          <h2>周期报表推送</h2>
          <div class="field">
            <label class="inline-chk"><input type="checkbox" v-model="s.reportPush.enabled" /> 启用定时推送</label>
          </div>
          <div class="ret-grid">
            <div class="field"><label>推送日</label>
              <select v-model.number="s.reportPush.dayOfWeek">
                <option :value="1">每周一</option><option :value="2">每周二</option><option :value="3">每周三</option>
                <option :value="4">每周四</option><option :value="5">每周五</option><option :value="6">每周六</option>
                <option :value="0">每周日</option>
              </select>
            </div>
            <div class="field"><label>时刻（本地时间）</label>
              <div class="row2">
                <select v-model.number="s.reportPush.hour"><option v-for="h in 24" :key="h" :value="h - 1">{{ String(h - 1).padStart(2, '0') }} 时</option></select>
                <select v-model.number="s.reportPush.minute"><option :value="0">00 分</option><option :value="15">15 分</option><option :value="30">30 分</option><option :value="45">45 分</option></select>
              </div>
            </div>
          </div>
          <div class="field">
            <label>推送渠道（多选）</label>
            <div class="chips">
              <label v-for="c in pushChannels" :key="c" class="inline-chk">
                <input type="checkbox" :value="c" v-model="s.reportPush.channels" /> {{ c }}
              </label>
            </div>
          </div>
          <div class="faint" v-if="s.reportPush.lastSentAt" style="margin-bottom:8px">上次推送：{{ fmtTime(s.reportPush.lastSentAt) }}</div>
          <button class="btn" :disabled="savingPush" @click="savePush">保存推送计划</button>
        </div>
        <div class="card">
          <h2>审计提示</h2>
          <p class="faint" style="margin:0 0 8px">
            打印机增删改、告警确认/恢复、设置修改、用户管理等操作会记录到「审计日志」，
            可在左侧菜单查看操作人与时间。
          </p>
        </div>
      </div>

      <h2 style="margin:18px 0 10px">通知渠道（可同时启用多个）</h2>
      <div class="grid2">
        <div class="card" v-for="ch in channels" :key="ch.key">
          <div class="ch-head">
            <h3>{{ ch.title }}</h3>
            <label class="switch">
              <input type="checkbox" v-model="ch.data.enabled" />
              <span>{{ ch.data.enabled ? '已启用' : '未启用' }}</span>
            </label>
          </div>
          <p class="faint" style="margin-top:0">{{ ch.desc }}</p>

          <template v-if="ch.key === 'email'">
            <div class="field"><label>SMTP 服务器</label><input v-model="ch.data.smtpHost" placeholder="smtp.example.com" /></div>
            <div class="row2">
              <div class="field"><label>端口</label><input v-model.number="ch.data.smtpPort" type="number" /></div>
              <div class="field"><label>SSL</label>
                <select v-model="ch.data.useSsl"><option :value="true">SSL/TLS</option><option :value="false">不加密</option></select>
              </div>
            </div>
            <div class="field"><label>账号</label><input v-model="ch.data.username" /></div>
            <div class="field"><label>密码 / 授权码（留空保持不变）</label><input v-model="ch.data.password" type="password" /></div>
            <div class="field"><label>发件人地址</label><input v-model="ch.data.fromAddress" /></div>
            <div class="field"><label>收件人（逗号分隔）</label><input v-model="toAddressesText" placeholder="ops@example.com, boss@example.com" /></div>
          </template>
          <template v-else-if="ch.key === 'webhook'">
            <div class="field">
              <label>接收地址 {{ ch.data.url ? '（已配置，留空保持不变）' : '' }}</label>
              <input v-model="ch.data.url" :placeholder="ch.data.url ? '******' : 'https://itsm.example.com/hooks/printer'" />
            </div>
            <div class="field">
              <label>鉴权令牌 {{ ch.data.secret ? '（已配置，留空保持不变）' : '（可选，经 X-SkyEye-Token 头携带）' }}</label>
              <input v-model="ch.data.secret" :placeholder="ch.data.secret ? '******' : ''" />
            </div>
            <p class="faint" style="margin:0">推送 JSON 含 event / severity / printer / message 等字段，POST 到该地址。</p>
          </template>
          <template v-else>
            <div class="field">
              <label>Webhook 地址 {{ ch.data.webhookUrl ? '（已配置，留空保持不变）' : '' }}</label>
              <input v-model="ch.data.webhookUrl" :placeholder="ch.data.webhookUrl ? '******' : 'https://…'" />
            </div>
            <div class="field" v-if="ch.hasSecret">
              <label>签名密钥 {{ ch.data.secret ? '（已配置，留空保持不变）' : '（未开启签名则留空）' }}</label>
              <input v-model="ch.data.secret" :placeholder="ch.data.secret ? '******' : ''" />
            </div>
          </template>

          <div class="routing" v-if="ch.key !== 'webhook'">
            <div class="field"><label>最低推送级别</label>
              <select v-model="ch.data.minSeverity">
                <option value="Info">全部（Info 及以上）</option>
                <option value="Warning">警告及以上</option>
                <option value="Critical">仅严重</option>
              </select>
            </div>
            <div class="field">
              <label class="inline-chk"><input type="checkbox" v-model="ch.data.quietHoursEnabled" /> 免打扰时段（时段内仅推严重告警）</label>
              <div class="row2" v-if="ch.data.quietHoursEnabled">
                <input v-model="ch.data.quietHoursStart" type="time" />
                <input v-model="ch.data.quietHoursEnd" type="time" />
              </div>
            </div>
          </div>

          <div class="ch-actions">
            <button class="btn ghost sm" :disabled="!ch.data.enabled || testing !== ''" @click="test(ch.key)">
              {{ testing === ch.key ? '发送中…' : '发送测试消息' }}
            </button>
            <div class="spacer"></div>
            <span class="ok-tag" v-if="ch.data.configured">✓ 配置完整</span>
            <button class="btn sm" :disabled="saving === ch.key" @click="save(ch)">{{ saving === ch.key ? '保存中…' : '保存' }}</button>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<script setup>
import { ref, reactive, computed, onMounted } from 'vue'
import { api, errMsg, fmtTime } from '../api'
import { showToast } from '../components/ui'

const s = ref(null)
const savingThresholds = ref(false)
const savingRetention = ref(false)
const savingPolling = ref(false)
const savingAnalysis = ref(false)
const savingPush = ref(false)
const saving = ref('')
const testing = ref('')
const toAddressesText = ref('')

const channels = reactive([])
const channelDefs = [
  { key: 'feishu', title: '飞书机器人', desc: '群内创建自定义机器人获取 Webhook；卡片消息，支持签名校验。', hasSecret: true },
  { key: 'dingtalk', title: '钉钉机器人', desc: '群内创建自定义机器人；Markdown 消息，支持加签。', hasSecret: true },
  { key: 'wecom', title: '企业微信机器人', desc: '群内创建机器人获取 Webhook；Markdown 消息。', hasSecret: false },
  { key: 'email', title: '邮件（SMTP）', desc: '支持企业邮箱 / QQ 邮箱（授权码）/ 阿里云邮箱等。', hasSecret: false },
  { key: 'webhook', title: '通用 Webhook', desc: 'POST JSON 到任意地址，用于对接 ITSM / n8n / 自建系统。', hasSecret: true }
]

const pushChannels = ['feishu', 'dingtalk', 'wecom', 'email', 'webhook']

for (const def of channelDefs) {
  channels.push({
    ...def,
    data: reactive({
      enabled: false, webhookUrl: '', secret: '', configured: false,
      url: '', minSeverity: 'Info', quietHoursEnabled: false,
      quietHoursStart: '22:00', quietHoursEnd: '08:00'
    })
  })
}

function channelData(key, view) {
  const ch = channels.find(c => c.key === key)
  if (key === 'email') {
    Object.assign(ch.data, {
      enabled: view.enabled, smtpHost: view.smtpHost, smtpPort: view.smtpPort,
      useSsl: view.useSsl, username: view.username, password: '',
      fromAddress: view.fromAddress, fromName: view.fromName,
      configured: view.configured
    })
  } else if (key === 'webhook') {
    Object.assign(ch.data, { enabled: view.enabled, url: '', secret: '', configured: view.configured })
  } else {
    Object.assign(ch.data, {
      enabled: view.enabled, webhookUrl: '', secret: '', configured: view.configured
    })
  }
  // 路由策略字段各渠道通用
  if (view && key !== 'webhook') {
    ch.data.minSeverity = view.minSeverity || 'Info'
    ch.data.quietHoursEnabled = !!view.quietHoursEnabled
    ch.data.quietHoursStart = view.quietHoursStart || '22:00'
    ch.data.quietHoursEnd = view.quietHoursEnd || '08:00'
  }
}

async function load() {
  try {
    const { data } = await api.get('/settings')
    s.value = {
      thresholds: data.thresholds,
      retention: data.retention,
      analysis: data.analysis || { forecastWindowDays: 14, supplyReplacementRisePercent: 15 },
      sampling: data.sampling || { supplySampleMinChangePercent: 1 },
      polling: data.polling || { intervalMinutes: 2, concurrency: 20 },
      reportPush: data.reportPush || { enabled: false, dayOfWeek: 1, hour: 8, minute: 0, channels: ['feishu'] }
    }
    if (!Array.isArray(s.value.reportPush.channels)) s.value.reportPush.channels = ['feishu']
    const n = data.notifiers
    for (const def of channelDefs) if (n[def.key]) channelData(def.key, n[def.key])
    toAddressesText.value = (n.email?.toAddresses || []).join(', ')
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

async function saveThresholds() {
  savingThresholds.value = true
  try {
    await api.put('/settings/thresholds', s.value.thresholds)
    showToast('阈值已保存')
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    savingThresholds.value = false
  }
}

async function saveRetention() {
  savingRetention.value = true
  try {
    await api.put('/settings/retention', s.value.retention)
    showToast('保留策略已保存')
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    savingRetention.value = false
  }
}

async function savePolling() {
  savingPolling.value = true
  try {
    await api.put('/settings/polling', s.value.polling)
    await api.put('/settings/sampling', s.value.sampling)
    showToast('已保存，轮询间隔下一轮生效')
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    savingPolling.value = false
  }
}

async function saveAnalysis() {
  savingAnalysis.value = true
  try {
    await api.put('/settings/analysis', s.value.analysis)
    showToast('分析参数已保存')
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    savingAnalysis.value = false
  }
}

function payload(ch) {
  if (ch.key === 'email') {
    return {
      enabled: ch.data.enabled,
      smtpHost: ch.data.smtpHost,
      smtpPort: ch.data.smtpPort,
      useSsl: ch.data.useSsl,
      username: ch.data.username,
      password: ch.data.password || '',
      fromAddress: ch.data.fromAddress,
      fromName: ch.data.fromName || 'SkyEyePMS',
      toAddresses: toAddressesText.value.split(',').map(x => x.trim()).filter(Boolean),
      minSeverity: ch.data.minSeverity,
      quietHoursEnabled: ch.data.quietHoursEnabled,
      quietHoursStart: ch.data.quietHoursStart,
      quietHoursEnd: ch.data.quietHoursEnd
    }
  }
  if (ch.key === 'webhook') {
    return {
      enabled: ch.data.enabled,
      url: ch.data.url || '',
      secret: ch.data.secret || ''
    }
  }
  return {
    enabled: ch.data.enabled,
    webhookUrl: ch.data.webhookUrl || '',
    ...(ch.hasSecret ? { secret: ch.data.secret || '' } : {}),
    minSeverity: ch.data.minSeverity,
    quietHoursEnabled: ch.data.quietHoursEnabled,
    quietHoursStart: ch.data.quietHoursStart,
    quietHoursEnd: ch.data.quietHoursEnd
  }
}

async function savePush() {
  savingPush.value = true
  try {
    await api.put('/settings/reportpush', s.value.reportPush)
    showToast('推送计划已保存')
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    savingPush.value = false
  }
}

async function save(ch) {
  saving.value = ch.key
  try {
    await api.put(`/settings/notifiers/${ch.key}`, payload(ch))
    showToast(`${ch.title} 配置已保存`)
    await load()
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    saving.value = ''
  }
}

async function test(key) {
  testing.value = key
  try {
    await api.post(`/settings/notifiers/${key}/test`)
    showToast('测试消息已发送，请到群里/邮箱查收')
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    testing.value = ''
  }
}

onMounted(load)
</script>

<style scoped>
.page-head { margin-bottom: 16px; }
.grid2 { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
.field { margin-bottom: 10px; }
.field input, .field select { width: 100%; }
.row2 { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.ret-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.ret-grid .full-w { grid-column: 1 / -1; }
.inline-chk { display: flex; align-items: center; gap: 6px; font-size: 13px; color: var(--text-dim); cursor: pointer; }
.inline-chk input { margin: 0; }
.chips { display: flex; gap: 10px; flex-wrap: wrap; }
.routing { border-top: 1px dashed var(--line); padding-top: 10px; margin-top: 10px; }
.ch-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 4px; }
.switch { display: flex; align-items: center; gap: 6px; font-size: 12.5px; color: var(--text-dim); cursor: pointer; }
.switch input { margin: 0; }
.ch-actions { display: flex; align-items: center; gap: 8px; margin-top: 8px; }
.ok-tag { color: var(--ok); font-size: 12px; margin-right: 6px; }
</style>
