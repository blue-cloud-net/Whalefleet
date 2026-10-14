-- ============================================================================
-- 0003_refresh_tokens.sql —— 刷新令牌表
--
-- 只存令牌原文的 SHA-256 哈希（token_hash），原文永不落库。
-- 撤销即黑名单：revoked_at 非空表示已撤销，无需额外的黑名单表。
-- 过期记录由维护任务物理删除，因此本表不带软删除列。
--
-- 刻意不建 user_id → users(id) 外键：用户是软删除的（行始终存在），
-- 外键带不来额外保护，却会让"物理清理已删除用户"的维护脚本变复杂。
-- ============================================================================

CREATE TABLE refresh_tokens
(
    id                     TEXT NOT NULL PRIMARY KEY,
    user_id                TEXT NOT NULL,
    token_hash             TEXT NOT NULL,
    expires_at             TEXT NOT NULL,
    revoked_at             TEXT NULL,
    replaced_by_token_hash TEXT NULL,
    creation_time          TEXT NOT NULL,
    creator_id             TEXT NOT NULL
);

CREATE UNIQUE INDEX ux_refresh_tokens_token_hash
    ON refresh_tokens (token_hash);

-- 「登出所有设备」与过期清理都按这两列筛选
CREATE INDEX ix_refresh_tokens_user_id
    ON refresh_tokens (user_id);

CREATE INDEX ix_refresh_tokens_expires_at
    ON refresh_tokens (expires_at);
