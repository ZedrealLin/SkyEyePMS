import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 开发期代理到后端；生产可将构建产物放入后端 wwwroot 或用 nginx 反代
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5090',
        changeOrigin: true
      }
    }
  }
})
