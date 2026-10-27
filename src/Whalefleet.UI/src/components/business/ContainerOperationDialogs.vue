<script setup lang="ts">
import { ref } from 'vue'
import ContainerRenameDialog from '@/components/business/ContainerRenameDialog.vue'
import ContainerUpdateDialog from '@/components/business/ContainerUpdateDialog.vue'
import ContainerDuplicateDialog from '@/components/business/ContainerDuplicateDialog.vue'
import ContainerAddVolumesDialog from '@/components/business/ContainerAddVolumesDialog.vue'
import ContainerKillDialog from '@/components/business/ContainerKillDialog.vue'
import ContainerRecreateDialog from '@/components/business/ContainerRecreateDialog.vue'
import ContainerEditDialog from '@/components/business/ContainerEditDialog.vue'

/**
 * 容器操作对话框的**统一宿主**（0.7.0）。
 *
 * ## 为什么要有这一层
 *
 * 这些对话框原本各自挂在 `ContainersView` 上，而 0.7.0 的详情页
 * **也要**同样的七个操作（「详情页自成一体」）。
 * 两处各挂一次意味着：
 * - 每加一个对话框要改两个页面（改漏一个的症状是「列表页有、详情页没有」）；
 * - 而两份挂载代码长得一模一样，却会各自漂移。
 *
 * 把它们收进一个宿主组件后，两个页面**各挂一次**这个宿主即可，
 * 而「有哪些操作、每个操作弹什么」只有一份定义。
 *
 * ## 为什么用 `target` 而不是六个独立的 visible + id
 *
 * 六个独立的 visible 与 id 会允许出现「A 对话框开着，B 的 id 却指着另一个容器」的组合——
 * 而那种状态下用户点确认，操作会作用在**错的容器**上。
 * 一个 `target` 从构造上排除了这种组合。
 */
export interface ContainerOperationTarget {
  id: string
  name: string
}

const props = defineProps<{
  /** 主机标识（主机名） */
  hostId: string
  /** 当前操作对象；`null` = 没有待处理的操作 */
  target: ContainerOperationTarget | null
}>()

const emit = defineEmits<{
  /** 任一操作完成（调用方刷新列表 / 详情） */
  done: []
  /**
   * 发生了重建（容器 ID 变了）。
   *
   * ⚠️ **调用方必须处理这个事件**：重建后旧容器已让位、新容器有了新 ID，
   * 详情页若还停在旧 ID 上就会 404。因此详情页要 `router.replace` 到新 ID。
   */
  recreated: [result: { containerId: string; replacedContainerId: string | null }]
}>()

const renameVisible = ref(false)
const updateVisible = ref(false)
const duplicateVisible = ref(false)
const addVolumesVisible = ref(false)
const killVisible = ref(false)
const recreateVisible = ref(false)
const editVisible = ref(false)

/** 终止对话框的目标（可以是多个）。 */
const killTargets = ref<ContainerOperationTarget[]>([])

/**
 * 打开某个操作对话框。
 *
 * ⚠️ **必须在调用方先设好 `target`**：对话框读的是 `props.target`，
 * 而那正是「这次操作作用在哪个容器上」的**唯一**来源。
 */
function openDialog(dialog: { value: boolean }): void {
  if (!props.target) return

  dialog.value = true
}

function openRename(): void {
  openDialog(renameVisible)
}

function openUpdate(): void {
  openDialog(updateVisible)
}

function openDuplicate(): void {
  openDialog(duplicateVisible)
}

function openAddVolumes(): void {
  openDialog(addVolumesVisible)
}

function openRecreate(): void {
  openDialog(recreateVisible)
}

function openEdit(): void {
  openDialog(editVisible)
}

/**
 * 打开终止对话框（可带多个目标——批量终止时一次选信号，应用到全部）。
 */
function openKill(targets: ContainerOperationTarget[]): void {
  killTargets.value = targets
  killVisible.value = true
}

defineExpose({
  openRename,
  openUpdate,
  openDuplicate,
  openAddVolumes,
  openKill,
  openRecreate,
  openEdit
})
</script>

<template>
  <ContainerRenameDialog
    v-model="renameVisible"
    :host-id="hostId"
    :container-id="target?.id ?? null"
    :current-name="target?.name ?? ''"
    @renamed="emit('done')"
  />

  <ContainerUpdateDialog
    v-model="updateVisible"
    :host-id="hostId"
    :container-id="target?.id ?? null"
    @updated="emit('done')"
  />

  <ContainerDuplicateDialog
    v-model="duplicateVisible"
    :host-id="hostId"
    :container-id="target?.id ?? null"
    :current-name="target?.name ?? ''"
    @duplicated="emit('done')"
  />

  <ContainerAddVolumesDialog
    v-model="addVolumesVisible"
    :host-id="hostId"
    :container-id="target?.id ?? null"
    :current-name="target?.name ?? ''"
    @recreated="emit('done')"
  />

  <ContainerKillDialog
    v-model="killVisible"
    :host-id="hostId"
    :targets="killTargets"
    @killed="emit('done')"
  />

  <ContainerRecreateDialog
    v-model="recreateVisible"
    :host-id="hostId"
    :container-id="target?.id ?? null"
    :current-name="target?.name ?? ''"
    @recreated="(result) => emit('recreated', result)"
  />

  <ContainerEditDialog
    v-model="editVisible"
    :host-id="hostId"
    :container-id="target?.id ?? null"
    @recreated="(result) => emit('recreated', result)"
  />
</template>
