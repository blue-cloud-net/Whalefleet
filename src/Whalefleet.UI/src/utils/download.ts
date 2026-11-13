/**
 * 浏览器端「另存为」工具。
 *
 * ## 为什么需要一个工具函数
 * `<a download>` 的可用性依赖**同源**：跨源时 `download` 属性会被浏览器忽略，
 * 变成「打开一个新标签页」。而下载地址要么带一次性票据（后端生成），
 * 要么是 Blob（前端取回），两者都需要把「保存」这件事在**前端**完成。
 *
 * ## 为什么 Blob 必须 `revokeObjectURL`
 * 每个 `URL.createObjectURL` 都会让浏览器**持有**那份数据，不释放就是内存泄漏；
 * 大文件（几百 MB）累积几次就能把标签页拖死。因此统一在触发点击后立即排一个
 * 微任务释放——过早释放会让下载来不及开始，过晚则泄漏。
 */

/**
 * 用 Blob 触发一次「另存为」。
 *
 * @param blob 文件内容
 * @param fileName 保存时建议的文件名（浏览器可能按设置改名）
 */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)

  try {
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = fileName
    anchor.rel = 'noopener'
    anchor.style.display = 'none'

    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
  } finally {
    // 交给下一个宏任务：同步释放时部分浏览器会取消刚刚开始的下载
    setTimeout(() => URL.revokeObjectURL(url), 0)
  }
}

/**
 * 直接访问下载地址（票据已在查询串里）。
 *
 * **不要**给它加 `download` 属性：跨源时该属性无效，而带上去反而会让
 * 某些浏览器把响应当成附件之外的形态处理。让服务端的
 * `Content-Disposition` 决定文件名。
 *
 * @param url 服务端给出的下载地址
 */
export function openDownloadUrl(url: string): void {
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.rel = 'noopener'
  anchor.style.display = 'none'

  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
}

/**
 * 把文本作为文件保存（用于「导出定义」这类纯文本内容）。
 *
 * @param content 文本内容
 * @param fileName 保存时建议的文件名
 * @param mimeType MIME 类型；默认 `text/plain`
 */
export function saveText(content: string, fileName: string, mimeType = 'text/plain'): void {
  saveBlob(new Blob([content], { type: `${mimeType};charset=utf-8` }), fileName)
}
