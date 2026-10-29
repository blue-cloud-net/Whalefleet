-- ============================================================================
-- 0009_audit_logs.sql —— 操作审计日志（0.9.0）
--
-- 回答一个问题：「谁在什么时候对什么做了什么、结果如何」。
-- 这是从「能用」到「敢用」的分界——远程 shell 与后续的 AI 写操作都直接改生产环境，
-- 没有留痕等于没有问责路径。
--
-- ## 为什么写库而不是只写 Serilog
-- 日志会被轮转、被采集器采样、被集中式系统按保留期删掉；而审计要回答的是
-- 「三个月前是谁删了这台主机」。另外日志里的资源名是**当时**的文本，
-- 而资源删除之后，任何「按 ID 现查名字」的方案都只会得到空白。
--
-- ## 为什么名字是快照列（actor_name / target_name / host_name）
-- 用户会被删除、容器会被删除、主机名可以被改。若只存 ID，删除之后就再也解释不出
-- 「那条记录说的是哪个容器」。因此写入那一刻把名字固化下来，**之后不再跟随资源变化**。
--
-- ## 为什么没有 is_deleted
-- 记录一旦写出就是事实。审计表不提供任何删除入口，也没有软删除——
-- 一个能被删的审计日志等于没有审计。
--
-- ## 为什么没有外键
-- actor / target 都会消失。加了外键之后，要么删除资源时连带删掉它的审计（不可接受），
-- 要么写入时因为目标已删除而失败（审计丢一条，更不可接受）。
--
-- ## 容量
-- 本表**不自动清理**（删除历史审计与「不可篡改」直接冲突）。
-- 长期运行需要运维自行归档（导出 CSV 后按时间范围清理是后续迭代的事）。
-- 登录失败是全量记录的，其写入速率由既有的登录限流兜住（同一 IP + 用户名 5 次/分钟）。
-- ============================================================================

CREATE TABLE audit_logs
(
    id          TEXT    NOT NULL PRIMARY KEY,

    -- 类别：authentication / activity（枚举的小写文本形态）
    category    TEXT    NOT NULL,

    -- 动作标识：域.对象.动作，如 container.start / auth.login（字符串常量，见 AuditActions）
    action      TEXT    NOT NULL,

    -- 操作者。actor_id 可为空（登录失败时连用户是否存在都不知道）
    actor_id    TEXT    NULL,
    -- 操作者名快照：登录失败时记录**尝试的用户名**
    actor_name  TEXT    NOT NULL,

    -- 目标。三者都可为空（如「登录」没有目标资源）
    target_type TEXT    NULL,
    target_id   TEXT    NULL,
    -- 目标名快照：容器 / 镜像 / 角色被删除后，这条记录仍要能读懂
    target_name TEXT    NULL,

    -- 作用域主机（hosts.id 的小写 36 位文本）；全局操作为 NULL
    host_id     TEXT    NULL,
    -- 主机名快照：主机名可以被改，审计保留当时的叫法
    host_name   TEXT    NULL,

    -- 结果：success / denied / failure
    outcome     TEXT    NOT NULL,

    -- 失败时的业务错误码（1401 / 1502 …）；成功为 NULL
    error_code  INTEGER NULL,

    -- 来源 IP（经反向代理头修正后的客户端地址）
    client_ip   TEXT    NULL,

    -- 附加说明（会话 ID / 耗时 / 退出码 / 变更字段…）
    -- ⚠️ 绝不写入密码、令牌、环境变量值
    detail      TEXT    NULL,

    -- 发生时刻（UTC，yyyy-MM-dd HH:mm:ss.fff）
    occurred_at TEXT    NOT NULL
);

-- 两个页面都按时间倒序翻页，且默认只看最近 7 天
CREATE INDEX ix_audit_logs_occurred
    ON audit_logs (occurred_at DESC);

-- 认证日志 / 活动日志各查各的：类别 + 时间是最常用的组合
CREATE INDEX ix_audit_logs_category_occurred
    ON audit_logs (category, occurred_at DESC);

-- 「这个人都做过什么」
CREATE INDEX ix_audit_logs_actor
    ON audit_logs (actor_id);

-- 主机侧「事件」页：某台主机上发生了什么
CREATE INDEX ix_audit_logs_host_occurred
    ON audit_logs (host_id, occurred_at DESC);
