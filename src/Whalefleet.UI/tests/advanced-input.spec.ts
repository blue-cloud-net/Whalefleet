import { describe, expect, it } from 'vitest'
import {
  MAX_NANO_CPUS,
  MIN_MEMORY_LIMIT_BYTES,
  isValidContainerName,
  parseMemoryLimit,
  parseNanoCpus,
  validateImageTag,
  validateRepository
} from '../src/utils/advanced-input'

/**
 * 0.3.0 的输入判定（热更新对话框、镜像打标签对话框共用）。
 *
 * 这些函数存在的唯一理由是把「后端一定会拒」的输入提前拦下，
 * 并把提示落到对应输入框上。因此测试盯的是**边界值**——
 * 尤其是那几个「后端会返回 200 但没生效」的 0 值。
 */
describe('isValidContainerName', () => {
  it('接受字母数字开头、其后含 _ . - 的名字', () => {
    expect(isValidContainerName('web-1')).toBe(true)
    expect(isValidContainerName('a')).toBe(true)
    expect(isValidContainerName('9lives')).toBe(true)
    expect(isValidContainerName('my_app.test-1')).toBe(true)
  })

  it('拒绝非字母数字开头', () => {
    // Docker 的规则：与 `docker run --name` 一致
    expect(isValidContainerName('-web')).toBe(false)
    expect(isValidContainerName('.web')).toBe(false)
    expect(isValidContainerName('_web')).toBe(false)
  })

  it('拒绝含斜杠与空串', () => {
    expect(isValidContainerName('a/b')).toBe(false)
    expect(isValidContainerName('')).toBe(false)
  })
})

describe('parseMemoryLimit', () => {
  it('空输入返回 null（表示不改）', () => {
    // ⚠️ null 与 0 必须能区分：null 是「不改」，而 0 在热更新里是**非法**的
    expect(parseMemoryLimit('')).toBeNull()
    expect(parseMemoryLimit('   ')).toBeNull()
  })

  it('接受大于等于 6 MiB 的输入并换算成字节', () => {
    // ⚠️ 入参是 **MiB**、出参是**字节**：输入框标签写 MiB，而请求字段是 memoryLimitBytes
    expect(parseMemoryLimit('6')).toBe(MIN_MEMORY_LIMIT_BYTES)
    expect(parseMemoryLimit('512')).toBe(512 * 1024 * 1024)
  })

  it('拒绝 0 —— 它在热更新里是「返回成功但不生效」', () => {
    // 实测 `docker update --memory=0` 返回 200，而容器上的限制根本没变。
    // 放行它等于给用户一个「保存成功却什么也没做」的控件。
    expect(parseMemoryLimit('0')).toBe('error')
  })

  it('拒绝负数、非整数与低于 6 MiB 的值', () => {
    expect(parseMemoryLimit('-1')).toBe('error')
    expect(parseMemoryLimit('1.5')).toBe('error')
    // 边界：5 MiB（6291455 × 1024 的前一档）被守护进程拒绝
    expect(parseMemoryLimit('5')).toBe('error')
    expect(parseMemoryLimit('nonsense')).toBe('error')
  })
})

describe('parseNanoCpus', () => {
  it('空输入返回 null（不改）', () => {
    expect(parseNanoCpus('')).toBeNull()
  })

  it('入参是「核」、出参是十亿分之一核', () => {
    expect(parseNanoCpus('1')).toBe(1_000_000_000)
    expect(parseNanoCpus('1.5')).toBe(1_500_000_000)
    expect(parseNanoCpus('1024')).toBe(MAX_NANO_CPUS)
  })

  it('拒绝 0（与内存同理：静默不生效）', () => {
    expect(parseNanoCpus('0')).toBe('error')
  })

  it('拒绝超过 1024 核的值', () => {
    // 「把毫核当十亿分之一核」传 2e9 是常见错值；这里按「核」解读就是 2000 核
    expect(parseNanoCpus('2000')).toBe('error')
  })
})

describe('validateImageTag', () => {
  it('空标签合法（后端取 latest）', () => {
    expect(validateImageTag('')).toBeNull()
  })

  it('接受字母或下划线开头的合法标签', () => {
    expect(validateImageTag('v1.2.3')).toBeNull()
    expect(validateImageTag('latest')).toBeNull()
    expect(validateImageTag('_rc1')).toBeNull()
  })

  it('拒绝数字开头、含斜杠、含冒号', () => {
    // 斜杠属于仓库名部分；冒号是「镜像:标签」的分隔符，两者都进不了 tag
    expect(validateImageTag('1.27')).toBe('image.tag.invalid')
    expect(validateImageTag('-v1')).toBe('image.tag.invalid')
    expect(validateImageTag('team/app')).toBe('image.tag.invalid')
    expect(validateImageTag('a:b')).toBe('image.tag.invalid')
  })

  it('拒绝超过 128 字符的标签', () => {
    expect(validateImageTag('a'.repeat(128))).toBeNull()
    expect(validateImageTag('a'.repeat(129))).toBe('image.tag.tooLong')
  })
})

describe('validateRepository', () => {
  it('接受不含标签的仓库名', () => {
    expect(validateRepository('nginx')).toBeNull()
    expect(validateRepository('registry.example.com/team/app')).toBeNull()
    // ⚠️ 端口的冒号在**最后一个斜杠之前**，不是标签
    expect(validateRepository('registry:5000/app')).toBeNull()
  })

  it('拒绝含标签的仓库名', () => {
    // 用户最容易犯的错：照着「源镜像」那一栏把 nginx:1.27 整个粘进仓库名
    expect(validateRepository('nginx:1.27')).toBe('image.tag.repositoryHasTag')
    expect(validateRepository('registry:5000/app:v1')).toBe('image.tag.repositoryHasTag')
  })
})
