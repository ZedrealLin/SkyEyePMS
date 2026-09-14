import { createRouter, createWebHistory } from 'vue-router'
import { auth } from './api'

const routes = [
  { path: '/login', component: () => import('./views/Login.vue'), meta: { public: true } },
  {
    path: '/',
    component: () => import('./views/Layout.vue'),
    children: [
      { path: '', redirect: '/dashboard' },
      { path: 'dashboard', component: () => import('./views/Dashboard.vue') },
      { path: 'printers/:id', component: () => import('./views/PrinterDetail.vue') },
      { path: 'alerts', component: () => import('./views/Alerts.vue') },
      { path: 'reports', component: () => import('./views/Reports.vue') },
      { path: 'printers-admin', component: () => import('./views/PrintersAdmin.vue'), meta: { admin: true } },
      { path: 'settings', component: () => import('./views/Settings.vue'), meta: { admin: true } },
      { path: 'users', component: () => import('./views/UsersAdmin.vue'), meta: { admin: true } },
      { path: 'audit', component: () => import('./views/Audit.vue'), meta: { admin: true } }
    ]
  },
  { path: '/:pathMatch(.*)*', redirect: '/dashboard' }
]

export const router = createRouter({ history: createWebHistory(), routes })

router.beforeEach(to => {
  if (!to.meta.public && !auth.token) return '/login'
  if (to.meta.admin && auth.role !== 'admin') return '/dashboard'
  if (to.path === '/login' && auth.token) return '/dashboard'
  return true
})
