<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ShortcutRow } from '@/composables/useShortcuts'

/**
 * 快捷键帮助（0.0.14）。
 *
 * 内容由 `useShortcuts` 的 `rows` 提供，而 rows 又由**同一份注册表**生成——
 * 这里不含任何硬编码的键位表。因此「加了新快捷键但忘记写进帮助」不可能发生。
 */
const visible = defineModel<boolean>({ required: true })

const props = defineProps<{
  /** `useShortcuts` 返回的行 */
  rows: ShortcutRow[]
}>()

const { t } = useI18n()

/**
 * 按分组聚合，但**保持首次出现的顺序**。
 *
 * 顺序由 rows 决定（全局在前、导航在后），因此不需要排序——
 * 「按 key 排序」会把中文分组名按拼音重排，与注册顺序脱节。
 */
const groups = computed(() => {
  const order: string[] = []
  const buckets = new Map<string, ShortcutRow[]>()

  for (const row of props.rows) {
    if (!buckets.has(row.group)) {
      buckets.set(row.group, [])
      order.push(row.group)
    }
    buckets.get(row.group)?.push(row)
  }

  return order.map((name) => ({ name, rows: buckets.get(name) ?? [] }))
})
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="t('shortcut.title')"
    width="min(520px, 94vw)"
    append-to-body
  >
    <div class="flex flex-col gap-5">
      <section v-for="group in groups" :key="group.name">
        <h3 class="text-xs font-medium text-content-muted mb-2">{{ group.name }}</h3>
        <ul class="flex flex-col gap-1.5">
          <li
            v-for="row in group.rows"
            :key="row.keys"
            class="flex items-center justify-between gap-4 text-sm"
          >
            <span class="text-content-body">{{ row.description }}</span>
            <span class="flex items-center gap-1 shrink-0">
              <kbd
                v-for="part in row.keys.split(' then ')"
                :key="part"
                class="app-kbd"
              >{{ part }}</kbd>
            </span>
          </li>
        </ul>
      </section>
    </div>

    <template #footer>
      <p class="text-xs text-content-faint leading-relaxed">
        {{ t('shortcut.inputHint') }}
      </p>
    </template>
  </el-dialog>
</template>

<style scoped>
.app-kbd {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 1.75rem;
  padding: 0.15rem 0.45rem;
  font-family: 'JetBrains Mono', ui-monospace, monospace;
  font-size: 0.72rem;
  line-height: 1.5;
  border-radius: var(--radius-sm);
  border: 1px solid var(--border-subtle);
  background: var(--surface-sunken);
  color: var(--text-muted);
}
</style>