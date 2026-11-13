-- ============================================================================
-- 0016_image_update_policies.sql —— 镜像自动更新策略
--
-- 交付「镜像自动更新」：按「主机 + 镜像引用（仓库:标签）」跟踪仓库摘要变化，
-- 变化时按策略决定「只通知」还是「拉取并重建容器」。
--
-- ## 为什么绑标签而不是绑镜像 ID
-- 要跟踪的正是「这个标签背后的摘要变了」。绑 ID 等于绑一个不可变的快照，
-- 永远查不出更新。
--
-- ## 为什么基线摘要与远端摘要分两列
--   * remote_digest —— 仓库现在是什么（每次探测都刷新）。
--   * baseline_digest —— 我们上次确认过的那个（只在「建立基线」与「应用成功」时
--     前进）。
-- 两者相等才算「已是最新」。只存远端摘要的话，每次扫描都会重新报一次更新，
-- 因为没有任何东西记住「这一版已经看过了」。
--
-- ## 为什么 last_notified_digest 单独一列
-- 扫描按分钟级周期跑，同一版更新会被反复命中。去重必须靠「这一版已经通知过」这个
-- 事实，而不是靠状态——否则铃铛里会堆满同一条消息。
--
-- ## 时间列
-- 与 audit_logs 同口径用固定格式文本（yyyy-MM-dd HH:mm:ss.fff），
-- 见 AGENTS.md 陷阱 69：不定长格式会让字典序 ≠ 时间序。
-- ============================================================================

CREATE TABLE image_update_policies
(
    id                    TEXT    NOT NULL PRIMARY KEY,

    -- 所属主机（hosts.id 的小写 36 位文本）。
    -- ⚠️ 存 hosts.id 而不是主机名：主机名可改，策略与主机的从属关系不该因改名断掉。
    host_id               TEXT    NOT NULL REFERENCES hosts (id),

    -- 镜像引用（仓库:标签，标签缺省为 latest）。
    -- ⚠️ 标签**大小写敏感**（v1.0 与 V1.0 是两个标签），因此唯一索引用默认的
    --    BINARY 排序，不加 COLLATE NOCASE。
    image_reference       TEXT    NOT NULL,

    -- 执行模式：notify（只通知）/ auto（拉取并重建）。
    mode                  TEXT    NOT NULL DEFAULT 'notify',

    -- 是否启用。停用后不参与扫描，但保留历史结论（用户可能只是暂时不想被提醒）。
    is_enabled            INTEGER NOT NULL DEFAULT 1,

    -- 基线摘要（上次确认过的远端摘要）。NULL = 尚未建立基线。
    baseline_digest       TEXT    NULL,

    -- 最近一次探测到的远端摘要。NULL = 尚未探测成功。
    remote_digest         TEXT    NULL,

    -- 最近一次检查结论：unknown / up-to-date / update-available / applied / failed。
    -- ⚠️ unknown（还没查过）与 up-to-date（查过、确实最新）**必须分开**：
    --    合并会让刚加上的策略立刻宣称自己是最新的。
    last_state            TEXT    NOT NULL DEFAULT 'unknown',

    -- 结论说明（失败原因 / 已应用的摘要）。
    last_message          TEXT    NULL,

    -- 最近一次检查时间（UTC，固定格式文本）。
    last_checked_at       TEXT    NULL,

    -- 最近一次自动应用成功的时间（UTC，固定格式文本）。
    last_applied_at       TEXT    NULL,

    -- 最近一次「已经为此摘要发过通知」的摘要。
    last_notified_digest  TEXT    NULL,

    creation_time         TEXT    NOT NULL,
    creator_id            TEXT    NOT NULL,
    last_modification_time TEXT   NULL,
    last_modifier_id      TEXT    NOT NULL,
    is_deleted            INTEGER NOT NULL DEFAULT 0,
    deletion_time         TEXT    NULL,
    deleter_id            TEXT    NOT NULL
);

-- 同一主机上的同一镜像引用只有一条策略；软删除后可重新创建
CREATE UNIQUE INDEX ux_image_update_policies_host_reference
    ON image_update_policies (host_id, image_reference)
    WHERE is_deleted = 0;

-- 后台扫描的取数路径：只要已启用的
CREATE INDEX ix_image_update_policies_enabled
    ON image_update_policies (host_id)
    WHERE is_deleted = 0 AND is_enabled = 1;
