// 主题皮肤：CSS 变量定义在 styles.css 的 [data-theme=...] 块中
export const themes = [
  { id: 'dark', name: '深海蓝', swatch: ['#0d1220', '#4f8cff'] },
  { id: 'obsidian', name: '曜石黑', swatch: ['#0b0b0e', '#d9a44a'] },
  { id: 'aurora', name: '极光青', swatch: ['#071619', '#2dd4bf'] },
  { id: 'violet', name: '暮光紫', swatch: ['#140f24', '#9b6dff'] },
  { id: 'light', name: '晨雾白', swatch: ['#eef1f7', '#3b82f6'] }
]

const KEY = 'skyeepms_theme'

export function initTheme() {
  let saved = null
  try { saved = localStorage.getItem(KEY) } catch { /* 隐私模式等场景忽略 */ }
  applyTheme(themes.some(t => t.id === saved) ? saved : 'dark', false)
}

export function applyTheme(id, persist = true) {
  document.documentElement.dataset.theme = id
  if (persist) {
    try { localStorage.setItem(KEY, id) } catch { /* ignore */ }
  }
  // 通知图表等组件跟随主题重绘
  window.dispatchEvent(new CustomEvent('themechange', { detail: id }))
}

export function currentTheme() {
  return document.documentElement.dataset.theme || 'dark'
}

/// 读取当前主题的图表配色（供 ECharts 使用）
export function chartColors() {
  const cs = getComputedStyle(document.documentElement)
  const get = n => cs.getPropertyValue(n).trim()
  const ok = get('--ok')
  return {
    text: get('--text-dim'),
    faint: get('--text-faint'),
    line: get('--line'),
    accent: get('--accent'),
    ok,
    warn: get('--warn'),
    error: get('--error'),
    violet: '#9b6dff',
    area: ok + '14' // 面积填充：ok 色 ~8% 透明度
  }
}
