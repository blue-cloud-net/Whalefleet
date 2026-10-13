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
 * 端口映射编辑器（0.0.14）。
 *
 * 与 `EnvVarEditor` 同构（草稿 + 结构化 model），但**多一层校验**：
 * 同一个容器端口被映射两次是常见的低级错误（`80:80` 又来一条 `8080:80`），
 * Docker 不会报错，但结果只有一条生效——用户会以为两个端口都通。
 * 因此这里显式把重复的容器端口标出来。
 *
 * ## 为什么宿主端口可留空
 * `-p 80` 表示「只暴露、让 Docker 随机分配宿主端口」。这是一个真实且常用的写法，
 * 而表单里强制填宿主端口会让人以为这个功能不支持。
 */
const model = defineModel<PortMapping[]>({ required: true })

const { t } = useI18n()

/** 草稿：文本框的权威来源（理由见 `EnvVarEditor` 的同名变量注释）。 */
const draft = ref(model.value.map(formatPortMapping).join('\n'))

const parsed = computed(() => parsePortText(draft.value))

watch(draft, (value) => {
  model.value = parsePortText(value).values
})

watch(
  model,
  (value) => {
    const fromDraft = draft.value
    const next = value.map(formatPortMapping).join('\n')
    if (next !== fromDraft && parsePortText(fromDraft).values.length !== value.length) {
      draft.value = next
    }
  },
  { deep: true }
)

const errorLine = computed(() => parsed.value.errorLine)

/** 被重复映射的容器端口（同一个容器端口只能对外映射到一个宿主端口）。 */
const duplicateContainerPorts = computed(() => {
  const seen = new Set<number>()
  const dupes = new Set<number>()

  for (const item of parsed.value.values) {
    if (seen.has(item.containerPort)) dupes.add(item.containerPort)
    seen.add(item.containerPort)
  }

  return dupes
})

/** 逐行校验：仅在文本不合法时才用结构化值提示。 */
function rowInvalid(index: number, key: 'hostPort' | 'containerPort'): boolean {
  const row = parsed.value.values[index]
  if (!row) return false
  if (key === 'hostPort') return row.hostPort !== null && !isValidPort(row.hostPort)

  return !isValidPort(row.containerPort)
}

function addRow(): void {
  model.value = [...model.value, { hostIp: null, hostPort: null, containerPort: 80, protocol: 'tcp' }]
}

function removeRow(index: number): void {
  model.value = model.value.filter((_, i) => i !== index)
}
</script>

<template>
  <div class="flex flex-col gap-2">
    <el-input
      v-model="draft"
      type="textarea"
      :rows="2"
      :placeholder="t('container.create.portHint')"
      :class="{ '!border-status-down': errorLine !== null }"
    />

    <p v-if="errorLine !== null" class="text-xs text-status-down">
      {{ t('container.create.portInvalid', { line: errorLine }) }}
    </p>

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

    <el-collapse>
      <el-collapse-item :title="t('container.editor.editRows')" name="rows">
        <div class="flex flex-col gap-2">
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
              :class="{ '!border-status-down': rowInvalid(index, 'hostPort') }"
              @update:model-value="
                (v: string) => (row.hostPort = v.trim().length > 0 ? Number(v) : null)
              "
            />
            <el-input
              :model-value="String(row.containerPort)"
              :placeholder="t('container.editor.containerPort')"
              class="w-1/4 font-mono app-tabular"
              @update:model-value="(v: string) => (row.containerPort = Number(v))"
            />
            <el-select
              :model-value="row.protocol"
              class="w-20"
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
        </div>
      </el-collapse-item>
    </el-collapse>
  </div>
</template>