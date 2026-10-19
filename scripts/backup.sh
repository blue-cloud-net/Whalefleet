#!/usr/bin/env bash
#
# Whalefleet — SQLite 备份脚本
# ============================================================================
# 用 `sqlite3 .backup` 做**在线**备份：不锁库、不中断服务。
#
# ## 为什么不能直接 cp 数据库文件
# SQLite 默认是 rollback journal 模式（本项目虽已启用 WAL，但两者都适用）：
# 直接 `cp whalefleet.db` 拷到的是**某一瞬时**的文件状态，
# 若此刻正在写事务，拷出来的可能是撕裂的——恢复时报
# "database disk image is malformed"，而这时往往已经需要它了。
#
# `.backup` 命令由 SQLite 自己完成：它会拿一个读事务保证快照一致，
# 因此**可以在服务运行中执行**。代价是要花一份数据库大小的临时空间。
#
# ## 前提
# 宿主机需要 sqlite3：
#   Debian/Ubuntu : apt-get install sqlite3
#   Alpine        : apk add sqlite3
#   macOS         : 自带
#
# 镜像内**已自带** sqlite3（与 curl 一起在 runtime 阶段安装），
# 因此容器内执行无需额外准备。宿主机上跑则需要自行安装。
#
# ## jwt.key 也必须一并备份（重要）
# JWT 签名密钥在未配置 JWT__SECRET 时由程序自动生成到 /app/data/jwt.key。
# 恢复备份时若漏掉它，程序会生成一份**新**密钥，而旧密钥签发的令牌全部失效——
# 表现为「恢复完之后所有人都被登出」，且没有任何报错。因此密钥与数据库同目录存一份。
#
# 密钥文件是普通文件且极小（几十字节），直接 cp 即可，无需 .backup。
#
# ## ⚠️ 手工恢复时的属主陷阱
# 用 `docker cp` 把备份放进数据卷，文件会属到**执行 cp 的那个宿主机用户**（常见是 1000），
# 而容器内 app 用户是 10001。jwt.key 是 0600，于是 app 读不到它 → 启动直接失败：
#   Permission denied / unable to open database file
# 恢复后必须改回属主（或直接在容器内 cp，容器内 cp 会落到 app 用户）：
#   docker exec -u 0 whalefleet chown 10001:10001 /app/data/jwt.key /app/data/registry.key /app/data/whalefleet.db
#
# ## 用法
#   # 容器内执行（推荐：镜像已含 sqlite3）
#   docker compose exec whalefleet bash /app/scripts/backup.sh
#   # 取出备份
#   docker compose cp whalefleet:/app/data/backups ./backups
#
#   # 宿主机执行
#   WHALEFLEET_DB=/srv/whalefleet/data/whalefleet.db \
#     bash scripts/backup.sh
#
# ## cron 示例（容器内，每天 03:17）
#   17 3 * * * docker exec whalefleet bash /app/scripts/backup.sh \
#     >> /var/log/whalefleet-backup.log 2>&1
#
# 之所以给 17 而不是 0：整点是所有定时任务的默认目标，错峰能避开
# 宿主机上其他任务在同一秒争抢 IO 的情况。
# ============================================================================

set -Eeuo pipefail

# ---------------------------------------------------------------------------
# 配置
# ---------------------------------------------------------------------------
DB_PATH="${WHALEFLEET_DB:-/app/data/whalefleet.db}"
BACKUP_DIR="${WHALEFLEET_BACKUP_DIR:-/app/data/backups}"
# JWT 签名密钥。它与数据库同目录，但**不是**数据库文件，因此不能靠 .backup 带出。
# 路径由 DB_PATH 推导，保证两者永远在同一目录。
JWT_SECRET_PATH="${WHALEFLEET_JWT_SECRET:-$(dirname "${DB_PATH}")/jwt.key}"
# Registry 口令加密密钥（0.4.0）。与 jwt.key 同理：它与数据库同目录，
# 但**不是**数据库文件，因此不能靠 .backup 带出。
# ⚠️ 漏掉它的后果：已保存的 Registry 口令全部无法解密（表现为「认证失败」，
#    但根因是密钥没了）。
REGISTRY_KEY_PATH="${WHALEFLEET_REGISTRY_KEY:-$(dirname "${DB_PATH}")/registry.key}"
# 保留最近多少份。备份的意义在于「出事后手边有东西」，
# 留 14 份 ≈ 两周的每日备份；再多通常只是占满磁盘。
KEEP="${WHALEFLEET_BACKUP_KEEP:-14}"
# 单个备份超过该大小（字节）时删除。0 = 不限制。
MAX_BYTES="${WHALEFLEET_BACKUP_MAX_BYTES:-0}"

log() { printf '[backup] %s\n' "$*"; }
fail() { printf '[backup] 错误：%s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# 前置检查
# ---------------------------------------------------------------------------
command -v sqlite3 >/dev/null 2>&1 \
  || fail "未找到 sqlite3 命令。备份脚本必须用 .backup（在线快照），不能用 cp。"

[[ -f "${DB_PATH}" ]] || fail "数据库文件不存在：${DB_PATH}"

# ---------------------------------------------------------------------------
# 生成文件名：whalefleet-20261014-031700.db
# 时间戳到秒：同一分钟内跑两次也不会互相覆盖（虽然那种情况
# 本身就说明 cron 配错了，但覆盖掉上一份备份更糟）。
# ---------------------------------------------------------------------------
TIMESTAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP_FILE="${BACKUP_DIR}/whalefleet-${TIMESTAMP}.db"

mkdir -p "${BACKUP_DIR}"

# ---------------------------------------------------------------------------
# 在线备份
# ---------------------------------------------------------------------------
# `.timeout` 是给 .backup 的等待上限（毫秒）：数据库被长事务占住时
# 它会等，但不会无限等——挂死的备份脚本比失败的更难发现。
log "备份 ${DB_PATH} → ${BACKUP_FILE}"
if ! sqlite3 "${DB_PATH}" <<SQL
.timeout 10000
.backup '${BACKUP_FILE}'
SQL
then
  rm -f "${BACKUP_FILE}"
  fail "sqlite3 .backup 执行失败（数据库可能被长事务占用，或磁盘已满）"
fi

[[ -s "${BACKUP_FILE}" ]] || { rm -f "${BACKUP_FILE}"; fail "备份文件为空"; }

# ---------------------------------------------------------------------------
# 校验备份可用
# ---------------------------------------------------------------------------
# 这一步不是多余的：备份的意义完全取决于它**能**被打开。
# integrity_check 能查出截断与页损坏，而它只需读一遍文件。
INTEGRITY="$(sqlite3 "${BACKUP_FILE}" 'PRAGMA integrity_check;' 2>&1 || true)"
if [[ "${INTEGRITY}" != "ok" ]]; then
  # 保留坏文件供事后排查（但绝不让它进入轮转集合）。
  mv "${BACKUP_FILE}" "${BACKUP_FILE}.corrupt"
  fail "备份完整性检查未通过（${INTEGRITY}），文件已保留为 ${BACKUP_FILE}.corrupt"
fi
log "完整性检查通过"

# ---------------------------------------------------------------------------
# JWT 签名密钥
# ---------------------------------------------------------------------------
# 放在 .backup **之后**：先保证数据库快照成功，再处理附属文件。
# 密钥丢失的代价（全员登出）小于数据库丢失，但也不该让前者拖垮后者。
if [[ -f "${JWT_SECRET_PATH}" ]]; then
  cp -p "${JWT_SECRET_PATH}" "${BACKUP_FILE}.jwt.key"
  chmod 600 "${BACKUP_FILE}.jwt.key"
  log "已备份 JWT 签名密钥 ${BACKUP_FILE}.jwt.key"
else
  # 缺失只说明部署方显式配了 JWT__SECRET（密钥不在文件里），不是错误。
  log "未找到 ${JWT_SECRET_PATH}，跳过（显式配置 JWT__SECRET 时本就没有该文件）"
fi

# ---------------------------------------------------------------------------
# Registry 口令加密密钥（0.4.0）
# ---------------------------------------------------------------------------
# 与 jwt.key 同理，但丢失的后果不同：jwt.key 丢了 = 全员被登出（且有报错），
# registry.key 丢了 = 已保存的 Registry 口令无法解密（表现为「认证失败」，
# 很容易被误当成「口令写错了」而反复重填）。因此它同样必须随备份一起带走。
if [[ -f "${REGISTRY_KEY_PATH}" ]]; then
  cp -p "${REGISTRY_KEY_PATH}" "${BACKUP_FILE}.registry.key"
  chmod 600 "${BACKUP_FILE}.registry.key"
  log "已备份 Registry 加密密钥 ${BACKUP_FILE}.registry.key"
else
  # 尚未配置过任何 Registry 时本就没有该文件，不是错误。
  log "未找到 ${REGISTRY_KEY_PATH}，跳过（尚未配置 Registry 时本就没有该文件）"
fi

# ---------------------------------------------------------------------------
# 轮转清理
# ---------------------------------------------------------------------------
# 用「按文件名排序」而非 mtime：文件名含时间戳，排序结果与时间一致，
# 且不会因为 cp -p 保留 mtime 而排错序。
mapfile -t OLD < <(find "${BACKUP_DIR}" -maxdepth 1 -type f -name 'whalefleet-*.db' | sort -r)
REMOVED=0
for (( i=KEEP; i<${#OLD[@]}; i++ )); do
  rm -f "${OLD[i]}"
  # 密钥副本与数据库快照同名对应，必须一起清，否则会残留一批无主密钥。
  rm -f "${OLD[i]}.jwt.key"
  rm -f "${OLD[i]}.registry.key"
  REMOVED=$((REMOVED + 1))
done
[[ ${REMOVED} -gt 0 ]] && log "已清理 ${REMOVED} 份旧备份（保留最近 ${KEEP} 份）"

if [[ "${MAX_BYTES}" != "0" ]]; then
  SIZE="$(stat -c %s "${BACKUP_FILE}" 2>/dev/null || stat -f %z "${BACKUP_FILE}")"
  if [[ "${SIZE}" -gt "${MAX_BYTES}" ]]; then
    fail "备份体积 ${SIZE} 字节超过上限 ${MAX_BYTES}。请检查数据库是否有异常膨胀"
  fi
  log "备份体积 ${SIZE} 字节"
fi

log "完成：${BACKUP_FILE}"