<template>
  <div class="layout">
    <aside class="sidebar">
      <div class="brand">
        <div class="logo">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
            <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" />
            <circle cx="12" cy="12" r="3" />
          </svg>
        </div>
        <div class="brand-text">
          <div class="brand-name">SkyEyePMS</div>
          <div class="brand-sub">打印机状态监控</div>
        </div>
      </div>

      <nav>
        <router-link to="/dashboard" class="nav-item">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
            <rect x="3" y="3" width="7" height="9" rx="1.5" /><rect x="14" y="3" width="7" height="5" rx="1.5" />
            <rect x="14" y="12" width="7" height="9" rx="1.5" /><rect x="3" y="16" width="7" height="5" rx="1.5" />
          </svg>
          <span>监控中心</span>
        </router-link>
        <router-link to="/alerts" class="nav-item">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
            <path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9" />
            <path d="M13.7 21a2 2 0 0 1-3.4 0" />
          </svg>
          <span>告警中心</span>
        </router-link>
        <router-link to="/reports" class="nav-item">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
            <line x1="18" y1="20" x2="18" y2="10" /><line x1="12" y1="20" x2="12" y2="4" /><line x1="6" y1="20" x2="6" y2="14" />
          </svg>
          <span>报表统计</span>
        </router-link>

        <template v-if="auth.role === 'admin'">
          <div class="nav-sep">管理</div>
          <router-link to="/printers-admin" class="nav-item">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
              <polyline points="6 9 6 2 18 2 18 9" />
              <path d="M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2" />
              <rect x="6" y="14" width="12" height="8" rx="1" />
            </svg>
            <span>打印机管理</span>
          </router-link>
          <router-link to="/settings" class="nav-item">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
              <line x1="4" y1="21" x2="4" y2="14" /><line x1="4" y1="10" x2="4" y2="3" />
              <line x1="12" y1="21" x2="12" y2="12" /><line x1="12" y1="8" x2="12" y2="3" />
              <line x1="20" y1="21" x2="20" y2="16" /><line x1="20" y1="12" x2="20" y2="3" />
              <line x1="1" y1="14" x2="7" y2="14" /><line x1="9" y1="8" x2="15" y2="8" /><line x1="17" y1="16" x2="23" y2="16" />
            </svg>
            <span>系统设置</span>
          </router-link>
          <router-link to="/users" class="nav-item">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
              <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" /><circle cx="9" cy="7" r="4" />
              <path d="M23 21v-2a4 4 0 0 0-3-3.87" /><path d="M16 3.13a4 4 0 0 1 0 7.75" />
            </svg>
            <span>用户管理</span>
          </router-link>
          <router-link to="/audit" class="nav-item">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
              <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
              <polyline points="14 2 14 8 20 8" />
              <line x1="16" y1="13" x2="8" y2="13" /><line x1="16" y1="17" x2="8" y2="17" />
            </svg>
            <span>审计日志</span>
          </router-link>
        </template>
      </nav>

      <div class="sidebar-foot theme-zone">
        <div v-if="themeOpen" class="theme-pop">
          <div class="theme-title">外观主题</div>
          <button
            v-for="t in themes" :key="t.id"
            class="theme-opt" :class="{ on: current === t.id }"
            @click="pick(t.id)"
          >
            <span class="swatch">
              <i :style="{ background: t.swatch[0] }"></i><i :style="{ background: t.swatch[1] }"></i>
            </span>
            <span class="theme-name">{{ t.name }}</span>
            <svg v-if="current === t.id" class="check" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
              <polyline points="20 6 9 17 4 12" />
            </svg>
          </button>
        </div>
        <div class="who">
          <div class="avatar">{{ auth.username.slice(0, 1).toUpperCase() }}</div>
          <div class="who-text">
            <div class="who-name">{{ auth.username }}</div>
            <div class="who-role">{{ auth.role === 'admin' ? '管理员' : '只读' }}</div>
          </div>
          <div class="who-actions">
            <button class="icon-btn" title="两步验证" @click.stop="totpDialog.openDialog()">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
                <rect x="3" y="11" width="18" height="11" rx="2" /><path d="M7 11V7a5 5 0 0 1 10 0v4" />
              </svg>
            </button>
            <button class="icon-btn" title="切换主题" @click.stop="themeOpen = !themeOpen">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
                <path d="M12 2.69l5.66 5.66a8 8 0 1 1-11.31 0z" />
              </svg>
            </button>
            <button class="icon-btn logout" title="退出登录" @click="logout">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
                <path d="M18.36 6.64a9 9 0 1 1-12.73 0" /><line x1="12" y1="2" x2="12" y2="12" />
              </svg>
            </button>
          </div>
        </div>
      </div>
    </aside>
    <main class="main">
      <AppToast />
      <TotpDialog ref="totpDialog" />
      <router-view />
    </main>
  </div>
</template>

<script setup>
import { ref, onMounted, onUnmounted } from 'vue'
import { auth, clearAuth } from '../api'
import { useRouter } from 'vue-router'
import AppToast from '../components/AppToast.vue'
import TotpDialog from '../components/TotpDialog.vue'
import { themes, applyTheme, currentTheme } from '../theme'

const router = useRouter()
const totpDialog = ref(null)
function logout() {
  clearAuth()
  router.push('/login')
}

const themeOpen = ref(false)
const current = ref(currentTheme())
function pick(id) {
  applyTheme(id)
  current.value = id
  themeOpen.value = false
}
function onDocClick(e) {
  if (themeOpen.value && !e.target.closest('.theme-zone')) themeOpen.value = false
}
onMounted(() => document.addEventListener('click', onDocClick))
onUnmounted(() => document.removeEventListener('click', onDocClick))
</script>

<style scoped>
.layout { display: flex; min-height: 100vh; }
.sidebar {
  width: 224px;
  flex-shrink: 0;
  background: var(--sidebar-grad);
  border-right: 1px solid var(--line);
  display: flex;
  flex-direction: column;
  padding: 20px 12px 16px;
  position: sticky;
  top: 0;
  height: 100vh;
  transition: background .25s ease;
}

/* ---- 品牌区 ---- */
.brand { display: flex; gap: 11px; align-items: center; padding: 0 8px 20px; }
.logo {
  width: 38px; height: 38px;
  border-radius: 11px;
  background: var(--logo-grad);
  box-shadow: 0 4px 14px color-mix(in srgb, var(--accent) 35%, transparent), inset 0 1px 0 rgba(255, 255, 255, .22);
  display: flex; align-items: center; justify-content: center;
  color: #fff;
  flex-shrink: 0;
}
.logo svg { width: 20px; height: 20px; }
.brand-name { font-weight: 700; font-size: 15px; letter-spacing: .3px; line-height: 1.25; }
.brand-sub { font-size: 11px; color: var(--text-faint); }

/* ---- 导航 ---- */
nav { display: flex; flex-direction: column; gap: 3px; flex: 1; }
.nav-item {
  position: relative;
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 9px 12px;
  border-radius: 9px;
  color: var(--text-dim);
  font-size: 13.5px;
  transition: background .15s, color .15s;
}
.nav-item svg { width: 16px; height: 16px; opacity: .8; flex-shrink: 0; transition: opacity .15s; }
.nav-item:hover { color: var(--text); background: color-mix(in srgb, var(--accent) 8%, transparent); text-decoration: none; }
.nav-item:hover svg { opacity: 1; }
.nav-item.router-link-active {
  color: var(--text);
  background: linear-gradient(90deg, color-mix(in srgb, var(--accent) 22%, transparent), color-mix(in srgb, var(--accent) 7%, transparent));
}
.nav-item.router-link-active svg { color: var(--accent-2); opacity: 1; }
.nav-item.router-link-active::before {
  content: '';
  position: absolute;
  left: -12px;
  top: 50%;
  transform: translateY(-50%);
  width: 3px;
  height: 18px;
  border-radius: 0 3px 3px 0;
  background: var(--accent);
  box-shadow: 0 0 8px color-mix(in srgb, var(--accent) 80%, transparent);
}
.nav-sep {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 11px;
  color: var(--text-faint);
  padding: 16px 4px 6px;
  letter-spacing: 3px;
}
.nav-sep::after { content: ''; flex: 1; height: 1px; background: var(--line); }

/* ---- 底部用户区 + 主题切换 ---- */
.sidebar-foot { border-top: 1px solid var(--line); padding-top: 12px; position: relative; }
.who {
  display: flex;
  gap: 10px;
  align-items: center;
  background: var(--card);
  border: 1px solid var(--line);
  border-radius: 11px;
  padding: 9px 10px;
}
.avatar {
  width: 32px; height: 32px; border-radius: 50%;
  background: var(--logo-grad);
  display: flex; align-items: center; justify-content: center;
  font-size: 13px; font-weight: 700; color: #fff;
  flex-shrink: 0;
}
.who-text { flex: 1; min-width: 0; }
.who-name { font-size: 13px; font-weight: 600; line-height: 1.3; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.who-role { font-size: 11px; color: var(--text-faint); line-height: 1.3; }
.who-actions { display: flex; gap: 2px; flex-shrink: 0; }
.icon-btn {
  background: transparent;
  border: none;
  color: var(--text-faint);
  width: 26px; height: 28px;
  border-radius: 7px;
  display: flex; align-items: center; justify-content: center;
  cursor: pointer;
  transition: color .15s, background .15s;
}
.icon-btn svg { width: 15px; height: 15px; }
.icon-btn:hover { color: var(--accent-2); background: color-mix(in srgb, var(--accent) 10%, transparent); }
.icon-btn.logout:hover { color: var(--error); background: color-mix(in srgb, var(--error) 10%, transparent); }

.theme-pop {
  position: absolute;
  bottom: calc(100% + 10px);
  left: 0;
  right: 0;
  background: var(--card-2);
  border: 1px solid var(--line);
  border-radius: 12px;
  padding: 8px;
  box-shadow: var(--shadow-pop);
  z-index: 40;
  animation: popIn .15s ease;
}
@keyframes popIn { from { opacity: 0; transform: translateY(6px); } }
.theme-title {
  font-size: 11px;
  color: var(--text-faint);
  letter-spacing: 2px;
  padding: 4px 8px 8px;
}
.theme-opt {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  background: transparent;
  border: none;
  border-radius: 8px;
  padding: 7px 8px;
  color: var(--text-dim);
  font-size: 13px;
  cursor: pointer;
  transition: background .12s, color .12s;
}
.theme-opt:hover { background: color-mix(in srgb, var(--accent) 9%, transparent); color: var(--text); }
.theme-opt.on { color: var(--text); background: color-mix(in srgb, var(--accent) 14%, transparent); }
.swatch { display: flex; flex-shrink: 0; }
.swatch i {
  width: 14px; height: 14px;
  border-radius: 50%;
  border: 1px solid rgba(255, 255, 255, .18);
  display: inline-block;
}
.swatch i + i { margin-left: -5px; }
.theme-name { flex: 1; text-align: left; }
.check { width: 14px; height: 14px; color: var(--accent-2); }

.main { flex: 1; padding: 22px 26px; max-width: 1500px; min-width: 0; }

/* ---- 移动端：侧边栏收窄为图标栏，内容占满 ---- */
@media (max-width: 860px) {
  .sidebar { width: 64px; padding: 14px 8px 12px; }
  .brand { flex-direction: column; gap: 6px; padding: 0 0 14px; }
  .brand-text { display: none; }
  .nav-item { justify-content: center; padding: 10px 0; }
  .nav-item span { display: none; }
  .nav-sep { justify-content: center; padding: 12px 0 4px; letter-spacing: 0; }
  .nav-sep::after { display: none; }
  .nav-item.router-link-active::before { left: -8px; }
  .who { padding: 8px 6px; justify-content: center; }
  .who-text { display: none; }
  .who-actions { flex-direction: column; gap: 4px; }
  .main { padding: 14px 12px; }
  .theme-pop { left: auto; right: 8px; width: 200px; }
}
</style>
