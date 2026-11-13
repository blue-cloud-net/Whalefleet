<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { importContainer, importImage, type UploadProgress } from '@/api/transfer'

/**
 * 导入（镜像 / 容器）对话框（0.14.0）。
 *
 * ## 两档的副标题必须不同
 * 镜像导入是 `docker load`（**保留**原有的层、标签与历史），容器导入是
 * `docker import`（把一份文件系统快照压成**单层**镜像，没有历史）。
 * 用一句「导入 tar」概括，用户会拿容器导出包去走镜像导入，
 * 然后得到一个「能导入但跑不起来」的镜像。
 *
 * ## 上限提示
 * 服务端有可配置上限（默认 20 GB），且是**边读边数**——因此超限时是
 * 「传到一半失败」，不是「选完文件立刻报错」。界面在选中文件时就先提醒一句
 * 能省掉一次几分钟的等待。
 */

const props = defineProps<{
  modelValue: boolean
  hostId: string
  kind: 'image' | 'container'
}>()

const emit = defineEmits<{
  'update:modelValue': [boolean]
  /** 导入成功（父页据此刷新列表） */
  done: []
}>()

const { t } = useI18n()

const file = ref<File | null>(null)
const busy = ref(false)
const percent = ref<number | null>(null)
/** 容器导入的可选标签 */
const repository = ref('')
const tag = ref('latest')

const visible = computed({
  get: () => props.modelValue,
  set: (value: boolean) => emit('update:modelValue', value)
})

/** 选中文件。 */
function onFileChange(event: Event): void {
  const input = event.target as HTMLInputElement

  file.value = input.files?.[0] ?? null
  input.value = ''
}

/** 执行导入。 */
async function submit(): Promise<void> {
  if (!file.value) {
    return
  }

  busy.value = true
  percent.value = 0

  try {
    const progress: UploadProgress = (value) => {
      percent.value = value
    }

    if (props.kind === 'image') {
      const result = await importImage(props.hostId, file.value, progress)

      ElMessage.success(
        result.images.length > 0
          ? t('transfer.import.imageSucceeded', { count: result.images.length })
          : // 镜像已存在且内容一致时守护进程一条消息都不回，这不是失败
            t('transfer.import.imageNoop', { count: result.messageCount })
      )
    } else {
      await importContainer(
        props.hostId,
        file.value,
        { repository: repository.value.trim() || undefined, tag: tag.value.trim() || undefined },
        progress
      )

      ElMessage.success(t('transfer.import.containerSucceeded'))
    }

    emit('done')
    visible.value = false
    file.value = null
  } finally {
    busy.value = false
    percent.value = null
  }
}
</script>

<template>
  <el-dialog v-model="visible" :title="t('transfer.import.title')" width="520px">
    <div class="flex flex-col gap-3">
      <p class="text-xs text-content-muted">
        {{
          kind === 'image'
            ? t('transfer.import.subtitleImage')
            : t('transfer.import.subtitleContainer')
        }}
      </p>

      <input type="file" accept=".tar,application/x-tar" :disabled="busy" @change="onFileChange" />

      <p v-if="file" class="text-xs text-content-muted">{{ file.name }}</p>

      <template v-if="kind === 'container'">
        <el-input v-model="repository" :placeholder="t('transfer.import.repository')" clearable />
        <el-input v-model="tag" :placeholder="t('transfer.import.tag')" clearable />
      </template>

      <el-progress
        v-if="busy"
        :percentage="percent ?? 0"
        :indeterminate="percent == null"
        :duration="3"
      />

      <p class="text-xs text-content-muted">{{ t('transfer.import.sizeHint') }}</p>
    </div>

    <template #footer>
      <el-button :disabled="busy" @click="visible = false">{{ t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="busy" :disabled="!file" @click="submit">
        {{ t('transfer.import.start') }}
      </el-button>
    </template>
  </el-dialog>
</template>
