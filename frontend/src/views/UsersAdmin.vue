<template>
  <div>
    <div class="page-head">
      <h1>用户管理</h1>
      <div class="spacer"></div>
      <button class="btn" @click="dialog = true">添加用户</button>
    </div>

    <div class="card">
      <table>
        <thead><tr><th>用户名</th><th>角色</th><th>创建时间</th><th>最近登录</th><th></th></tr></thead>
        <tbody>
          <tr v-for="u in users" :key="u.id">
            <td>{{ u.username }} <span v-if="u.username === auth.username" class="faint">（当前）</span></td>
            <td>
              <span class="pill" :class="u.role === 'admin' ? 'warn' : 'unknown'">
                {{ u.role === 'admin' ? '管理员' : '只读' }}
              </span>
            </td>
            <td class="muted">{{ fmtTime(u.createdAt) }}</td>
            <td class="muted">{{ fmtTime(u.lastLoginAt) }}</td>
            <td style="text-align:right">
              <button class="btn ghost sm" @click="resetPwd(u)">重置密码</button>
              <button v-if="u.username !== auth.username" class="btn danger sm" @click="del(u)">删除</button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="dialog" class="modal-mask" @click.self="dialog = false">
      <div class="modal">
        <h2>添加用户</h2>
        <div class="field">
          <label>用户名</label>
          <input v-model="form.username" style="width:100%" />
        </div>
        <div class="field">
          <label>初始密码（至少 8 位）</label>
          <input v-model="form.password" type="password" style="width:100%" />
        </div>
        <div class="field">
          <label>角色</label>
          <select v-model="form.role" style="width:100%">
            <option value="viewer">只读（仅查看监控中心）</option>
            <option value="admin">管理员</option>
          </select>
        </div>
        <div class="modal-actions">
          <button class="btn ghost" @click="dialog = false">取消</button>
          <button class="btn" @click="create">创建</button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup>
import { ref, reactive, onMounted } from 'vue'
import { api, auth, fmtTime, errMsg } from '../api'
import { showToast } from '../components/ui'

const users = ref([])
const dialog = ref(false)
const form = reactive({ username: '', password: '', role: 'viewer' })

async function load() {
  try {
    const { data } = await api.get('/users')
    users.value = data
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

async function create() {
  try {
    await api.post('/users', form)
    showToast('已创建')
    dialog.value = false
    form.username = ''
    form.password = ''
    load()
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

async function resetPwd(u) {
  const pwd = prompt(`为「${u.username}」设置新密码（至少 8 位）：`)
  if (!pwd) return
  try {
    await api.put(`/users/${u.id}/password`, { newPassword: pwd })
    showToast('密码已重置')
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

async function del(u) {
  if (!confirm(`确定删除用户「${u.username}」？`)) return
  try {
    await api.delete(`/users/${u.id}`)
    showToast('已删除')
    load()
  } catch (e) {
    showToast(errMsg(e), true)
  }
}

onMounted(load)
</script>

<style scoped>
.page-head { display: flex; align-items: center; gap: 10px; margin-bottom: 16px; }
.field { margin-bottom: 12px; }
</style>
