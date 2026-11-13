-- ============================================================================
-- 0015_container_webhooks.sql —— 容器重建 Webhook
--
-- 本迭代（0.15.0）让外部事件（CI 推完代码）触发「把某个容器重建」，
-- 而不只是重新应用 Compose 项目。
--
-- ## 为什么单独一张表，而不是给 containers 加列
-- 容器是 **Docker 侧的资源**，本平台没有自己的容器表——
-- 给「本平台的容器记录」加列就得先建一张容器表，那是另一件事。
-- 而 Webhook 令牌是**本平台的凭据**，它的生命周期与容器不同
-- （容器重建后 ID 会变，令牌却要继续有效），因此独立成表。
--
-- ## 为什么绑「主机 + 容器名」而不是容器 ID
-- 重建会让容器 ID **一定改变**。绑 ID 等于「令牌下一次就失效」，
-- 而重建 Webhook 的高频用法恰恰是「反复触发同一个容器重建」。
-- 容器名在重建前后不变，因此它是唯一稳定的标识。
-- 代价是「容器改名会让令牌失效」——界面必须明确提示这一点。
--
-- ## 唯一性
--   * (host_id, container_name) 唯一：同一台机器上同一个容器只有一个令牌。
--   * webhook_token_hash 唯一：令牌反查必须走唯一索引（这是热路径）。
-- 两者都是**部分唯一索引**（WHERE is_deleted = 0），软删除后可以重建。
-- ============================================================================

CREATE TABLE container_webhooks
(
    id                     TEXT    NOT NULL PRIMARY KEY,

    -- 所属主机（hosts.id 的小写 36 位文本）。
    -- ⚠️ 存 hosts.id 而不是主机名：主机名可改，令牌与主机的从属关系不该因改名断掉。
    host_id                TEXT    NOT NULL REFERENCES hosts (id),

    -- 目标容器名（Docker 的容器名，不含前导 /）。
    container_name         TEXT    NOT NULL,

    -- Webhook 令牌的 SHA-256 哈希（小写十六进制）。
    -- ⚠️ **只存哈希，不存明文**：令牌等价于「对这个容器重建」的权限，
    --    泄露的代价是任何人都能重放你的重建。明文只在生成那一刻返回一次。
    webhook_token_hash     TEXT    NOT NULL,

    -- Webhook 令牌提示（明文前若干位），仅供界面显示「哪一个令牌」。
    webhook_token_hint     TEXT    NOT NULL,

    -- Webhook 是否启用。吊销时哈希与提示一并清空。
    webhook_enabled        INTEGER NOT NULL DEFAULT 1,

    -- Webhook 最近一次被触发的时间（UTC 文本）。从未触发为 NULL。
    webhook_last_used_at   TEXT    NULL,

    creation_time          TEXT    NOT NULL,
    creator_id             TEXT    NOT NULL,
    last_modification_time TEXT    NULL,
    last_modifier_id       TEXT    NOT NULL,
    is_deleted             INTEGER NOT NULL DEFAULT 0,
    deletion_time          TEXT    NULL,
    deleter_id             TEXT    NOT NULL
);

-- 同一台机器上同一个容器只有一个 Webhook；软删除后可以重新登记
CREATE UNIQUE INDEX ux_container_webhooks_host_container
    ON container_webhooks (host_id, container_name)
    WHERE is_deleted = 0;

-- 令牌反查走唯一索引。**部分索引（WHERE webhook_enabled = 1）**：
-- 吊销后哈希被清空，因此这里不会有两个相同的有效哈希。
CREATE UNIQUE INDEX ux_container_webhooks_token
    ON container_webhooks (webhook_token_hash)
    WHERE is_deleted = 0 AND webhook_enabled = 1 AND webhook_token_hash <> '';

-- 界面按主机列出令牌时用
CREATE INDEX ix_container_webhooks_host
    ON container_webhooks (host_id)
    WHERE is_deleted = 0;
