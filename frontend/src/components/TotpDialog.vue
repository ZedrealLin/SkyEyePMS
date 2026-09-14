<template>
  <div v-if="open" class="modal-mask" @click.self="close">
    <div class="modal">
      <h2>两步验证（TOTP）</h2>

      <template v-if="!enabled && !secretReady">
        <p class="faint">使用验证器 App（Microsoft Authenticator / Google Authenticator 等）扫描或手动录入密钥，启用后登录需要输入 6 位动态验证码。</p>
        <button class="btn" :disabled="loading" @click="setup">{{ loading ? '生成中…' : '生成密钥' }}</button>
      </template>

      <template v-else-if="!enabled && secretReady">
        <p class="faint">1. 在验证器 App 中手动录入以下密钥（或直接粘贴 otpauth URI 到支持的 App）：</p>
        <div class="secret mono">{{ secret }}</div>
        <div class="uri mono">{{ otpauthUri }}</div>
        <p class="faint" style="margin-top:10px">2. 输入 App 显示的 6 位验证码完成启用：</p>
        <input v-model="code" class="totp-input" inputmode="numeric" maxlength="6" placeholder="000000" />
        <div class="modal-actions">
          <button class="btn ghost" @click="close">取消</button>
          <button class="btn" :disabled="loading || code.length !== 6" @click="enable">{{ loading ? '启用中…' : '确认启用' }}</button>
        </div>
      </template>

      <template v-else>
        <p class="faint">✅ 两步验证已启用。登录时需要输入验证器 App 中的动态验证码。</p>
        <p class="faint">如需停用，请输入当前验证码确认：</p>
        <input v-model="code" class="totp-input" inputmode="numeric" maxlength="6" placeholder="000000" />
        <div class="modal-actions">
          <button class="btn ghost" @click="close">关闭</button>
          <button class="btn danger" :disabled="loading || code.length !== 6" @click="disable">{{ loading ? '停用中…' : '停用两步验证' }}</button>
        </div>
      </template>
    </div>
  </div>
</template>

<script setup>
import { ref } from 'vue'
import { api, errMsg } from '../api'
import { showToast } from './ui'

const open = ref(false)
const loading = ref(false)
const enabled = ref(false)
const secretReady = ref(false)
const secret = ref('')
const otpauthUri = ref('')
const code = ref('')

async function openDialog() {
  open.value = true
  code.value = ''
  secretReady.value = false
  secret.value = ''
  otpauthUri.value = ''
  try {
    const { data } = await api.get('/auth/totp/status')
    enabled.value = !!data.enabled
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

function close() { open.value = false }

async function setup() {
  loading.value = true
  try {
    const { data } = await api.post('/auth/totp/setup')
    secret.value = data.secret
    otpauthUri.value = data.otpauthUri
    secretReady.value = true
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    loading.value = false
  }
}

async function enable() {
  loading.value = true
  try {
    await api.post('/auth/totp/enable', { code: code.value })
    enabled.value = true
    code.value = ''
    showToast('两步验证已启用，下次登录需要输入验证码')
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    loading.value = false
  }
}

async function disable() {
  loading.value = true
  try {
    await api.post('/auth/totp/disable', { code: code.value })
    enabled.value = false
    code.value = ''
    showToast('两步验证已停用')
  } catch (e) {
    showToast(errMsg(e), true)
  } finally {
    loading.value = false
  }
}

defineExpose({ openDialog })
</script>

<style scoped>
.secret {
  background: var(--bg-soft);
  border: 1px solid var(--line);
  border-radius: 8px;
  padding: 8px 12px;
  font-size: 15px;
  letter-spacing: 2px;
  margin: 6px 0;
  user-select: all;
}
.uri {
  word-break: break-all;
  color: var(--text-faint);
  font-size: 11px;
}
.totp-input {
  font-family: var(--mono);
  letter-spacing: 8px;
  text-align: center;
  font-size: 18px;
  width: 100%;
}
</style>
