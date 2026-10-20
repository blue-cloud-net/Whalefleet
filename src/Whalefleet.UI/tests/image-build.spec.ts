import { describe, expect, it } from 'vitest'
import { isTarArchive, parseKeyValueJson, validateBuildTag } from '../src/utils/image-build'

/**
 * 构建表单校验的守卫。
 *
 * 重点是 `parseKeyValueJson` 的**边界**：它决定了提交给后端的 JSON 是什么样，
 * 而后端只回一个笼统的 `1000`，用户在界面上看不出是哪一段写错了。
 */
describe('isTarArchive', () => {
  it('接受 tar / tar.gz / tgz', () => {
    expect(isTarArchive('context.tar')).toBe(true)
    expect(isTarArchive('context.tar.gz')).toBe(true)
    expect(isTarArchive('CONTEXT.TAR')).toBe(true)
    expect(isTarArchive('image.tgz')).toBe(true)
  })

  it('拒绝其他归档与普通文件', () => {
    // Docker 只认 tar；zip 是用户在 Windows 上最常打错的一种
    expect(isTarArchive('context.zip')).toBe(false)
    expect(isTarArchive('Dockerfile')).toBe(false)
    expect(isTarArchive('context.tar.bz2')).toBe(false)
  })
})

describe('parseKeyValueJson', () => {
  it('空输入不发送该字段', () => {
    expect(parseKeyValueJson('')).toEqual({ ok: true })
    expect(parseKeyValueJson('   ')).toEqual({ ok: true })
  })

  it('合法对象原样返回', () => {
    expect(parseKeyValueJson('{"VERSION":"1.0","DEBUG":"true"}')).toEqual({
      ok: true,
      value: { VERSION: '1.0', DEBUG: 'true' }
    })
  })

  it('数字与布尔被转成字符串（Docker 侧本来就是字符串语义）', () => {
    expect(parseKeyValueJson('{"RETRIES":3,"ON":false}')).toEqual({
      ok: true,
      value: { RETRIES: '3', ON: 'false' }
    })
  })

  it('语法错误 → badJson', () => {
    expect(parseKeyValueJson('{VERSION:1}')).toEqual({
      ok: false,
      errorKey: 'image.build.error.badJson'
    })
  })

  it('数组 / 标量 / null → notObject（后端只收 JSON 对象）', () => {
    for (const text of ['["a"]', '"a"', '1', 'null']) {
      expect(parseKeyValueJson(text), text).toEqual({
        ok: false,
        errorKey: 'image.build.error.notObject'
      })
    }
  })

  it('嵌套对象 → badValue（Dictionary<string,string> 反序列化会失败）', () => {
    expect(parseKeyValueJson('{"A":{"B":"1"}}')).toEqual({
      ok: false,
      errorKey: 'image.build.error.badValue'
    })
  })

  it('空键 → emptyKey（后端会以 1000 拒绝）', () => {
    expect(parseKeyValueJson('{"":"x"}')).toEqual({
      ok: false,
      errorKey: 'image.build.error.emptyKey'
    })
    expect(parseKeyValueJson('{"  ":"x"}')).toEqual({
      ok: false,
      errorKey: 'image.build.error.emptyKey'
    })
  })
})

describe('validateBuildTag', () => {
  it('必填', () => {
    expect(validateBuildTag('')).toBe('image.build.error.tagRequired')
    expect(validateBuildTag('   ')).toBe('image.build.error.tagRequired')
  })

  it('含空白 → 非法（Docker 引用语法不允许）', () => {
    expect(validateBuildTag('my app:v1')).toBe('image.build.error.tagHasSpace')
  })

  it('正常引用通过（含端口与标签）', () => {
    expect(validateBuildTag('harbor.local:5000/app:v1')).toBeNull()
    expect(validateBuildTag('myapp:v1')).toBeNull()
  })
})
