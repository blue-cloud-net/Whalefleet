-- ============================================================================
-- 0004_roles.sql —— 角色表 + users 表角色列改为外键
--
-- 为什么要改：角色原本是 C# 枚举（UserRole），把"系统有哪些角色"焊死在编译期。
-- 现改为运行时数据，管理员可在系统运行中增删改角色，不需要改代码发版。
--
-- users 表用 SQLite 官方的「建新表 → 搬数据 → 删旧表 → 改名」模式重建：
-- SQLite 的 ALTER TABLE 不能改列类型/约束，而本迁移需要把
-- `role TEXT NOT NULL` 换成 `role_id TEXT NOT NULL REFERENCES roles(id)`。
--
-- 内置角色的创建时间固定为常量而非 datetime('now')：
-- 种子数据应可复现，否则每次重建库都产生不同的行，测试无法断言。
-- ============================================================================

CREATE TABLE roles
(
    id                     TEXT    NOT NULL PRIMARY KEY,
    name                   TEXT    NOT NULL,
    display_name           TEXT    NULL,
    description            TEXT    NULL,
    is_built_in            INTEGER NOT NULL DEFAULT 0,
    is_default             INTEGER NOT NULL DEFAULT 0,
    creation_time          TEXT    NOT NULL,
    creator_id             TEXT    NOT NULL,
    last_modification_time TEXT    NULL,
    last_modifier_id       TEXT    NOT NULL,
    is_deleted             INTEGER NOT NULL DEFAULT 0,
    deletion_time          TEXT    NULL,
    deleter_id             TEXT    NOT NULL
);

-- 角色标识不区分大小写且唯一；删除后同名可再次创建
CREATE UNIQUE INDEX ux_roles_name
    ON roles (name COLLATE NOCASE)
    WHERE is_deleted = 0;

-- 默认角色查询（创建用户时取）走这个索引；partial 保证全库至多命中一行
CREATE UNIQUE INDEX ux_roles_default
    ON roles (is_default)
    WHERE is_default = 1 AND is_deleted = 0;

-- ---------------------------------------------------------------------------
-- 内置角色（不可删除）。
-- 主键固定，运维脚本与文档可引用。
--
-- 「角色」只表达身份标签，**不表达资源范围**——某个用户能操作哪些主机/容器
-- 由 0.3.0 的资源权限表承担。因此这里不存在"给角色配一套权限"的隐式授权。
-- ---------------------------------------------------------------------------
INSERT INTO roles (id, name, display_name, description, is_built_in, is_default,
                   creation_time, creator_id, last_modification_time, last_modifier_id,
                   is_deleted, deletion_time, deleter_id)
VALUES
    ('00000000-0000-0000-0000-0000000000a1', 'admin', '管理员',
     '可管理用户、主机与全部资源；[Authorize(Roles = "admin")] 依赖此角色',
     1, 0, '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL,
     '00000000-0000-0000-0000-000000000000', 0, NULL, '00000000-0000-0000-0000-000000000000'),
    ('00000000-0000-0000-0000-0000000000a2', 'operator', '运维',
     '对已授权主机上的容器、镜像、数据卷与网络有操作权限',
     1, 0, '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL,
     '00000000-0000-0000-0000-000000000000', 0, NULL, '00000000-0000-0000-0000-000000000000'),
    ('00000000-0000-0000-0000-0000000000a3', 'viewer', '只读',
     '对已授权资源仅可查看，不可修改',
     1, 0, '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL,
     '00000000-0000-0000-0000-000000000000', 0, NULL, '00000000-0000-0000-0000-000000000000'),
    ('00000000-0000-0000-0000-0000000000a4', 'auditor', '审计',
     '只读，并可查看操作审计（审计能力于 0.3.0 落地）',
     1, 1, '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL,
     '00000000-0000-0000-0000-000000000000', 0, NULL, '00000000-0000-0000-0000-000000000000');

-- ---------------------------------------------------------------------------
-- users：role（枚举名文本） → role_id（指向 roles.id 的外键）
--
-- 回填用 LEFT JOIN + COALESCE：
--   * 旧列只可能是 admin / user，user 没有对应行 → 落到默认角色 auditor
--   * COALESCE 兜住任何未知取值，避免 NOT NULL 约束把迁移整个卡死
-- ---------------------------------------------------------------------------
CREATE TABLE users_new
(
    id                     TEXT    NOT NULL PRIMARY KEY,
    username               TEXT    NOT NULL,
    password_hash          TEXT    NOT NULL,
    role_id                TEXT    NOT NULL REFERENCES roles (id),
    creation_time          TEXT    NOT NULL,
    creator_id             TEXT    NOT NULL,
    last_modification_time TEXT    NULL,
    last_modifier_id       TEXT    NOT NULL,
    is_deleted             INTEGER NOT NULL DEFAULT 0,
    deletion_time          TEXT    NULL,
    deleter_id             TEXT    NOT NULL
);

INSERT INTO users_new (id, username, password_hash, role_id,
                       creation_time, creator_id, last_modification_time, last_modifier_id,
                       is_deleted, deletion_time, deleter_id)
SELECT u.id,
       u.username,
       u.password_hash,
       COALESCE(
           (SELECT r.id FROM roles r WHERE r.name = LOWER(u.role) LIMIT 1),
           (SELECT r.id FROM roles r WHERE r.is_default = 1 AND r.is_deleted = 0 LIMIT 1)),
       u.creation_time,
       u.creator_id,
       u.last_modification_time,
       u.last_modifier_id,
       u.is_deleted,
       u.deletion_time,
       u.deleter_id
FROM users u;

DROP TABLE users;

ALTER TABLE users_new RENAME TO users;

-- 重建表的索引要一起补回来（DROP TABLE users 会连带删掉旧索引）
CREATE UNIQUE INDEX ux_users_username
    ON users (username COLLATE NOCASE)
    WHERE is_deleted = 0;

CREATE INDEX ix_users_role_id
    ON users (role_id);