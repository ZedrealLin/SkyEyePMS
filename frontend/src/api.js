import axios from 'axios'
import { reactive } from 'vue'
import { router } from './router'

export const auth = reactive({
  token: localStorage.getItem('skyeye_token') || '',
  username: localStorage.getItem('skyeye_username') || '',
  role: localStorage.getItem('skyeye_role') || ''
})

export function setAuth(token, username, role) {
  auth.token = token
  auth.username = username
  auth.role = role
  localStorage.setItem('skyeye_token', token)
  localStorage.setItem('skyeye_username', username)
  localStorage.setItem('skyeye_role', role)
}

export function clearAuth() {
  auth.token = ''
  auth.username = ''
  auth.role = ''
  localStorage.removeItem('skyeye_token')
  localStorage.removeItem('skyeye_username')
  localStorage.removeItem('skyeye_role')
}

export const api = axios.create({ baseURL: '/api', timeout: 60000 })

api.interceptors.request.use(cfg => {
  if (auth.token) cfg.headers.Authorization = `Bearer ${auth.token}`
  return cfg
})

api.interceptors.response.use(
  resp => resp,
  err => {
    if (err.response?.status === 401 && auth.token) {
      clearAuth()
      router.push('/login')
    }
    return Promise.reject(err)
  }
)

export function errMsg(e) {
  return e?.response?.data?.message || e?.response?.data?.error || e?.message || '请求失败'
}

/// 带 JWT 下载文件（CSV 导出等），成功触发浏览器保存
export async function downloadFile(url, filename) {
  const resp = await fetch(url, {
    headers: auth.token ? { Authorization: `Bearer ${auth.token}` } : {}
  })
  if (!resp.ok) throw new Error(`下载失败（${resp.status}）`)
  const blob = await resp.blob()
  const a = document.createElement('a')
  const objUrl = URL.createObjectURL(blob)
  a.href = objUrl
  a.download = filename
  document.body.appendChild(a)
  a.click()
  a.remove()
  URL.revokeObjectURL(objUrl)
}

export function fmtTime(iso) {
  if (!iso) return '—'
  const d = new Date(iso)
  const pad = n => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

export function relTime(iso) {
  if (!iso) return '从未'
  const diff = (Date.now() - new Date(iso).getTime()) / 1000
  if (diff < 60) return `${Math.max(0, Math.floor(diff))} 秒前`
  if (diff < 3600) return `${Math.floor(diff / 60)} 分钟前`
  if (diff < 86400) return `${Math.floor(diff / 3600)} 小时前`
  return `${Math.floor(diff / 86400)} 天前`
}
