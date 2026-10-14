<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  formatVolumeMount,
  isValidContainerPath,
  parseVolumeText,
  type VolumeMount
} from '@/utils/container-input'

/**
 * 挂载编辑器（0.0.14）。
 *
 * ## 最重要的一条：容器内路径必须绝对
 * Docker 把相对路径按 **daemon 的工作目录** 解析，而这个目录随启动方式变化
 * （systemd 通常是 `/`，手动启动可能是用户的 home）。用户以为挂到了 `./data`，
 * 实际可能挂到了 `/root/data`，且**没有任何报错**。
 * 因此逐行编辑时对相对路径实时标红，并在提示里说清原因。
 *
 * ## 为什么没有「挂载类型」选择器
 * 后端 `ContainerVolumeMapping` 同样没有 `type` 字段——bind 与具名卷的区别由 Docker
 * 根据来源路径是否存在来判定。界面若提供一个选了也不生效的类型下拉，
 * 就是 `CreateContainerDialog` 注释里明令禁止的「前端支持但后端忽略的开关」：
 * 用户会以为 `volume` 选项能强制按具名卷处理。因此一律交给 Docker 判定。
 */
const model = defineModel<VolumeMount[]>({ required: true })

/** 出错的行号；导出给父级用于拦截提交（理由见 `EnvVarEditor` 的同名注释）。 */
const errorLine = defineModel<number | null>('errorLine', { required: true })

const { t } = useI18n()

/** 草稿：文本框的权威来源（理由见 `EnvVarEditor` 的同名变量注释）。 */
const draft = ref(model.value.map(formatVolumeMount).join('\n'))

const parsed = computed(() => parseVolumeText(draft.value))

watch(draft, (value) => {
  model.value = parseVolumeText(value).values
})

watch(
  model,
  (value) => {
    const fromDraft = draft.value
    const next = value.map(formatVolumeMount).join('\n')
    if (next !== fromDraft && parseVolumeText(fromDraft).values.length !== value.length) {
      draft.value = next
    }
  },
  { deep: true }
)

watch(
  parsed,
  (value) => {
    errorLine.value = value.errorLine
  },
  { immediate: true }
)

function addRow(): void {
  model.value = [...model.value, { source: '', destination: '/data', readOnly: false }]
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
      :placeholder="t('container.create.volumeHint')"
      :class="{ '!border-status-down': errorLine !== null }"
    />

    <p v-if="errorLine !== null" class="text-xs text-status-down">
      {{ t('container.create.volumeInvalid', { line: errorLine }) }}
    </p>

    <el-collapse>
      <el-collapse-item :title="t('container.editor.editRows')" name="rows">
        <div class="flex flex-col gap-2">
          <div v-for="(row, index) in model" :key="index" class="flex items-start gap-2">
            <el-input
              v-model="row.source"
              :placeholder="t('container.editor.mountSource')"
              class="flex-1 font-mono"
            />

            <el-input
              v-model="row.destination"
              :placeholder="t('container.editor.mountDestination')"
              class="flex-1 font-mono"
              :class="{ '!border-status-down': row.destination.length > 0 && !isValidContainerPath(row.destination) }"
            />

            <el-checkbox :model-value="row.readOnly" @update:model-value="(v: boolean) => (row.readOnly = v)">
              {{ t('container.editor.readOnly') }}
            </el-checkbox>

            <el-button
              text
              :aria-label="t('common.delete')"
              class="!px-1.5 text-status-down"
              @click="removeRow(index)"
            >
              <span class="i-carbon-trash-can" />
            </el-button>
          </div>

          <!--
            相对路径的说明放在行下方而不是 tooltip：这不是「补充信息」，
            而是一个会导致「挂载到了意料之外的目录且不报错」的陷阱，
            值得占一行字。
          -->
          <p
            v-if="model.some((row) => row.destination.length > 0 && !isValidContainerPath(row.destination))"
            class="text-xs text-status-warn"
          >
            {{ t('container.editor.relativePathWarn') }}
          </p>

          <el-button size="small" class="self-start" @click="addRow">
            <span class="i-carbon-add mr-1" />{{ t('container.editor.addRow') }}
          </el-button>
        </div>
      </el-collapse-item>
    </el-collapse>
  </div>
</template>