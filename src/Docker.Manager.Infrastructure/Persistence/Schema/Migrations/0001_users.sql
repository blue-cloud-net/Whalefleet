-- ============================================================================
-- 0001_users.sql —— 用户表
--
-- 约定（见 docs/architecture.md「实体审计与软删除」）：
--   * 列名 = 实体属性名的 snake_case 版
--   * 时间一律为 UTC，以 TEXT 存储
--   * 软删除：唯一约束必须是带 WHERE is_deleted = 0 的**部分索引**，
--     否则被软删除的用户会永久占用用户名
--   * 不建 Row DTO，实体由 Dapper 直接物化（非公开无参构造 + skipVisibility）
-- ============================================================================

CREATE TABLE users
(
    id                     TEXT    NOT NULL PRIMARY KEY,
    username               TEXT    NOT NULL,
    password_hash          TEXT    NOT NULL,
    role                   TEXT    NOT NULL,
    creation_time          TEXT    NOT NULL,
    creator_id             TEXT    NOT NULL,
    last_modification_time TEXT    NULL,
    last_modifier_id       TEXT    NOT NULL,
    is_deleted             INTEGER NOT NULL DEFAULT 0,
    deletion_time          TEXT    NULL,
    deleter_id             TEXT    NOT NULL
);

-- 部分唯一索引 + COLLATE NOCASE：登录名不区分大小写，且删除后可重新使用
CREATE UNIQUE INDEX ux_users_username
    ON users (username COLLATE NOCASE)
    WHERE is_deleted = 0;
