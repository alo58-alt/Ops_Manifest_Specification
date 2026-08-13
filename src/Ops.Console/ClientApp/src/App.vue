<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'

type AgentEnvelope<T> = { success: boolean; data: T; errorMessage?: string }
type ComponentView = {
  componentId: string; displayName: string; kind: string; ownership: string;
  runtimeState: string; healthState: string; detail?: string
}
type ProjectView = {
  projectId: string; displayName: string; environment: string; status: string;
  installedVersion?: string; generation?: number; installRoot?: string; gitUpdateEnabled: boolean; hasInstalledState: boolean;
  components: ComponentView[]; problems: string[]
}
type SecurityContext = { user: string; role: 'reader' | 'operator' | 'admin'; csrfToken: string }
type GitUpdateAuditData = {
  operationId: string; projectId: string; environment: string; action: 'Check' | 'Apply';
  fromCommit?: string; toCommit?: string; changedFiles: string[]; steps: string[];
  durationMilliseconds: number; rolledBack: boolean; errorCode?: string
}
type AuditEvent = {
  eventId: string; occurredAt: string; category: string; action: string; outcome: string;
  detail?: string; data?: GitUpdateAuditData | Record<string, unknown>
}
type DeploymentAction = 'Plan' | 'Install' | 'Update' | 'Rollback'
type DeploymentResult = {
  operationId: string; action: DeploymentAction; outcome: string; projectId: string; environment: string;
  fromVersion?: string; toVersion?: string; steps: string[]; errorCode?: string; detail?: string
}
type OnboardingComponent = {
  componentId: string; displayName: string; kind: string; nativeName?: string;
  requiresInput: boolean; candidates: string[]
}
type OnboardingPort = {
  portId: string; componentId: string; protocol: string; address: string;
  port?: number; requiresInput: boolean
}
type OnboardingHealth = { componentId: string; success: boolean; detail?: string }
type OnboardingResult = {
  action: 'Plan' | 'Apply'; outcome: string; projectId?: string; displayName?: string;
  environment: string; hostId: string; canApply: boolean; alreadyOnboarded: boolean;
  planToken?: string; components: OnboardingComponent[]; ports: OnboardingPort[];
  health: OnboardingHealth[]; steps: string[]; problems: string[]; errorCode?: string; detail?: string
}
type DirectoryBrowseEntry = { name: string; fullPath: string }
type DirectoryBrowseResult = {
  currentPath?: string; parentPath?: string; isProjectRoot: boolean; directories: DirectoryBrowseEntry[]
}
type GitUpdateResult = {
  operationId: string; action: 'Check' | 'Apply'; outcome: string; projectId: string; environment: string;
  updateAvailable: boolean; canApply: boolean; currentCommit?: string; remoteCommit?: string;
  changedFiles: string[]; steps: string[]; errorCode?: string; detail?: string
}
type GitCredentialSetResult = {
  operationId: string; outcome: string; projectId: string; environment: string;
  remoteHost?: string; configured: boolean; errorCode?: string; detail?: string
}

const security = ref<SecurityContext | null>(null)
const projects = ref<ProjectView[]>([])
const audits = ref<AuditEvent[]>([])
const agentMode = ref('unknown')
const observedAt = ref('')
const loading = ref(true)
const error = ref('')
const activeOperation = ref('')
const selectedProjectKey = ref('')
const artifactDirectory = ref('')
const deploymentIdentity = ref(createDeploymentIdentity())
const deploymentResult = ref<DeploymentResult | null>(null)
const onboardingProjectRoot = ref('')
const onboardingEnvironment = ref('production')
const onboardingResult = ref<OnboardingResult | null>(null)
const onboardingNativeNames = ref<Record<string, string>>({})
const onboardingPorts = ref<Record<string, number>>({})
const directoryBrowserOpen = ref(false)
const directoryBrowserLoading = ref(false)
const directoryBrowserResult = ref<DirectoryBrowseResult | null>(null)
const directoryBrowserError = ref('')
const gitUpdatesEnabled = ref(false)
const interactiveSessionOperationsEnabled = ref(false)
const gitUpdateResults = ref<Record<string, GitUpdateResult>>({})
const credentialProject = ref<ProjectView | null>(null)
const credentialUsername = ref('')
const credentialSecret = ref('')
const credentialMessage = ref('')
const credentialMessageTone = ref<'good' | 'bad' | ''>('')
const expandedAudits = ref<Record<string, boolean>>({})
const canOperate = computed(() => security.value?.role === 'operator' || security.value?.role === 'admin')
const serviceControlEnabled = computed(() => agentMode.value === 'service-control-enabled' || agentMode.value === 'mutations-enabled')
const selectedProject = computed(() => projects.value.find(project => projectKey(project) === selectedProjectKey.value) ?? null)
const automaticDeploymentAction = computed<DeploymentAction>(() =>
  selectedProject.value?.hasInstalledState ? 'Update' : 'Install')
const releaseManifestPath = computed(() => {
  const directory = artifactDirectory.value.trim().replace(/[\\/]+$/, '')
  return directory ? `${directory}\\release-manifest.json` : ''
})

function canSubmitDeployment(action: DeploymentAction) {
  const project = selectedProject.value
  if (!canOperate.value || !project || project.status === 'Conflict' || !!activeOperation.value) return false
  if (action !== 'Rollback' && !artifactDirectory.value.trim()) return false
  if (action === 'Install' && project.hasInstalledState) return false
  if ((action === 'Update' || action === 'Rollback') && !project.hasInstalledState) return false
  if ((action === 'Update' || action === 'Rollback' || action === 'Plan' && project.hasInstalledState) &&
      (project.generation == null || project.status !== 'Installed' || project.components.some(component => component.ownership !== 'Owned'))) return false
  return action === 'Plan' || agentMode.value === 'mutations-enabled'
}

async function api<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { credentials: 'same-origin', ...init })
  const responseText = await response.text()
  let payload: Record<string, unknown> | null = null
  if (responseText.trim()) {
    try {
      payload = JSON.parse(responseText) as Record<string, unknown>
    } catch {
      const preview = responseText.replace(/\s+/g, ' ').trim().slice(0, 240)
      throw new Error(`CompanyOps 返回了无法解析的响应（HTTP ${response.status}）：${preview || '空响应'}`)
    }
  }
  if (!response.ok) {
    const message = payload?.errorMessage || payload?.title || payload?.detail
    throw new Error(typeof message === 'string'
      ? message
      : `CompanyOps 请求失败（HTTP ${response.status}${responseText.trim() ? '' : '，服务器未返回错误详情'}）`)
  }
  if (!payload) throw new Error(`CompanyOps 返回空响应（HTTP ${response.status}）`)
  return payload as T
}

async function refresh() {
  loading.value = true
  error.value = ''
  try {
    security.value = await api<SecurityContext>('/api/security/context')
    const [status, projectEnvelope, auditEnvelope] = await Promise.all([
      api<AgentEnvelope<{ mode: string; gitUpdatesEnabled: boolean; interactiveSessionOperationsEnabled: boolean }>>('/api/status'),
      api<AgentEnvelope<{ observedAt: string; projects: ProjectView[] }>>('/api/projects'),
      api<AgentEnvelope<AuditEvent[]>>('/api/audit'),
    ])
    agentMode.value = status.data.mode
    gitUpdatesEnabled.value = status.data.gitUpdatesEnabled
    interactiveSessionOperationsEnabled.value = status.data.interactiveSessionOperationsEnabled
    projects.value = projectEnvelope.data.projects
    if (!projects.value.some(project => projectKey(project) === selectedProjectKey.value)) {
      selectedProjectKey.value = projects.value.length ? projectKey(projects.value[0]) : ''
    }
    observedAt.value = projectEnvelope.data.observedAt
    audits.value = auditEnvelope.data
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    loading.value = false
  }
}

function projectKey(project: ProjectView) {
  return `${project.projectId}/${project.environment}`
}

function componentControlEnabled(component: ComponentView) {
  return serviceControlEnabled.value &&
    (component.kind !== 'interactiveApp' || interactiveSessionOperationsEnabled.value)
}

function createDeploymentIdentity() {
  return `console-${Date.now()}-${crypto.randomUUID()}`
}

function resetDeploymentAttempt() {
  deploymentIdentity.value = createDeploymentIdentity()
  deploymentResult.value = null
  error.value = ''
}

async function deploy(action: DeploymentAction) {
  const project = selectedProject.value
  if (!canSubmitDeployment(action) || !project || !security.value) return
  if (action !== 'Plan' &&
      !confirm(action === 'Rollback'
        ? `确认把 ${project.displayName} 回滚到上一版本？\n系统会重新校验归属、重启组件并做健康检查。`
        : `确认安全更新 ${project.displayName}？\n系统会自动校验发布包、停止精确组件、切换版本并做健康检查；失败时自动恢复。`)) return

  activeOperation.value = `deploy/${projectKey(project)}/${action}`
  error.value = ''
  deploymentResult.value = null
  try {
    const request: Record<string, unknown> = {
      operationId: deploymentIdentity.value,
      idempotencyKey: deploymentIdentity.value,
      projectId: project.projectId,
      environment: project.environment,
      action,
      expectedGeneration: project.generation ?? 0,
    }
    if (action !== 'Rollback') {
      request.releaseManifestPath = releaseManifestPath.value
      request.artifactDirectory = artifactDirectory.value.trim()
    }

    const envelope = await api<AgentEnvelope<DeploymentResult>>('/api/deployments', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CompanyOps-CSRF': security.value.csrfToken },
      body: JSON.stringify(request),
    })
    deploymentResult.value = envelope.data
    deploymentIdentity.value = createDeploymentIdentity()
    if (envelope.data.outcome !== 'Succeeded') {
      error.value = `${envelope.data.errorCode || 'deployment_rejected'}：${envelope.data.detail || '部署请求被拒绝'}`
      return
    }

    await refresh()
  } catch (cause) {
    error.value = `${cause instanceof Error ? cause.message : String(cause)}。当前幂等键已保留；确认 audit 后再使用同一键重试。`
  } finally {
    activeOperation.value = ''
  }
}

function prepareControlledRelease(project: ProjectView) {
  selectedProjectKey.value = projectKey(project)
  resetDeploymentAttempt()
  requestAnimationFrame(() => {
    document.getElementById('controlled-release')?.scrollIntoView({ behavior: 'smooth', block: 'start' })
    document.getElementById('release-directory')?.focus({ preventScroll: true })
  })
}

function onboardingRequest(action: 'Plan' | 'Apply') {
  const nativeNames = Object.fromEntries(
    Object.entries(onboardingNativeNames.value).filter(([, value]) => value.trim()))
  const ports = Object.fromEntries(
    Object.entries(onboardingPorts.value).filter(([, value]) => Number.isInteger(value) && value > 0 && value <= 65535))
  return {
    projectRoot: onboardingProjectRoot.value.trim(),
    environment: onboardingEnvironment.value.trim() || 'production',
    action,
    expectedPlanToken: action === 'Apply' ? onboardingResult.value?.planToken : undefined,
    nativeNames,
    ports,
  }
}

async function planOnboarding() {
  if (!security.value) {
    error.value = '尚未取得登录与安全上下文，请先点击“刷新状态”。'
    return
  }
  if (!canOperate.value) {
    error.value = '当前账号没有项目接入权限，需要 operator 或 admin。'
    return
  }
  if (!onboardingProjectRoot.value.trim()) {
    error.value = '请先点击“选择目录…”，选择包含 ops\\project-manifest.json 的服务器项目目录。'
    return
  }
  if (activeOperation.value) {
    error.value = activeOperation.value === 'onboarding/plan'
      ? '项目检查仍在执行，请等待当前检查完成。'
      : `当前还有操作正在执行：${activeOperation.value}`
    return
  }
  activeOperation.value = 'onboarding/plan'
  error.value = ''
  onboardingResult.value = null
  try {
    const envelope = await api<AgentEnvelope<OnboardingResult>>('/api/onboarding/existing-project', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CompanyOps-CSRF': security.value.csrfToken },
      body: JSON.stringify(onboardingRequest('Plan')),
    })
    onboardingResult.value = envelope.data
    for (const component of envelope.data.components) {
      if (component.nativeName && !onboardingNativeNames.value[component.componentId]) {
        onboardingNativeNames.value[component.componentId] = component.nativeName
      }
    }
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    activeOperation.value = ''
  }
}

async function browseDirectories(path?: string) {
  if (!security.value || !canOperate.value) {
    error.value = '当前账号没有服务器目录浏览权限，需要 operator 或 admin。'
    return
  }
  directoryBrowserOpen.value = true
  directoryBrowserLoading.value = true
  directoryBrowserError.value = ''
  try {
    const envelope = await api<AgentEnvelope<DirectoryBrowseResult>>('/api/directories/browse', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CompanyOps-CSRF': security.value.csrfToken },
      body: JSON.stringify({ path: path || null }),
    })
    directoryBrowserResult.value = envelope.data
  } catch (cause) {
    directoryBrowserError.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    directoryBrowserLoading.value = false
  }
}

function chooseProjectDirectory() {
  const result = directoryBrowserResult.value
  if (!result?.currentPath || !result.isProjectRoot) return
  onboardingProjectRoot.value = result.currentPath
  onboardingNativeNames.value = {}
  onboardingPorts.value = {}
  resetOnboardingPlan()
  directoryBrowserOpen.value = false
}

async function applyOnboarding() {
  if (!canOperate.value || !security.value || !onboardingResult.value?.canApply ||
      !onboardingResult.value.planToken || activeOperation.value) return
  if (!confirm(`确认把 ${onboardingResult.value.displayName || onboardingResult.value.projectId} 接入当前主机？\n只导入声明，不会启停或修改业务服务。`)) return
  activeOperation.value = 'onboarding/apply'
  error.value = ''
  try {
    const envelope = await api<AgentEnvelope<OnboardingResult>>('/api/onboarding/existing-project', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CompanyOps-CSRF': security.value.csrfToken },
      body: JSON.stringify(onboardingRequest('Apply')),
    })
    onboardingResult.value = envelope.data
    if (envelope.data.outcome === 'Succeeded') await refresh()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    activeOperation.value = ''
  }
}

function resetOnboardingPlan() {
  onboardingResult.value = null
}

function invalidateOnboardingPlan() {
  if (!onboardingResult.value) return
  onboardingResult.value = {
    ...onboardingResult.value,
    canApply: false,
    planToken: undefined,
    detail: '接入参数已修改，请重新点击“检查项目”。',
  }
}

async function operate(project: ProjectView, component: ComponentView, action: 'Start' | 'Stop' | 'Restart') {
  if (!canOperate.value || !serviceControlEnabled.value || !security.value || project.generation == null) return
  const key = `${project.projectId}/${component.componentId}/${action}`
  if (!confirm(`确认对 ${component.displayName} 执行 ${action}？\n系统会再次校验归属与 generation。`)) return
  activeOperation.value = key
  error.value = ''
  try {
    const now = Date.now()
    const envelope = await api<AgentEnvelope<{ outcome: string; errorCode?: string; detail?: string }>>('/api/operations', {
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
    if (envelope.data.outcome !== 'Succeeded') {
      error.value = `${envelope.data.errorCode || 'operation_rejected'}：${envelope.data.detail || '操作未成功'}`
    }
    await refresh()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    activeOperation.value = ''
  }
}

async function gitUpdate(project: ProjectView, action: 'Check' | 'Apply') {
  if (!canOperate.value || !gitUpdatesEnabled.value || !project.gitUpdateEnabled ||
      project.generation == null || !security.value || activeOperation.value) return
  const previous = gitUpdateResults.value[projectKey(project)]
  if (action === 'Apply') {
    if (!previous?.canApply || !previous.currentCommit || !previous.remoteCommit) return
    if (!confirm(`确认把 ${project.displayName} 从 ${previous.currentCommit.slice(0, 12)} 更新到 ${previous.remoteCommit.slice(0, 12)}？\nCompanyOps 会停止精确 Windows 服务、快进代码、启动并做健康检查；失败时恢复原提交。`)) return
  }

  const now = Date.now()
  activeOperation.value = `git/${projectKey(project)}/${action}`
  error.value = ''
  try {
    const envelope = await api<AgentEnvelope<GitUpdateResult>>('/api/git-updates', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CompanyOps-CSRF': security.value.csrfToken },
      body: JSON.stringify({
        operationId: `console-git-${now}`,
        idempotencyKey: `console-git-${now}-${project.projectId}-${project.environment}-${action}`,
        projectId: project.projectId,
        environment: project.environment,
        action,
        expectedGeneration: project.generation,
        expectedCurrentCommit: action === 'Apply' ? previous?.currentCommit : undefined,
        expectedRemoteCommit: action === 'Apply' ? previous?.remoteCommit : undefined,
      }),
    })
    gitUpdateResults.value = { ...gitUpdateResults.value, [projectKey(project)]: envelope.data }
    if (envelope.data.outcome !== 'Succeeded') {
      error.value = `${envelope.data.errorCode || 'git_update_rejected'}：${envelope.data.detail || 'Git 更新操作未成功'}`
      if (envelope.data.errorCode === 'git_credentials_required' ||
          envelope.data.errorCode === 'git_credential_rejected') {
        openCredentialDialog(project)
        credentialMessage.value = envelope.data.detail || '请配置有效的仓库凭据。'
        credentialMessageTone.value = 'bad'
      }
    }
    await refresh()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    activeOperation.value = ''
  }
}

function openCredentialDialog(project: ProjectView) {
  credentialProject.value = project
  credentialUsername.value = ''
  credentialSecret.value = ''
  credentialMessage.value = ''
  credentialMessageTone.value = ''
}

function closeCredentialDialog() {
  credentialProject.value = null
  credentialUsername.value = ''
  credentialSecret.value = ''
  credentialMessage.value = ''
  credentialMessageTone.value = ''
}

async function saveGitCredential() {
  const project = credentialProject.value
  if (!project || project.generation == null || !security.value || activeOperation.value ||
      !credentialUsername.value.trim() || !credentialSecret.value) return

  const now = Date.now()
  activeOperation.value = `git-credential/${projectKey(project)}`
  error.value = ''
  credentialMessage.value = ''
  credentialMessageTone.value = ''
  try {
    const envelope = await api<AgentEnvelope<GitCredentialSetResult>>('/api/git-credentials', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CompanyOps-CSRF': security.value.csrfToken },
      body: JSON.stringify({
        operationId: `console-git-credential-${now}`,
        idempotencyKey: `console-git-credential-${now}-${crypto.randomUUID()}`,
        projectId: project.projectId,
        environment: project.environment,
        expectedGeneration: project.generation,
        username: credentialUsername.value.trim(),
        secret: credentialSecret.value,
      }),
    })
    credentialSecret.value = ''
    if (envelope.data.outcome !== 'Succeeded') {
      credentialMessage.value = `${envelope.data.errorCode || 'credential_rejected'}：${envelope.data.detail || '凭据保存失败'}`
      credentialMessageTone.value = 'bad'
      return
    }
    credentialMessage.value = envelope.data.detail || '凭据已安全保存。'
    credentialMessageTone.value = 'good'
    gitUpdateResults.value = Object.fromEntries(
      Object.entries(gitUpdateResults.value).filter(([key]) => key !== projectKey(project)))
  } catch (cause) {
    credentialSecret.value = ''
    credentialMessage.value = cause instanceof Error ? cause.message : String(cause)
    credentialMessageTone.value = 'bad'
  } finally {
    activeOperation.value = ''
  }
}

function tone(value: string) {
  const normalized = value.toLowerCase()
  if (normalized.includes('unhealthy') || normalized.includes('conflict') || normalized.includes('failed')) return 'bad'
  if (normalized.includes('healthy') || normalized.includes('owned') || normalized.includes('installed') || normalized.includes('succeeded') || normalized.includes('enabled') || normalized.includes('running')) return 'good'
  return 'warn'
}

function auditActionLabel(event: AuditEvent) {
  if (event.category === 'git-update') {
    return event.action === 'Apply' ? 'Git 安全更新' : event.action === 'Check' ? 'Git 更新检查' : event.action
  }
  if (event.category === 'git-credential') return '配置仓库凭据'
  return event.action
}

function gitAuditData(event: AuditEvent): GitUpdateAuditData | null {
  if (event.category !== 'git-update' || !event.data ||
      !Array.isArray((event.data as GitUpdateAuditData).steps)) return null
  return event.data as GitUpdateAuditData
}

function toggleAudit(eventId: string) {
  expandedAudits.value = { ...expandedAudits.value, [eventId]: !expandedAudits.value[eventId] }
}

function formatDuration(milliseconds: number) {
  if (milliseconds < 1000) return `${milliseconds} ms`
  return `${(milliseconds / 1000).toFixed(milliseconds < 10000 ? 1 : 0)} 秒`
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
      <article><label>Agent 模式</label><strong :class="tone(agentMode)">{{ agentMode }}</strong><small>{{ serviceControlEnabled ? '仅允许精确归属服务控制' : '默认失败关闭' }}</small></article>
      <article><label>项目环境</label><strong>{{ projects.length }}</strong><small>当前主机声明</small></article>
      <article><label>归属冲突</label><strong :class="projects.some(p => p.status === 'Conflict') ? 'bad' : 'good'">{{ projects.filter(p => p.status === 'Conflict').length }}</strong><small>冲突时禁用操作</small></article>
      <article><label>最近观测</label><strong class="time">{{ observedAt ? new Date(observedAt).toLocaleTimeString() : '—' }}</strong><small>{{ observedAt ? new Date(observedAt).toLocaleDateString() : '' }}</small></article>
    </section>

    <p v-if="error" class="error-banner">{{ error }}</p>
    <p v-if="!canOperate && security" class="notice">当前为只读角色。控制按钮只有 operator/admin 可用，Agent 还会独立执行第二次授权与归属校验。</p>

    <section class="onboarding-panel">
      <div class="section-title">
        <div><p class="eyebrow">EXISTING PROJECT</p><h2>接入现有项目</h2></div>
        <span>只读预检 · 唯一资源匹配 · 不控制业务服务</span>
      </div>
      <form class="onboarding-form" @submit.prevent="planOnboarding">
        <label class="onboarding-path">服务器上的项目目录
          <span class="directory-field">
            <input v-model="onboardingProjectRoot" readonly placeholder="点击右侧按钮选择服务器项目目录">
            <button type="button" class="secondary" @click="browseDirectories(onboardingProjectRoot || undefined)">选择目录…</button>
          </span>
        </label>
        <label>环境标识
          <input v-model="onboardingEnvironment" autocomplete="off" placeholder="production" @input="resetOnboardingPlan">
        </label>
        <button type="submit" :disabled="activeOperation === 'onboarding/plan'">
          {{ activeOperation === 'onboarding/plan' ? '检查中…' : '检查项目' }}
        </button>
      </form>
      <p class="onboarding-hint"><code>production</code> 表示服务器上的正式运行实例；同一项目另有测试实例时可用 <code>test</code> 或 <code>staging</code>。请选择包含 <code>ops\project-manifest.json</code> 的服务器项目目录。检查不会复制文件、不会重启服务。</p>

      <div v-if="directoryBrowserOpen" class="directory-modal" @click.self="directoryBrowserOpen = false">
        <section class="directory-dialog" role="dialog" aria-modal="true" aria-label="选择服务器项目目录">
          <header><div><strong>选择服务器项目目录</strong><small>{{ directoryBrowserResult?.currentPath || '选择服务器磁盘' }}</small></div><button type="button" class="secondary" @click="directoryBrowserOpen = false">关闭</button></header>
          <p v-if="directoryBrowserError" class="directory-error">{{ directoryBrowserError }}</p>
          <p v-if="directoryBrowserLoading" class="directory-loading">正在读取服务器目录…</p>
          <div v-else class="directory-list">
            <button v-if="directoryBrowserResult?.currentPath" type="button" class="directory-entry parent" @click="browseDirectories(directoryBrowserResult.parentPath || undefined)">← {{ directoryBrowserResult.parentPath || '返回磁盘列表' }}</button>
            <button v-for="directory in directoryBrowserResult?.directories || []" :key="directory.fullPath" type="button" class="directory-entry" @click="browseDirectories(directory.fullPath)"><span>📁</span>{{ directory.name }}</button>
            <p v-if="directoryBrowserResult && !directoryBrowserResult.directories.length" class="directory-empty">此目录没有可浏览的子目录。</p>
          </div>
          <footer><span :class="directoryBrowserResult?.isProjectRoot ? 'good' : 'warn'">{{ directoryBrowserResult?.isProjectRoot ? '已检测到 ops\\project-manifest.json' : '当前目录不是可接入项目' }}</span><button type="button" :disabled="!directoryBrowserResult?.isProjectRoot" @click="chooseProjectDirectory">选择此项目</button></footer>
        </section>
      </div>

      <div v-if="onboardingResult" class="onboarding-result" :class="tone(onboardingResult.outcome)">
        <div class="onboarding-summary">
          <div><strong>{{ onboardingResult.displayName || onboardingResult.projectId || '未识别项目' }}</strong><span>{{ onboardingResult.projectId || '—' }} · {{ onboardingResult.environment }} · {{ onboardingResult.hostId }}</span></div>
          <span class="pill" :class="tone(onboardingResult.outcome)">{{ onboardingResult.outcome }}</span>
        </div>
        <p v-if="onboardingResult.detail">{{ onboardingResult.detail }}</p>
        <div v-for="component in onboardingResult.components" :key="component.componentId" class="onboarding-binding">
          <div><strong>{{ component.displayName }}</strong><small>{{ component.kind }} · {{ component.componentId }}</small></div>
          <label>主机原生名称
            <input v-model="onboardingNativeNames[component.componentId]" :list="`native-${component.componentId}`" :placeholder="component.nativeName || '请输入精确名称'" @input="invalidateOnboardingPlan">
            <datalist :id="`native-${component.componentId}`"><option v-for="candidate in component.candidates" :key="candidate" :value="candidate" /></datalist>
          </label>
        </div>
        <div v-for="port in onboardingResult.ports" :key="port.portId" class="onboarding-port">
          <span>{{ port.portId }} · {{ port.protocol }} · {{ port.address }} · 当前/声明端口 <strong>{{ port.port || '未声明' }}</strong></span>
          <label>指定新端口（可选）<input v-model.number="onboardingPorts[port.portId]" type="number" min="1" max="65535" :placeholder="port.port ? `留空沿用 ${port.port}` : '请输入端口'" @input="invalidateOnboardingPlan"></label>
        </div>
        <ul v-if="onboardingResult.problems.length" class="onboarding-problems"><li v-for="problem in onboardingResult.problems" :key="problem">{{ problem }}</li></ul>
        <ul v-if="onboardingResult.health.length" class="onboarding-health"><li v-for="item in onboardingResult.health" :key="item.componentId" :class="item.success ? 'good' : 'bad'">{{ item.componentId }}：{{ item.detail }}</li></ul>
        <ol v-if="onboardingResult.steps.length"><li v-for="step in onboardingResult.steps" :key="step">{{ step }}</li></ol>
        <button v-if="onboardingResult.action === 'Plan'" class="onboarding-apply" :disabled="!onboardingResult.canApply || !!activeOperation" @click="applyOnboarding">
          {{ activeOperation === 'onboarding/apply' ? '接入中…' : '确认只读接入' }}
        </button>
        <strong v-else-if="onboardingResult.outcome === 'Succeeded'" class="onboarding-done">接入完成，可以在下方查看项目。</strong>
      </div>
    </section>

    <section id="controlled-release" class="deployment-panel">
      <div class="section-title">
        <div><p class="eyebrow">SAFE UPDATE</p><h2>项目更新</h2></div>
        <span>自动校验 · 自动启停 · 失败自动恢复</span>
      </div>
      <form class="deployment-form" @submit.prevent="deploy('Plan')">
        <label>项目环境
          <select v-model="selectedProjectKey" @change="resetDeploymentAttempt">
            <option v-for="project in projects" :key="projectKey(project)" :value="projectKey(project)">
              {{ project.displayName }} · {{ project.environment }} · gen {{ project.generation ?? 0 }}
            </option>
          </select>
        </label>
        <label>本次操作
          <input :value="selectedProject?.hasInstalledState ? '更新现有版本' : '首次纳入版本管理'" readonly>
        </label>
        <label class="wide">发布包目录
          <input id="release-directory" v-model="artifactDirectory" autocomplete="off" placeholder="例如 D:\CompanyOps-Releases\webquizbot\webquizbot-3.0.1-20260813.1" @input="resetDeploymentAttempt">
          <small>目录内应包含 release-manifest.json 和发布 ZIP；完整性由系统自动校验，不需要手工计算哈希。</small>
        </label>
        <div class="deployment-guard wide">
          <div><small>当前状态</small><strong :class="tone(selectedProject?.status || 'unknown')">{{ selectedProject?.status || '未选择' }}</strong></div>
          <div><small>Agent</small><strong :class="tone(agentMode)">{{ agentMode }}</strong></div>
          <button type="submit" :disabled="!canSubmitDeployment('Plan')">
            {{ activeOperation.endsWith('/Plan') ? '检查中…' : '检查更新' }}
          </button>
          <button type="button" :disabled="!canSubmitDeployment(automaticDeploymentAction)" @click="deploy(automaticDeploymentAction)">
            {{ activeOperation.endsWith(`/${automaticDeploymentAction}`) ? '更新中…' : '安全更新' }}
          </button>
          <button type="button" class="secondary" :disabled="!canSubmitDeployment('Rollback')" @click="deploy('Rollback')">
            {{ activeOperation.endsWith('/Rollback') ? '回滚中…' : '回滚上一版本' }}
          </button>
        </div>
      </form>
      <p v-if="agentMode !== 'mutations-enabled'" class="deployment-hint">当前只允许“检查更新”。执行安全更新需要主机管理员在 CompanyOps 安装配置中启用受控版本变更；这是一项主机级一次性授权。</p>
      <p v-else class="deployment-hint">“检查更新”不会修改服务；“安全更新”会自动判断首次安装或普通更新，并在真正切换前再次完成全部校验。</p>
      <div v-if="deploymentResult" class="deployment-result" :class="tone(deploymentResult.outcome)">
        <strong>{{ deploymentResult.action }} · {{ deploymentResult.outcome }} · {{ deploymentResult.operationId }}</strong>
        <span>{{ deploymentResult.fromVersion || '未安装' }} → {{ deploymentResult.toVersion || '—' }}</span>
        <p v-if="deploymentResult.detail">{{ deploymentResult.detail }}</p>
        <ol><li v-for="step in deploymentResult.steps" :key="step">{{ step }}</li></ol>
      </div>
    </section>

    <section class="workspace">
      <div class="projects-panel">
        <div class="section-title"><div><p class="eyebrow">MANAGED PROJECTS</p><h2>项目与组件</h2></div><span>{{ projects.reduce((sum, project) => sum + project.components.length, 0) }} components</span></div>
        <div v-if="!loading && projects.length === 0" class="empty">本机尚无符合 hostId 的项目声明。</div>
        <article v-for="project in projects" :key="`${project.projectId}/${project.environment}`" class="project-card">
          <div class="project-head">
            <div><h3>{{ project.displayName }}</h3><p>{{ project.projectId }} · {{ project.environment }} · {{ project.installedVersion || '现有服务' }}</p></div>
            <span class="pill" :class="tone(project.status)">{{ project.status }}</span>
          </div>
          <p v-for="problem in project.problems" :key="problem" class="problem">{{ problem }}</p>
          <div v-if="project.gitUpdateEnabled" class="git-update">
            <div>
              <strong>L3 · Git 受控更新</strong>
              <small v-if="gitUpdateResults[projectKey(project)]">
                {{ gitUpdateResults[projectKey(project)].detail }}
                <template v-if="gitUpdateResults[projectKey(project)].currentCommit"> · {{ gitUpdateResults[projectKey(project)].currentCommit?.slice(0, 12) }}</template>
                <template v-if="gitUpdateResults[projectKey(project)].remoteCommit && gitUpdateResults[projectKey(project)].remoteCommit !== gitUpdateResults[projectKey(project)].currentCommit"> → {{ gitUpdateResults[projectKey(project)].remoteCommit?.slice(0, 12) }}</template>
              </small>
              <small v-else>只允许声明远端、干净工作树和 fast-forward；依赖变化转受控制品发布。</small>
              <details v-if="gitUpdateResults[projectKey(project)]" class="operation-details">
                <summary>查看本次操作详情</summary>
                <div class="commit-flow" v-if="gitUpdateResults[projectKey(project)].currentCommit">
                  <code>{{ gitUpdateResults[projectKey(project)].currentCommit }}</code>
                  <span>→</span>
                  <code>{{ gitUpdateResults[projectKey(project)].remoteCommit || gitUpdateResults[projectKey(project)].currentCommit }}</code>
                </div>
                <ol><li v-for="step in gitUpdateResults[projectKey(project)].steps" :key="step">{{ step }}</li></ol>
                <div v-if="gitUpdateResults[projectKey(project)].changedFiles.length" class="changed-files">
                  <strong>变更文件（{{ gitUpdateResults[projectKey(project)].changedFiles.length }}）</strong>
                  <code v-for="file in gitUpdateResults[projectKey(project)].changedFiles" :key="file">{{ file }}</code>
                </div>
              </details>
            </div>
            <div class="controls">
              <button :disabled="!canOperate || !gitUpdatesEnabled || !!activeOperation" @click="gitUpdate(project, 'Check')">
                {{ activeOperation === `git/${projectKey(project)}/Check` ? '检查中…' : '检查更新' }}
              </button>
              <button class="secondary" :disabled="!canOperate || !gitUpdatesEnabled || !!activeOperation" @click="openCredentialDialog(project)">
                仓库凭据
              </button>
              <button :disabled="!canOperate || !gitUpdatesEnabled || !gitUpdateResults[projectKey(project)]?.canApply || !!activeOperation" @click="gitUpdate(project, 'Apply')">
                {{ activeOperation === `git/${projectKey(project)}/Apply` ? '更新中…' : '安全更新' }}
              </button>
            </div>
          </div>
          <div v-else class="git-update">
            <div>
              <strong>L3 · 受控版本更新</strong>
              <small>使用发布包更新全部声明组件；系统自动校验完整性、精确启停并在失败时恢复旧版本。</small>
            </div>
            <div class="controls">
              <button :disabled="!canOperate || project.status === 'Conflict' || !!activeOperation" @click="prepareControlledRelease(project)">打开更新面板 ↑</button>
            </div>
          </div>
          <div class="component" v-for="component in project.components" :key="component.componentId">
            <div class="component-main">
              <i :class="tone(component.ownership)"></i>
              <div>
                <strong>{{ component.displayName }}</strong>
                <small>{{ component.kind }} · {{ component.componentId }}</small>
                <small v-if="component.kind === 'interactiveApp'" class="session-note">窗口在声明用户的登录会话中显示；用户未登录时保持不可用，不回退到 Session 0。</small>
              </div>
            </div>
            <div class="component-state"><span :class="tone(component.ownership)">{{ component.ownership }}</span><small>{{ component.runtimeState }} / {{ component.healthState }}</small></div>
            <div class="controls">
              <button :disabled="!canOperate || !componentControlEnabled(component) || component.ownership !== 'Owned' || component.runtimeState === 'running' || !!activeOperation" @click="operate(project, component, 'Start')">启动</button>
              <button :disabled="!canOperate || !componentControlEnabled(component) || component.ownership !== 'Owned' || component.runtimeState !== 'running' || !!activeOperation" @click="operate(project, component, 'Restart')">重启</button>
              <button class="danger" :disabled="!canOperate || !componentControlEnabled(component) || component.ownership !== 'Owned' || component.runtimeState !== 'running' || !!activeOperation" @click="operate(project, component, 'Stop')">停止</button>
            </div>
          </div>
        </article>
      </div>

      <aside>
        <div class="section-title"><div><p class="eyebrow">AUDIT TRAIL</p><h2>最近操作</h2></div></div>
        <ol class="timeline">
          <li v-for="event in audits.slice(0, 20)" :key="event.eventId">
            <i :class="tone(event.outcome)"></i>
            <div class="audit-entry">
              <strong>{{ auditActionLabel(event) }}</strong>
              <p>{{ event.detail || event.category }}</p>
              <time>{{ new Date(event.occurredAt).toLocaleString() }}</time>
              <button v-if="gitAuditData(event)" type="button" class="audit-toggle" @click="toggleAudit(event.eventId)">
                {{ expandedAudits[event.eventId] ? '收起详情' : '展开详情' }}
              </button>
              <div v-if="expandedAudits[event.eventId] && gitAuditData(event)" class="audit-details">
                <dl>
                  <div><dt>操作编号</dt><dd><code>{{ gitAuditData(event)?.operationId }}</code></dd></div>
                  <div><dt>提交变化</dt><dd><code>{{ gitAuditData(event)?.fromCommit || '—' }}</code><span> → </span><code>{{ gitAuditData(event)?.toCommit || '—' }}</code></dd></div>
                  <div><dt>耗时</dt><dd>{{ formatDuration(gitAuditData(event)?.durationMilliseconds || 0) }}</dd></div>
                  <div><dt>回滚</dt><dd>{{ gitAuditData(event)?.rolledBack ? '已执行回滚' : '未发生回滚' }}</dd></div>
                </dl>
                <ol><li v-for="step in gitAuditData(event)?.steps || []" :key="step">{{ step }}</li></ol>
                <div v-if="gitAuditData(event)?.changedFiles.length" class="changed-files">
                  <strong>变更文件（{{ gitAuditData(event)?.changedFiles.length }}）</strong>
                  <code v-for="file in gitAuditData(event)?.changedFiles || []" :key="file">{{ file }}</code>
                </div>
              </div>
            </div>
          </li>
        </ol>
      </aside>
    </section>
    <div v-if="credentialProject" class="directory-modal" @click.self="closeCredentialDialog">
      <form class="credential-dialog" @submit.prevent="saveGitCredential">
        <header>
          <div><strong>配置私有 Git 仓库凭据</strong><small>{{ credentialProject.displayName }} · {{ credentialProject.environment }}</small></div>
          <button type="button" class="secondary" @click="closeCredentialDialog">关闭</button>
        </header>
        <div class="credential-body">
          <p>输入对该仓库具有只读权限的 Gitee 用户名和私人令牌。令牌由 Agent 使用 Windows DPAPI 加密保存，不写入项目清单、Git URL 或审计日志。</p>
          <label>用户名<input v-model="credentialUsername" autocomplete="username" maxlength="256"></label>
          <label>私人令牌<input v-model="credentialSecret" type="password" autocomplete="new-password" maxlength="4096"></label>
          <p v-if="credentialMessage" :class="credentialMessageTone">{{ credentialMessage }}</p>
        </div>
        <footer>
          <span>建议使用只读仓库权限的令牌</span>
          <button type="submit" :disabled="!credentialUsername.trim() || !credentialSecret || !!activeOperation">
            {{ activeOperation.startsWith('git-credential/') ? '保存中…' : '安全保存' }}
          </button>
        </footer>
      </form>
    </div>
  </main>
</template>
