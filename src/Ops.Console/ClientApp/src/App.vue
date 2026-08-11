<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'

type AgentEnvelope<T> = { success: boolean; data: T; errorMessage?: string }
type ComponentView = {
  componentId: string; displayName: string; kind: string; ownership: string;
  runtimeState: string; healthState: string; detail?: string
}
type ProjectView = {
  projectId: string; displayName: string; environment: string; status: string;
  installedVersion?: string; generation?: number; components: ComponentView[]; problems: string[]
}
type SecurityContext = { user: string; role: 'reader' | 'operator' | 'admin'; csrfToken: string }
type AuditEvent = { eventId: string; occurredAt: string; category: string; action: string; outcome: string; detail?: string }

const security = ref<SecurityContext | null>(null)
const projects = ref<ProjectView[]>([])
const audits = ref<AuditEvent[]>([])
const agentMode = ref('unknown')
const observedAt = ref('')
const loading = ref(true)
const error = ref('')
const activeOperation = ref('')
const canOperate = computed(() => security.value?.role === 'operator' || security.value?.role === 'admin')

async function api<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { credentials: 'same-origin', ...init })
  const payload = await response.json()
  if (!response.ok) throw new Error(payload.errorMessage || payload.title || `HTTP ${response.status}`)
  return payload
}

async function refresh() {
  loading.value = true
  error.value = ''
  try {
    security.value = await api<SecurityContext>('/api/security/context')
    const [status, projectEnvelope, auditEnvelope] = await Promise.all([
      api<AgentEnvelope<{ mode: string }>>('/api/status'),
      api<AgentEnvelope<{ observedAt: string; projects: ProjectView[] }>>('/api/projects'),
      api<AgentEnvelope<AuditEvent[]>>('/api/audit'),
    ])
    agentMode.value = status.data.mode
    projects.value = projectEnvelope.data.projects
    observedAt.value = projectEnvelope.data.observedAt
    audits.value = auditEnvelope.data
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    loading.value = false
  }
}

async function operate(project: ProjectView, component: ComponentView, action: 'Start' | 'Stop' | 'Restart') {
  if (!canOperate.value || !security.value || project.generation == null) return
  const key = `${project.projectId}/${component.componentId}/${action}`
  if (!confirm(`确认对 ${component.displayName} 执行 ${action}？\n系统会再次校验归属与 generation。`)) return
  activeOperation.value = key
  error.value = ''
  try {
    const now = Date.now()
    await api('/api/operations', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CompanyOps-CSRF': security.value.csrfToken },
      body: JSON.stringify({
        operationId: `console-${now}`,
        idempotencyKey: `console-${now}-${project.projectId}-${component.componentId}-${action}`,
        projectId: project.projectId,
        environment: project.environment,
        componentId: component.componentId,
        action,
        expectedGeneration: project.generation,
      }),
    })
    await refresh()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    activeOperation.value = ''
  }
}

function tone(value: string) {
  const normalized = value.toLowerCase()
  if (normalized.includes('healthy') || normalized.includes('owned') || normalized.includes('installed') || normalized.includes('succeeded')) return 'good'
  if (normalized.includes('conflict') || normalized.includes('failed') || normalized.includes('unhealthy')) return 'bad'
  return 'warn'
}

onMounted(refresh)
</script>

<template>
  <main>
    <header class="topbar">
      <div>
        <p class="eyebrow">WINDOWS CONTROL PLANE</p>
        <h1>CompanyOps <span>Console</span></h1>
      </div>
      <div class="top-actions">
        <div class="identity"><span>{{ security?.role || '—' }}</span>{{ security?.user || '未认证' }}</div>
        <button class="secondary" :disabled="loading" @click="refresh">{{ loading ? '同步中…' : '刷新状态' }}</button>
      </div>
    </header>

    <section class="summary-grid">
      <article><label>Agent 模式</label><strong :class="tone(agentMode)">{{ agentMode }}</strong><small>默认失败关闭</small></article>
      <article><label>项目环境</label><strong>{{ projects.length }}</strong><small>当前主机声明</small></article>
      <article><label>归属冲突</label><strong :class="projects.some(p => p.status === 'Conflict') ? 'bad' : 'good'">{{ projects.filter(p => p.status === 'Conflict').length }}</strong><small>冲突时禁用操作</small></article>
      <article><label>最近观测</label><strong class="time">{{ observedAt ? new Date(observedAt).toLocaleTimeString() : '—' }}</strong><small>{{ observedAt ? new Date(observedAt).toLocaleDateString() : '' }}</small></article>
    </section>

    <p v-if="error" class="error-banner">{{ error }}</p>
    <p v-if="!canOperate && security" class="notice">当前为只读角色。控制按钮只有 operator/admin 可用，Agent 还会独立执行第二次授权与归属校验。</p>

    <section class="workspace">
      <div class="projects-panel">
        <div class="section-title"><div><p class="eyebrow">MANAGED PROJECTS</p><h2>项目与组件</h2></div><span>{{ projects.reduce((sum, project) => sum + project.components.length, 0) }} components</span></div>
        <div v-if="!loading && projects.length === 0" class="empty">本机尚无符合 hostId 的项目声明。</div>
        <article v-for="project in projects" :key="`${project.projectId}/${project.environment}`" class="project-card">
          <div class="project-head">
            <div><h3>{{ project.displayName }}</h3><p>{{ project.projectId }} · {{ project.environment }} · {{ project.installedVersion || '未安装' }}</p></div>
            <span class="pill" :class="tone(project.status)">{{ project.status }}</span>
          </div>
          <p v-for="problem in project.problems" :key="problem" class="problem">{{ problem }}</p>
          <div class="component" v-for="component in project.components" :key="component.componentId">
            <div class="component-main">
              <i :class="tone(component.ownership)"></i>
              <div><strong>{{ component.displayName }}</strong><small>{{ component.kind }} · {{ component.componentId }}</small></div>
            </div>
            <div class="component-state"><span :class="tone(component.ownership)">{{ component.ownership }}</span><small>{{ component.runtimeState }} / {{ component.healthState }}</small></div>
            <div class="controls">
              <button :disabled="!canOperate || component.ownership !== 'Owned' || !!activeOperation" @click="operate(project, component, 'Start')">启动</button>
              <button :disabled="!canOperate || component.ownership !== 'Owned' || !!activeOperation" @click="operate(project, component, 'Restart')">重启</button>
              <button class="danger" :disabled="!canOperate || component.ownership !== 'Owned' || !!activeOperation" @click="operate(project, component, 'Stop')">停止</button>
            </div>
          </div>
        </article>
      </div>

      <aside>
        <div class="section-title"><div><p class="eyebrow">AUDIT TRAIL</p><h2>最近操作</h2></div></div>
        <ol class="timeline">
          <li v-for="event in audits.slice(0, 12)" :key="event.eventId">
            <i :class="tone(event.outcome)"></i>
            <div><strong>{{ event.action }}</strong><p>{{ event.detail || event.category }}</p><time>{{ new Date(event.occurredAt).toLocaleString() }}</time></div>
          </li>
        </ol>
      </aside>
    </section>
  </main>
</template>
