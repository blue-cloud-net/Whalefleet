/**
 * `KEY=VALUE` 条目的展示用拆分。
 *
 * Docker 给的环境变量就是一个**字符串数组**，每条形如 `KEY=VALUE`。
 * 详情页把它当表格展示（键一列、值一列）比逐行输出原文更好读：
 * 值里常含连接串，原文一行往往上百字符，一列排下来就只能横着读。
 *
 * ⚠️ **只在第一个 `=` 处切分**：值本身可以含 `=`（`JWT_SECRET=abc==`、
 * `CONN=Server=x;Pwd=y`），用 `split('=')` 会把值一起切碎。
 * 找不到 `=` 时整条当作键、值为空——守护进程允许这种条目存在（`FOO`），
 * 把它整个塞进值列反而是错的。
 */
export function splitEnvEntry(entry: string): { key: string; value: string } {
  const index = entry.indexOf('=')
  if (index < 0) return { key: entry, value: '' }

  return { key: entry.slice(0, index), value: entry.slice(index + 1) }
}
