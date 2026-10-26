<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  formatPortMapping,
  isValidPort,
  parsePortText,
  type PortMapping
} from '@/utils/container-input'

/**
 * 端口映射编辑器。
 *
 * ## 两种输入模式
 * - **表格**（默认）：宿主地址 / 宿主端口 / 容器端口 / 协议 四列。
 * - **批量粘贴**：一次贴一整段 `-p` 风格的端口列表（`8080:80`）。
 *
 * 粘贴保留是因为「从 Compose 文件抄端口」是高频动作；
 * 表格默认是因为「加一个端口、改一个端口」更常用，而粘贴框做不到后者。
 *
 * ## 为什么多一层校验
 * 同一个容器端口被映射两次是常见的低级错误（`80:80` 又来一条 `8080:80`），
 * Docker 不会报错，但结果只有一条生效——用户会以为两个端口都通。
 * 因此这里显式把重复的容器端口标出来。
 *
 * ## 为什么宿主端口可留空
 * `-p 80` 表示「只暴露、让 Docker 随机分配宿主端口」。这是一个真实且常用的写法，
 * 而表单里强制填宿主端口会让人以为这个功能不支持。
 */
const model = defineModel<PortMapping[]>({ required: true })

/** 出错的行号；导出给父级用于拦截提交（理由见 `EnvVarEditor` 的同名注释）。 */
const errorLine = defineModel<number | null>('errorLine', { required: true })

const { t } = useI18n()

/** 输入模式。 */
const mode = ref<'table' | 'paste'>('table')

/** 草稿：文本框的权威来源（理由见 `EnvVarEditor` 的同名变量注释）。 */
const draft = ref(model.value.map(formatPortMapping).join('\n'))

const parsed = computed(() => parsePortText(draft.value))

/** 表格模式下某一行是否合法。 */
function isRowValid(row: PortMapping): boolean {
  if (!isValidPort(row.containerPort)) return false
  if (row.hostPort !== null && !isValidPort(row.hostPort)) return false

  return true
}

/** 表格模式下第一行出错的行号（从 1 开始）；null = 全部合法。 */
const tableErrorLine = computed(() => {
  for (let index = 0; index < model.value.length; index++) {
    const row = model.value[index]
    if (row === undefined) continue
    if (isRowValid(row)) continue

    return index + 1
  }

  return null
})

watch(
  [mode, tableErrorLine, parsed],
  () => {
    errorLine.value = mode.value === 'paste' ? parsed.value.errorLine : tableErrorLine.value
  },
  { immediate: true }
)

watch(draft, (value) => {
  if (mode.value !== 'paste') return

  model.value = parsePortText(value).values
})

watch(
  model,
  (value) => {
    if (mode.value !== 'paste') return

    const fromDraft = draft.value
    const next = value.map(formatPortMapping).join('\n')
    if (next !== fromDraft && parsePortText(fromDraft).values.length !== value.length) {
      draft.value = next
    }
  },
  { deep: true }
)

/** 切到粘贴模式：把当前表格内容重新拼成文本。 */
watch(mode, (next) => {
  if (next === 'paste') {
    draft.value = model.value.map(formatPortMapping).join('\n')
  }
})

/** model 被清空（对话框每次打开都会 reset）→ 回到默认的表格模式。 */
watch(
  model,
  (value) => {
    if (value.length === 0 && mode.value !== 'table') {
      mode.value = 'table'
      draft.value = ''
    }
  },
  { deep: true }
)

/** 被重复映射的容器端口（同一个容器端口只能对外映射到一个宿主端口）。 */
const duplicateContainerPorts = computed(() => {
  const seen = new Set<number>()
  const dupes = new Set<number>()

  for (const item of model.value) {
    if (seen.has(item.containerPort)) dupes.add(item.containerPort)
    seen.add(item.containerPort)
  }

  return dupes
})

function addRow(): void {
  model.value = [...model.value, { hostIp: null, hostPort: null, containerPort: 80, protocol: 'tcp' }]
}

function removeRow(index: number): void {
  model.value = model.value.filter((_, i) => i !== index)
}
</script>

<template>
  <div class="flex flex-col gap-2">
    <!-- 模式切换：两个文字小按钮常驻，用户不该为了切模式先点开一个菜单。 -->
    <div class="flex items-center justify-end gap-1">
      <el-button
        size="small"
        text
        :type="mode === 'table' ? 'primary' : 'default'"
        @click="mode = 'table'"
      >
        {{ t('container.editor.modeTable') }}
      </el-button>
      <el-button
        size="small"
        text
        :type="mode === 'paste' ? 'primary' : 'default'"
        @click="mode = 'paste'"
      >
        {{ t('container.editor.modePaste') }}
      </el-button>
    </div>

    <template v-if="mode === 'table'">
      <p v-if="model.length === 0" class="text-xs text-content-faint">
        {{ t('container.editor.noRows') }}
      </p>

      <div v-for="(row, index) in model" :key="index" class="flex items-start gap-2">
        <el-input
          :model-value="row.hostIp ?? ''"
          :placeholder="t('container.editor.hostIp')"
          class="w-1/4 font-mono"
          @update:model-value="(v: string) => (row.hostIp = v.trim().length > 0 ? v.trim() : null)"
        />
        <el-input
          :model-value="row.hostPort?.toString() ?? ''"
          :placeholder="t('container.editor.hostPort')"
          class="w-1/4 font-mono app-tabular"
          :class="{ '!border-status-down': row.hostPort !== null && !isValidPort(row.hostPort) }"
          @update:model-value="
            (v: string) => (row.hostPort = v.trim().length > 0 ? Number(v) : null)
          "
        />
        <el-input
          :model-value="String(row.containerPort)"
          :placeholder="t('container.editor.containerPort')"
          class="w-1/4 font-mono app-tabular"
          :class="{ '!border-status-down': !isValidPort(row.containerPort) }"
          @update:model-value="(v: string) => (row.containerPort = Number(v))"
        />
        <el-select
          :model-value="row.protocol"
          class="!w-20"
          @update:model-value="(v: 'tcp' | 'udp') => (row.protocol = v)"
        >
          <el-option value="tcp" label="TCP" />
          <el-option value="udp" label="UDP" />
        </el-select>
        <el-button
          text
          :aria-label="t('common.delete')"
          class="!px-1.5 text-status-down"
          @click="removeRow(index)"
        >
          <span class="i-carbon-trash-can" />
        </el-button>
      </div>

      <el-button size="small" class="self-start" @click="addRow">
        <span class="i-carbon-add mr-1" />{{ t('container.editor.addRow') }}
      </el-button>

      <p v-if="tableErrorLine !== null" class="text-xs text-status-down">
        {{ t('container.editor.rowInvalid', { line: tableErrorLine }) }}
      </p>
    </template>

    <template v-else>
      <el-input
        v-model="draft"
        type="textarea"
        :rows="2"
        :placeholder="t('container.create.portHint')"
        :class="{ '!border-status-down': parsed.errorLine !== null }"
      />

      <p v-if="parsed.errorLine !== null" class="text-xs text-status-down">
        {{ t('container.create.portInvalid', { line: parsed.errorLine }) }}
      </p>
    </template>

    <el-alert
      v-if="duplicateContainerPorts.size > 0"
      type="warning"
      :closable="false"
      class="!py-1.5"
    >
      <template #title>
        <span class="text-xs">
          {{ t('container.editor.portDuplicate', { ports: [...duplicateContainerPorts].join(', ') }) }}
        </span>
      </template>
    </el-alert>
  </div>
</template>
