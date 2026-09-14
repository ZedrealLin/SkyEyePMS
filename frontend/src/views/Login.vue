<template>
  <div class="login-wrap">
    <div class="login-card">
      <div class="brand">
        <div class="logo">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
            <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" />
            <circle cx="12" cy="12" r="3" />
          </svg>
        </div>
        <div>
          <div class="brand-name">SkyEyePMS</div>
          <div class="brand-sub">打印机状态监控系统</div>
        </div>
      </div>
      <form @submit.prevent="submit">
        <template v-if="!totpStep">
          <div class="field">
            <label>用户名</label>
            <input v-model="username" autocomplete="username" placeholder="admin" />
          </div>
          <div class="field">
            <label>密码</label>
            <input v-model="password" type="password" autocomplete="current-password" placeholder="••••••••" />
          </div>
        </template>
        <template v-else>
          <div class="totp-hint">已启用两步验证，请输入验证器 App 中的 6 位验证码。</div>
          <div class="field">
            <label>验证码</label>
            <input v-model="totpCode" inputmode="numeric" maxlength="6" class="totp-input" placeholder="000000" autocomplete="one-time-code" />
          </div>
        </template>
        <div v-if="error" class="error">{{ error }}</div>
        <button class="btn login-btn" :disabled="loading">
          {{ loading ? (totpStep ? '验证中…' : '登录中…') : (totpStep ? '验 证' : '登 录') }}
        </button>
        <button v-if="totpStep" type="button" class="btn ghost login-btn" style="margin-top:8px" @click="backToLogin">返回重新登录</button>
      </form>
    </div>
    <div class="foot">SkyEyePMS · 开源打印机监控 · 支持 SNMP 标准协议</div>
  </div>
</template>

<script setup>
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, setAuth, errMsg } from '../api'

const router = useRouter()
const username = ref('')
const password = ref('')
const error = ref('')
const loading = ref(false)
const totpStep = ref(false)
const loginToken = ref('')
const totpCode = ref('')

async function submit() {
  if (!totpStep.value && (!username.value || !password.value)) { error.value = '请输入用户名和密码'; return }
  if (totpStep.value && (!totpCode.value || totpCode.value.length !== 6)) { error.value = '请输入 6 位验证码'; return }
  loading.value = true
  error.value = ''
  try {
    const { data } = totpStep.value
      ? await api.post('/auth/login/totp', { loginToken: loginToken.value, code: totpCode.value })
      : await api.post('/auth/login', { username: username.value, password: password.value })
    if (data.totpRequired) {
      loginToken.value = data.loginToken
      totpStep.value = true
      totpCode.value = ''
      error.value = ''
      return
    }
    setAuth(data.token, data.username, data.role)
    router.push('/dashboard')
  } catch (e) {
    error.value = errMsg(e)
  } finally {
    loading.value = false
  }
}

function backToLogin() {
  totpStep.value = false
  loginToken.value = ''
  totpCode.value = ''
  error.value = ''
}
</script>

<style scoped>
.login-wrap {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 16px;
  box-sizing: border-box;
  background:
    radial-gradient(900px 480px at 15% 0%, color-mix(in srgb, var(--accent) 14%, transparent), transparent 60%),
    radial-gradient(800px 420px at 100% 100%, color-mix(in srgb, var(--accent-2) 8%, transparent), transparent 60%),
    var(--bg);
  transition: background .25s ease;
}
.login-card {
  width: min(360px, 100%);
  background: var(--card);
  border: 1px solid var(--line);
  border-radius: 18px;
  padding: 30px;
  box-shadow: var(--shadow-pop);
}
.brand { display: flex; gap: 12px; align-items: center; margin-bottom: 24px; }
.logo {
  width: 42px; height: 42px;
  border-radius: 12px;
  background: var(--logo-grad);
  box-shadow: 0 4px 16px color-mix(in srgb, var(--accent) 35%, transparent), inset 0 1px 0 rgba(255, 255, 255, .22);
  display: flex; align-items: center; justify-content: center;
  color: #fff;
}
.logo svg { width: 22px; height: 22px; }
.brand-name { font-size: 18px; font-weight: 700; }
.brand-sub { font-size: 12px; color: var(--text-faint); }
.field { margin-bottom: 14px; }
.field input { width: 100%; padding: 9px 12px; }
.error {
  background: color-mix(in srgb, var(--error) 10%, transparent);
  border: 1px solid color-mix(in srgb, var(--error) 35%, transparent);
  color: var(--error);
  border-radius: 8px;
  padding: 8px 12px;
  font-size: 12.5px;
  margin-bottom: 12px;
}
.totp-hint {
  background: color-mix(in srgb, var(--accent) 10%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent) 30%, transparent);
  color: var(--accent-2);
  border-radius: 8px;
  padding: 8px 12px;
  font-size: 12.5px;
  margin-bottom: 12px;
}
.totp-input { font-family: var(--mono); letter-spacing: 8px; text-align: center; font-size: 18px; }
.login-btn { width: 100%; justify-content: center; padding: 10px; font-size: 14px; margin-top: 4px; background: var(--logo-grad); color: #fff; }
.foot { margin-top: 26px; color: var(--text-faint); font-size: 12px; }
</style>
