-- ============================================================================
-- 0011_alerts.sql —— 告警规则与通知中心（0.11.0）
--
-- 本迭代交付：
--   1. 可配置的三级阈值告警规则（主机离线 / 容器 CPU / 内存 / 网络 / 登录失败激增）
--   2. 通知渠道（Webhook / 邮件）
--   3. 告警触发事实（冷却判定）
--   4. 站内通知与每用户已读 / 删除状态
--
-- ## 为什么告警事件与通知分两张表
-- 告警事件是**规则引擎自己的事实**：回答「这条规则最近有没有报过」，用于冷却判定，
-- 只增不删。通知是**用户可见的投递**：可能因为渠道发送失败、用户删除而变化。
-- 两者生命周期不同，合成一张表会让「冷却」被用户的删除动作影响。
--
-- ## 为什么已读状态单独一张表
-- 一条通知对多个用户可见，而「我读过了」是个人状态。做成全局已读会让同事点开铃铛
-- 把所有人的红点都清掉。
--
-- ## 为什么通知只存 i18n key + 参数
-- 同一件事要出现在 toast、铃铛下拉、历史列表三处，文案必须能翻译。
-- 存成品中文会让英文界面永远显示中文。
--
-- ## 主机 / 容器名是快照
-- 它们随后可能被删除或改名，而通知历史仍要能读懂。与 audit_logs 同口径。
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 告警规则
-- ---------------------------------------------------------------------------
CREATE TABLE alert_rules
(
    id                     TEXT    NOT NULL PRIMARY KEY,

    -- 作用域主机（hosts.id）；NULL = 全局（所有主机）
    host_id                TEXT    NULL REFERENCES hosts (id),

    -- 规则类型：host-offline / container-cpu / container-memory /
    --           container-network / login-failure-surge
    kind                   TEXT    NOT NULL,

    enabled                INTEGER NOT NULL DEFAULT 0,

    -- 三级阈值。主机离线规则三者为 NULL（严重度由 severity 指定）；
    -- 登录失败激增的三者是「次数」，其余是百分比 / 字节每秒。
    info_threshold         REAL    NULL,
    warning_threshold      REAL    NULL,
    critical_threshold     REAL    NULL,

    -- 事件型规则的固定严重度（info / warning / critical）
    severity               TEXT    NOT NULL DEFAULT 'warning',

    -- 连续命中多少次才触发（压掉瞬时毛刺）
    consecutive_samples    INTEGER NOT NULL DEFAULT 3,

    -- 统计窗口（秒），仅登录失败激增使用
    window_seconds         INTEGER NULL,

    -- 冷却期（秒）：同一规则在同一目标上触发后，窗口内不重复
    cooldown_seconds       INTEGER NOT NULL DEFAULT 300,

    creation_time          TEXT    NOT NULL,
    creator_id             TEXT    NOT NULL,
    last_modification_time TEXT    NULL,
    last_modifier_id       TEXT    NOT NULL,
    is_deleted             INTEGER NOT NULL DEFAULT 0,
    deletion_time          TEXT    NULL,
    deleter_id             TEXT    NOT NULL
);

-- 同一作用域内同一种规则只能有一条。
-- ⚠️ 用 COALESCE(host_id, '') 而不是 (host_id, kind)：SQLite 的唯一索引把 NULL 视为互不相等，
-- 直接用 host_id 会让「多条全局 CPU 规则」全部通过。
CREATE UNIQUE INDEX ux_alert_rules_scope_kind
    ON alert_rules (COALESCE(host_id, ''), kind)
    WHERE is_deleted = 0;

CREATE INDEX ix_alert_rules_host
    ON alert_rules (host_id)
    WHERE is_deleted = 0;

-- ---------------------------------------------------------------------------
-- 通知渠道
-- ---------------------------------------------------------------------------
CREATE TABLE notification_channels
(
    id                     TEXT    NOT NULL PRIMARY KEY,

    name                   TEXT    NOT NULL,
    -- webhook / email
    kind                   TEXT    NOT NULL,
    enabled                INTEGER NOT NULL DEFAULT 1,

    -- 配置 JSON。**敏感字段已加密**（Webhook 鉴权令牌 / SMTP 密码），
    -- 与仓库口令同一把密钥（repository.key），随数据库一起备份。
    config_json            TEXT    NOT NULL,

    creation_time          TEXT    NOT NULL,
    creator_id             TEXT    NOT NULL,
    last_modification_time TEXT    NULL,
    last_modifier_id       TEXT    NOT NULL,
    is_deleted             INTEGER NOT NULL DEFAULT 0,
    deletion_time          TEXT    NULL,
    deleter_id             TEXT    NOT NULL
);

-- 渠道名唯一（不区分大小写，软删除后名称可再使用）
CREATE UNIQUE INDEX ux_notification_channels_name
    ON notification_channels (name COLLATE NOCASE)
    WHERE is_deleted = 0;

-- ---------------------------------------------------------------------------
-- 规则 → 渠道 关联（多对多）
-- ---------------------------------------------------------------------------
CREATE TABLE alert_rule_channels
(
    rule_id       TEXT NOT NULL REFERENCES alert_rules (id),
    channel_id    TEXT NOT NULL REFERENCES notification_channels (id),
    creation_time TEXT NOT NULL,
    creator_id    TEXT NOT NULL,
    PRIMARY KEY (rule_id, channel_id)
);

CREATE INDEX ix_alert_rule_channels_channel
    ON alert_rule_channels (channel_id);

-- ---------------------------------------------------------------------------
-- 告警触发事实（只增 + 按保留期裁剪）
-- ---------------------------------------------------------------------------
CREATE TABLE alert_events
(
    id               TEXT NOT NULL PRIMARY KEY,

    rule_id          TEXT NOT NULL,
    -- 规则类型快照：规则被删后仍能解释这条事件
    rule_kind        TEXT NOT NULL,

    -- 冷却作用域：host:名称 / host:名称:container:ID / global
    target_key       TEXT NOT NULL,

    host_id          TEXT NULL,
    host_name        TEXT NULL,
    container_id     TEXT NULL,
    container_name   TEXT NULL,

    severity         TEXT NOT NULL,

    -- 触发时的实际值与命中的阈值；事件型规则为 NULL
    metric_value     REAL NULL,
    threshold_value  REAL NULL,

    occurred_at      TEXT NOT NULL
);

CREATE INDEX ix_alert_events_rule_target
    ON alert_events (rule_id, target_key, occurred_at DESC);

CREATE INDEX ix_alert_events_occurred
    ON alert_events (occurred_at DESC);

-- ---------------------------------------------------------------------------
-- 站内通知
-- ---------------------------------------------------------------------------
CREATE TABLE notifications
(
    id           TEXT NOT NULL PRIMARY KEY,

    -- alert-fired / container-died / stack-task-failed
    source_kind  TEXT NOT NULL,
    severity     TEXT NOT NULL,

    -- i18n key + 参数（JSON 对象）
    message_key  TEXT NOT NULL,
    params_json  TEXT NOT NULL,

    host_id      TEXT NULL,
    host_name    TEXT NULL,

    -- 去重键：同一次事件的多个通道只落一条
    dedupe_key   TEXT NULL,

    occurred_at  TEXT NOT NULL
);

CREATE INDEX ix_notifications_occurred
    ON notifications (occurred_at DESC);

CREATE INDEX ix_notifications_host_occurred
    ON notifications (host_id, occurred_at DESC);

CREATE UNIQUE INDEX ux_notifications_dedupe
    ON notifications (dedupe_key)
    WHERE dedupe_key IS NOT NULL;

-- ---------------------------------------------------------------------------
-- 每用户通知状态（已读 / 已删除）
-- ---------------------------------------------------------------------------
CREATE TABLE notification_user_states
(
    notification_id TEXT NOT NULL REFERENCES notifications (id) ON DELETE CASCADE,
    user_id         TEXT NOT NULL REFERENCES users (id),
    read_at         TEXT NULL,
    dismissed_at    TEXT NULL,
    PRIMARY KEY (notification_id, user_id)
);

CREATE INDEX ix_notification_user_states_user
    ON notification_user_states (user_id, dismissed_at);

-- ---------------------------------------------------------------------------
-- 内置角色权限种子：alert.read / alert.write
--
-- 只种给 admin：告警是全局配置，主机级管理员不应因此获得修改所有主机阈值的能力。
-- 与 0006 / 0010 同口径，时间戳固定为常量。
-- ---------------------------------------------------------------------------
INSERT INTO role_permissions (role_id, permission, creation_time, creator_id, last_modification_time, last_modifier_id)
VALUES
    ('00000000-0000-0000-0000-0000000000a1', 'alert.read',  '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000'),
    ('00000000-0000-0000-0000-0000000000a1', 'alert.write', '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000');

-- ---------------------------------------------------------------------------
-- 默认规则种子
--
-- 主机离线默认启用（它没有额外成本，且是最该主动发现的事）。
-- 其余四条默认关闭：容器指标类规则启用后会常驻订阅容器 stats，
-- 让用户明确选择要监控哪些主机，而不是一上来就把所有容器都订上。
-- ---------------------------------------------------------------------------
INSERT INTO alert_rules
    (id, host_id, kind, enabled,
     info_threshold, warning_threshold, critical_threshold,
     severity, consecutive_samples, window_seconds, cooldown_seconds,
     creation_time, creator_id, last_modification_time, last_modifier_id,
     is_deleted, deletion_time, deleter_id)
VALUES
    ('00000000-0000-0000-0000-000000000a11', NULL, 'HostOffline', 1,
     NULL, NULL, NULL,
     'critical', 2, NULL, 300,
     '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000',
     0, NULL, '00000000-0000-0000-0000-000000000000'),

    ('00000000-0000-0000-0000-000000000a12', NULL, 'ContainerCpu', 0,
     70, 85, 95,
     'warning', 3, NULL, 300,
     '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000',
     0, NULL, '00000000-0000-0000-0000-000000000000'),

    ('00000000-0000-0000-0000-000000000a13', NULL, 'ContainerMemory', 0,
     70, 85, 95,
     'warning', 3, NULL, 300,
     '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000',
     0, NULL, '00000000-0000-0000-0000-000000000000'),

    ('00000000-0000-0000-0000-000000000a14', NULL, 'ContainerNetwork', 0,
     1048576, 10485760, 52428800,
     'warning', 3, NULL, 300,
     '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000',
     0, NULL, '00000000-0000-0000-0000-000000000000'),

    ('00000000-0000-0000-0000-000000000a15', NULL, 'LoginFailureSurge', 0,
     5, 10, 20,
     'warning', 1, 300, 300,
     '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000',
     0, NULL, '00000000-0000-0000-0000-000000000000');
