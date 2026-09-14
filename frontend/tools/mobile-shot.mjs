// 移动端视口截图：node tools/mobile-shot.mjs <token>
// 用系统 Edge 精确 390px 视口 + 注入登录态，验证响应式布局
import { chromium } from 'playwright-core'
import { writeFileSync } from 'node:fs'

const token = process.argv[2]
const base = 'http://localhost:5090'

const browser = await chromium.launch({ channel: 'msedge', headless: true })
const ctx = await browser.newContext({
  viewport: { width: 390, height: 844 },
  deviceScaleFactor: 2
})
const page = await ctx.newPage()
await page.addInitScript(t => {
  localStorage.setItem('skyeye_token', t)
  localStorage.setItem('skyeye_username', 'admin')
  localStorage.setItem('skyeye_role', 'admin')
}, token)

const shots = [
  ['/dashboard', 'mobile-dashboard.png', 3000],
  ['/reports', 'mobile-reports.png', 3500]
]
for (const [path, file, wait] of shots) {
  await page.goto(base + path, { waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(wait)
  await page.screenshot({ path: '../docs/screenshots/' + file, fullPage: false })
  // 趋势图在页面底部：滚到底再截一张长图（reports 用 fullPage）
  if (path === '/reports') {
    await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight))
    await page.waitForTimeout(1200)
    await page.screenshot({ path: '../docs/screenshots/mobile-reports-bottom.png' })
    await page.evaluate(() => window.scrollTo(0, 0))
    await page.waitForTimeout(400)
    await page.screenshot({ path: '../docs/screenshots/mobile-reports-full.png', fullPage: true })
  }
  console.log('saved', file)
}
await browser.close()
