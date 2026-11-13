-- ============================================================================
-- 0014_env_variables.sql —— 环境变量（全局变量 + Compose 项目级变量）
--
-- 本迭代（0.15.0）交付「.env 变量与密钥」。Compose 文件里用 ${VAR} 引用变量，
-- 变量本身由平台托管，不写进 YAML 正文。
--
-- ## 两档作用域
--   * **全局变量**：host_id IS NULL 且 compose_name IS NULL —— 对所有项目生效。
--   * **项目级变量**：host_id 与 compose_name 都非空 —— 只对该项目的 Compose 文件生效。
-- 两档的取舍由 CHECK 约束锁死：**不允许「有主机但没有项目名」的半截作用域**，
-- 因为那是一种「主机级变量」的第三档语义，本项目没有它。
--
-- ## 为什么项目级变量绑「主机 + 项目名」而不是 composes.id
-- composes.id 会随「删除项目 → 重新部署同一名字」而变，绑它等于「删一次项目就丢一遍变量」，
-- 而变量里通常存着口令——丢一次的代价很大。绑「主机 + 项目名」与 Compose 自身的 .env
-- 语义一致（docker compose down 也不会删掉 .env）。
--
-- ## 值一律加密
-- encrypted_value 存 AES-GCM 密文（复用仓库口令那把 repository.key）。
-- ⚠️ **普通变量也加密**：分类为「非密钥」只影响**展示**（是否遮罩、能否被 Webhook 覆盖），
-- 不影响**存储**。让普通变量以明文躺在库里，等于留了一条「备份文件泄露 = 全部口令泄露」的旁路。
--
-- ## 唯一性
--   * 全局变量：name 唯一（部分唯一索引 WHERE is_deleted = 0）。
--   * 项目级变量：(host_id, compose_name, name) 唯一。
-- 变量名**区分大小写**（与 Compose 一致：${TAG} 与 ${tag} 是两个变量），
-- 因此这里用的是默认的 BINARY 排序，而不是 COLLATE NOCASE。
-- ============================================================================

CREATE TABLE env_variables
(
    id                     TEXT    NOT NULL PRIMARY KEY,

    -- 所属主机（hosts.id 的小写 36 位文本）。NULL = 全局变量。
    -- ⚠️ 存 hosts.id 而不是主机名：主机名可改，变量与主机的从属关系不该因改名断掉。
    host_id                TEXT    NULL REFERENCES hosts (id),

    -- 所属 Compose 项目名。NULL = 全局变量。
    compose_name           TEXT    NULL,

    -- 变量名。字符集 [A-Za-z_][A-Za-z0-9_]*，≤ 64 字符（应用层校验）。
    -- ⚠️ **区分大小写**：与 Compose 的 ${VAR} 语义一致。
    name                   TEXT    NOT NULL,

    -- 变量值的 AES-GCM 密文。NULL = 尚未设置值（与「值为空串」是两件事）。
    encrypted_value        TEXT    NULL,

    -- 是否为密钥：影响展示（遮罩）与 Webhook 覆盖许可，**不影响存储**。
    is_secret              INTEGER NOT NULL DEFAULT 0,

    -- 是否允许被 Webhook 的查询串覆盖。
    -- ⚠️ 密钥恒为 0：Webhook 是匿名入口（凭据就是 URL 里的令牌），
    --    而 URL 会进反代的 access log——允许远程改密钥等于开了一条提权通道。
    webhook_overridable    INTEGER NOT NULL DEFAULT 0,

    -- 备注（给人看的）。
    description            TEXT    NULL,

    creation_time          TEXT    NOT NULL,
    creator_id             TEXT    NOT NULL,
    last_modification_time TEXT    NULL,
    last_modifier_id       TEXT    NOT NULL,
    is_deleted             INTEGER NOT NULL DEFAULT 0,
    deletion_time          TEXT    NULL,
    deleter_id             TEXT    NOT NULL,

    -- 两档作用域必须成对出现：要么都是 NULL（全局），要么都不是 NULL（项目级）。
    CHECK ((host_id IS NULL AND compose_name IS NULL)
        OR (host_id IS NOT NULL AND compose_name IS NOT NULL)),

    -- 密钥不可被 Webhook 覆盖——这条不变量交给数据库兜底，
    -- 免得应用层某次漏判就把「密钥可被远程改」写进了库。
    CHECK (is_secret = 0 OR webhook_overridable = 0)
);

-- 全局变量名唯一；软删除后名称可再次使用
CREATE UNIQUE INDEX ux_env_variables_global_name
    ON env_variables (name)
    WHERE is_deleted = 0 AND host_id IS NULL;

-- 同一项目的项目级变量名唯一
CREATE UNIQUE INDEX ux_env_variables_project_name
    ON env_variables (host_id, compose_name, name)
    WHERE is_deleted = 0 AND host_id IS NOT NULL;

-- 注入 Compose 时的取值查询：项目级 + 全局级，一次取齐
CREATE INDEX ix_env_variables_scope
    ON env_variables (host_id, compose_name)
    WHERE is_deleted = 0;

-- ---------------------------------------------------------------------------
-- 内置角色权限种子：variable.read / variable.write
--
-- 只种给 admin：全局变量对所有主机生效，主机级管理员不应因此能改全实例的变量。
-- 与 0006 / 0010 / 0011 同口径，时间戳固定为常量。
-- ---------------------------------------------------------------------------
INSERT INTO role_permissions (role_id, permission, creation_time, creator_id, last_modification_time, last_modifier_id)
VALUES
    ('00000000-0000-0000-0000-0000000000a1', 'variable.read',  '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000'),
    ('00000000-0000-0000-0000-0000000000a1', 'variable.write', '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000');
