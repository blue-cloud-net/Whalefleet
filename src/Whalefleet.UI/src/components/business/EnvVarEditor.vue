<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  formatEnvEntry,
  isValidEnvKey,
  parseEnvText,
  type EnvEntry
} from '@/utils/container-input'

/**
 * 环境变量编辑器。
 *
 * ## 两种输入模式
 * - **表格**（默认）：一行一个变量，键与值分列。改少量项时最直观。
 * - **批量粘贴**：一次贴一整段 `KEY=VALUE`。
 *
 * ## 为什么表格是默认而不是粘贴
 * 用户手上的环境变量几乎总是**已经存在的**——从 Compose 文件、`.env` 文件或
 * 另一台机器的容器里复制过来；逐行填表格要把 `KEY=VALUE` 手工拆成两列，
 * 是纯粹的重复劳动。因此粘贴必须**保留**。
 * 但反过来，「只改一个变量的值」是更常见的日常操作，而粘贴框做不到——
 * 它只有一整个文本框。
 * 两者互有长短，所以做成**可切换的两种模式**，而不是二选一。
 *
 * ## 为什么值列用 input 而不是 textarea
 * 环境变量的值常常是超长连接串。`input` 不会把对话框撑成几十行，
 * 横向滚动 + tooltip 已经足够；用 textarea 反而让 3 条变量占满整个视口。
 */
const model = defineModel<EnvEntry[]>({ required: true })

/**
 * 出错的行号（`null` = 全部合法），**双向**导出。
 *
 * 必须给父级：解析失败时 model 里只有出错行**之前**的合法部分，
 * 父级若看不到 errorLine 就会拿着这份残缺的列表提交，
 * 建出一个「少了一条环境变量但提示成功」的容器。本组件负责显示错误，
 * 「要不要拦下提交」是父级的决定，因此把判据交出去而不是就地禁用按钮。
 */
const errorLine = defineModel<number | null>('errorLine', { required: true })

const { t } = useI18n()

/** 输入模式。 */
const mode = ref<'table' | 'paste'>('table')

/**
 * 文本框的**原始草稿**（粘贴模式用）。
 *
 * 不能用 `computed` 的 get/set 与 model 双向绑定：解析出错时 model 里只剩
 * 合法的前缀，getter 再拼出来的文本就会**把出错那一行抹掉**——用户正在改的
 * 第 2 行凭空消失，比报错本身还糟。
 */
const draft = ref(model.value.map(formatEnvEntry).join('\n'))

const parsed = computed(() => parseEnvText(draft.value))

/** 表格模式下某一行是否合法。 */
function isRowValid(entry: EnvEntry): boolean {
  // 整行都空 = 还没填，提交时会被丢掉，不算错误
  if (entry.key.length === 0) return entry.value.length === 0

  return isValidEnvKey(entry.key)
}

/** 表格模式下第一行出错的行号（从 1 开始）；null = 全部合法。 */
const tableErrorLine = computed(() => {
  for (let index = 0; index < model.value.length; index++) {
    const entry = model.value[index]
    if (entry === undefined) continue
    if (isRowValid(entry)) continue

    return index + 1
  }

  return null
})

/**
 * 同步给父级：两种模式各有各的判据。
 *
 * `immediate` 保证「打开即有错」（粘贴残留）也能立刻反映到提交按钮上。
 */
watch(
  [mode, tableErrorLine, parsed],
  () => {
    errorLine.value = mode.value === 'paste' ? parsed.value.errorLine : tableErrorLine.value
  },
  { immediate: true }
)

/** 把草稿同步给 model（粘贴模式下每次输入都调）：只写入已解析出的合法部分。 */
watch(draft, (value) => {
  if (mode.value !== 'paste') return

  model.value = parseEnvText(value).values
})

/**
 * model 被外部改动（reset）时重建草稿；表格模式下不动草稿——
 * 那时的 model 变化是**用户自己**在编辑，重建草稿会把粘贴框的内容冲掉。
 */
watch(
  model,
  (value) => {
    if (mode.value !== 'paste') return

    const fromDraft = draft.value
    const next = value.map(formatEnvEntry).join('\n')
    // 已经一致就别动草稿，否则每次输入都会把光标打到末尾
    if (next !== fromDraft && parseEnvText(fromDraft).values.length !== value.length) {
      draft.value = next
    }
  },
  { deep: true }
)

/** 切到粘贴模式：把当前表格内容重新拼成文本。 */
watch(mode, (next) => {
  if (next === 'paste') {
    draft.value = model.value.map(formatEnvEntry).join('\n')
  }
})

/**
 * model 被清空（对话框每次打开都会 reset）→ 回到默认的表格模式。
 *
 * 刻意不靠「父级传个 visible 进来」实现：编辑器的状态归自己管，
 * 而「内容没了」本身就是「回到初始状态」的充分信号。
 */
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

/** 同一批里重复的键：后写的会覆盖先写的，这是 Docker 的行为，界面必须点出来。 */
const duplicateKeys = computed(() => {
  const seen = new Set<string>()
  const dupes = new Set<string>()

  for (const entry of model.value) {
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
    <!--
      模式切换：两个**文字**小按钮而不是下拉——
      它们要一直可见（用户不该为了切模式先点开一个菜单）。
    -->
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

      <div v-for="(entry, index) in model" :key="index" class="flex items-start gap-2">
        <el-input
          v-model="entry.key"
          :placeholder="t('container.editor.envKey')"
          class="w-2/5 font-mono"
          :class="{ '!border-status-down': !isRowValid(entry) }"
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

      <p v-if="tableErrorLine !== null" class="text-xs text-status-down">
        {{ t('container.editor.rowInvalid', { line: tableErrorLine }) }}
      </p>
    </template>

    <template v-else>
      <el-input
        v-model="draft"
        type="textarea"
        :rows="3"
        :placeholder="t('container.create.envHint')"
        :class="{ '!border-status-down': parsed.errorLine !== null }"
      />

      <p v-if="parsed.errorLine !== null" class="text-xs text-status-down">
        {{ t('container.create.envInvalid', { line: parsed.errorLine }) }}
      </p>
    </template>

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
  </div>
</template>
