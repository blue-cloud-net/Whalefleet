<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  isValidEnvKey,
  parseEnvText,
  type EnvEntry
} from '@/utils/container-input'

/**
 * 环境变量编辑器（0.0.14）。
 *
 * ## 为什么默认「批量粘贴」而不是逐行表格
 * 用户手上的环境变量几乎总是**已经存在的**——从 Compose 文件、`.env` 文件或
 * 另一台机器的容器里复制过来。逐行填表格要把 `KEY=VALUE` 手工拆成两列，
 * 是纯粹的重复劳动。
 *
 * 逐行模式只作为「编辑少量项」时的补充：粘贴进来的内容同样能逐项修改，
 * 不必「一次性粘贴 + 一次性提交」这种非此即彼的模型。
 *
 * ## 为什么值列用 input 而不是 textarea
 * 环境变量的值常常是超长连接串。`input` 不会把对话框撑成几十行，
 * 横向滚动 + tooltip 已经足够；用 textarea 反而让 3 条变量占满整个视口。
 */
const model = defineModel<EnvEntry[]>({ required: true })

const { t } = useI18n()

/**
 * 文本框的**原始草稿**。
 *
 * 不能用 `computed` 的 get/set 与 model 双向绑定：解析出错时 model 里只剩
 * 合法的前缀，getter 再拼出来的文本就会**把出错那一行抹掉**——用户正在改的
 * 第 2 行凭空消失，比报错本身还糟。
 *
 * 草稿只在两种情况下与 model 同步：
 *  1. model 被外部整体替换（对话框每次打开都会 reset）→ 重新生成文本
 *  2. model 与「按草稿解析的结果」不一致（用户改的是结构化行）→ 重新生成文本
 * 平时用户打字则只更新草稿，不打扰 model。
 */
const draft = ref(model.value.map((entry) => `${entry.key}=${entry.value}`).join('\n'))

const parsed = computed(() => parseEnvText(draft.value))

/** 把草稿同步给 model（每次输入都调）：只写入已解析出的合法部分。 */
watch(draft, (value) => {
  model.value = parseEnvText(value).values
})

/** model 被外部改动（reset / 结构化行编辑）时重建草稿。 */
watch(
  model,
  (value) => {
    const fromDraft = draft.value
    const next = value.map((entry) => `${entry.key}=${entry.value}`).join('\n')
    // 已经一致就别动草稿，否则每次输入都会把光标打到末尾
    if (next !== fromDraft && parseEnvText(fromDraft).values.length !== value.length) {
      draft.value = next
    }
  },
  { deep: true }
)

/** 哪一行不合法；由 `parseEnvText` 的 errorLine 直接给出。 */
const errorLine = computed(() => parsed.value.errorLine)

/** 同一批里重复的键：后写的会覆盖先写的，这是 Docker 的行为，界面必须点出来。 */
const duplicateKeys = computed(() => {
  const seen = new Set<string>()
  const dupes = new Set<string>()

  for (const entry of parsed.value.values) {
    if (seen.has(entry.key)) dupes.add(entry.key)
    seen.add(entry.key)
  }

  return dupes
})

function addRow(): void {
  model.value = [...model.value, { key: '', value: '' }]
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
      :rows="3"
      :placeholder="t('container.create.envHint')"
      :class="{ '!border-status-down': errorLine !== null }"
    />

    <p v-if="errorLine !== null" class="text-xs text-status-down">
      {{ t('container.create.envInvalid', { line: errorLine }) }}
    </p>

    <el-alert
      v-if="duplicateKeys.size > 0"
      type="warning"
      :closable="false"
      class="!py-1.5"
    >
      <template #title>
        <span class="text-xs">
          {{ t('container.editor.envDuplicate', { keys: [...duplicateKeys].join(', ') }) }}
        </span>
      </template>
    </el-alert>

    <el-collapse>
      <el-collapse-item :title="t('container.editor.editRows')" name="rows">
        <div class="flex flex-col gap-2">
          <div
            v-for="(entry, index) in model"
            :key="index"
            class="flex items-start gap-2"
          >
            <el-input
              v-model="entry.key"
              :placeholder="t('container.editor.envKey')"
              class="w-2/5 font-mono"
              :class="{ '!border-status-down': entry.key.length > 0 && !isValidEnvKey(entry.key) }"
            />
            <el-input
              v-model="entry.value"
              :placeholder="t('container.editor.envValue')"
              class="flex-1 font-mono"
            />
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