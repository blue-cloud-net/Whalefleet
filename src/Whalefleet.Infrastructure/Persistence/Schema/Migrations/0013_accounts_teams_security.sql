-- ============================================================================
-- 0013_accounts_teams_security.sql —— 账号状态、团队与安全策略（0.13.0）
--
-- 本迭代交付四组能力：
--   1. 账号状态：users 增「停用 / 首次改密 / 密码变更时间」
--   2. 会话元数据：refresh_tokens 增「设备指纹 / 来源 IP / UA / 上一代哈希 / 轮换时间」
--   3. 团队：teams + team_members + team_role_assignments
--   4. 安全策略：security_settings 单行表（密码强度与有效期）
--
-- ## 会话模型：一次登录 = 一行 = 一个会话
-- refresh_tokens 的一行既是**一个会话**，也是**当前 refresh token 的记录**：
--   * id            —— 会话 id（UUID），access token 的 sid 声明指向它
--   * token_hash    —— **当前** refresh token 的哈希（轮换时原地更新）
--   * previous_token_hash —— 上一次轮换前的哈希，仅用于重放检测
--   * rotated_at    —— 最近一次轮换时间
--
-- 轮换**不插新行**：同一行更新 token_hash 与 expires_at。
-- 理由是 access token 里的 sid 必须在轮换前后指向**同一个会话**，
-- 否则「会话列表」会把一次登录显示成多条记录，「按会话吊销」也会漏掉旧令牌。
--
-- 旧列 replaced_by_token_hash 保留但不再写入（避免重建表）；新逻辑用 previous_token_hash。
--
-- ## 团队只收窄、不扩权
-- team_role_assignments 表达的是「团队在某作用域内的权限上限」，
-- 而**不是**「团队成员的授权来源」。成员的个人授权仍在 user_role_assignments，
-- 由管理员显式同步（teams/{teamId}/sync）写入。
--
-- 为什么不把团队做成授权主体：团队成员变更往往由 HR / 组长这类**非管理员**操作，
-- 若「加进团队 = 获得权限」，那会变成一条绕过 user.write 的提权路径。
--
-- ## 作用域规则与 user_role_assignments 一致
--   host_id NULL      = 全局分配
--   host_id 非空      = 该主机上的分配
--   container_id 非空 = 该主机上某个容器的分配（必须同时有 host_id）
--
-- ⚠️ 唯一索引必须用 COALESCE(host_id, '')：SQLite 的唯一索引把 NULL 视为互不相等，
--    直接用 host_id 会让「同一个全局角色」被无限次重复分配。
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. users：账号状态
-- ---------------------------------------------------------------------------

-- 是否停用。停用后该用户已签发的所有会话立即失效（撤销 + 每请求校验）。
ALTER TABLE users ADD COLUMN is_disabled INTEGER NOT NULL DEFAULT 0;

-- 是否必须先改密。0.13.0 起新建用户默认置 1（按安全设置），
-- 改密成功后清除。存量用户置 0：他们已经在用了，追溯要求改密会造成意外登出。
ALTER TABLE users ADD COLUMN must_change_password INTEGER NOT NULL DEFAULT 0;

-- 最近一次改密时间（UTC，TEXT）。用于判断密码是否过期。
-- 存量用户回填为创建时间——他们从没改过密码，用创建时间才是诚实的事实。
ALTER TABLE users ADD COLUMN password_changed_at TEXT NULL;

UPDATE users SET password_changed_at = creation_time WHERE password_changed_at IS NULL;

-- ---------------------------------------------------------------------------
-- 2. refresh_tokens：会话元数据与轮换痕迹
-- ---------------------------------------------------------------------------

-- 设备指纹。**只记录、不校验**：变化时覆盖更新，用于让人在会话列表里
-- 认出「哪一条是我自己」。它不是安全边界。
ALTER TABLE refresh_tokens ADD COLUMN device_fingerprint TEXT NULL;

-- 登录来源 IP 与 UA。登录时快照，后续不更新。
ALTER TABLE refresh_tokens ADD COLUMN created_ip TEXT NULL;
ALTER TABLE refresh_tokens ADD COLUMN user_agent TEXT NULL;

-- 上一次轮换前的 refresh token 哈希。重放检测专用：
-- 入参哈希命中它 → 说明旧令牌在轮换后再次出现 → 判定重放。
ALTER TABLE refresh_tokens ADD COLUMN previous_token_hash TEXT NULL;

-- 最近一次轮换时间（UTC）。从未轮换为 NULL。
ALTER TABLE refresh_tokens ADD COLUMN rotated_at TEXT NULL;

-- 会话列表按用户查活跃会话
CREATE INDEX ix_refresh_tokens_user_active
    ON refresh_tokens (user_id)
    WHERE revoked_at IS NULL;

-- ---------------------------------------------------------------------------
-- 3. teams：团队花名册
-- ---------------------------------------------------------------------------

CREATE TABLE teams
(
    id                     TEXT    NOT NULL PRIMARY KEY,

    -- 团队标识（管理员起的名字，如「SRE 组」）。不区分大小写且唯一。
    name                   TEXT    NOT NULL,

    -- 可读名称；可为空（为空时界面回退显示 name）。
    display_name           TEXT    NULL,

    -- 备注（可为空）。
    description            TEXT    NULL,

    creation_time          TEXT    NOT NULL,
    creator_id             TEXT    NOT NULL,
    last_modification_time TEXT    NULL,
    last_modifier_id       TEXT    NOT NULL,
    is_deleted             INTEGER NOT NULL DEFAULT 0,
    deletion_time          TEXT    NULL,
    deleter_id             TEXT    NOT NULL
);

-- 团队名不区分大小写且唯一；删除后名称可再次使用
CREATE UNIQUE INDEX ux_teams_name
    ON teams (name COLLATE NOCASE)
    WHERE is_deleted = 0;

-- ---------------------------------------------------------------------------
-- 4. team_members：团队 ↔ 用户（多对多）
--
-- 复合主键而不是自增行号：加成员天然幂等（重复加不该报错），
-- INSERT OR IGNORE 把幂等性交给数据库。
--
-- 硬删除：它只是「关系」，没有独立生命周期与审计价值（撤销成员关系就是删掉这行）。
-- 与 host_tag_assignments 同口径。
-- ---------------------------------------------------------------------------

CREATE TABLE team_members
(
    team_id       TEXT NOT NULL REFERENCES teams (id),
    user_id       TEXT NOT NULL REFERENCES users (id),
    creation_time TEXT NOT NULL,
    creator_id    TEXT NOT NULL,
    PRIMARY KEY (team_id, user_id)
);

-- 反向查询（某人在哪些团队里）需要独立索引；正向查询由主键前缀覆盖。
-- ⚠️ 这一条是**鉴权热路径**：每个请求都要查用户的团队。
CREATE INDEX ix_team_members_user_id ON team_members (user_id);

-- ---------------------------------------------------------------------------
-- 5. team_role_assignments：团队的权限上限
--
-- 与 user_role_assignments 同构：全局 / 主机 / 容器三级作用域。
-- 它表达的是**上限**而不是授权来源——成员的实际权限 = 个人权限 ∩ 团队上限。
-- ---------------------------------------------------------------------------

CREATE TABLE team_role_assignments
(
    id                     TEXT    NOT NULL PRIMARY KEY,

    team_id                TEXT    NOT NULL REFERENCES teams (id),
    role_id                TEXT    NOT NULL REFERENCES roles (id),

    -- NULL = 全局分配；非空 = 只在该主机上生效。
    host_id                TEXT    NULL REFERENCES hosts (id),

    -- 容器级分配：必须同时带 host_id（容器只在某台主机上存在）。
    container_id           TEXT    NULL
        CHECK (container_id IS NULL OR (host_id IS NOT NULL AND length(trim(container_id)) > 0)),

    creation_time          TEXT    NOT NULL,
    creator_id             TEXT    NOT NULL,
    last_modification_time TEXT    NULL,
    last_modifier_id       TEXT    NOT NULL,
    is_deleted             INTEGER NOT NULL DEFAULT 0,
    deletion_time          TEXT    NULL,
    deleter_id             TEXT    NOT NULL
);

-- ⚠️ 唯一索引必须用 COALESCE(host_id, '') 与 COALESCE(container_id, '')：
--    SQLite 的唯一索引把 NULL 视为互不相等，直接用裸列会让同一个角色被无限次重复分配。
CREATE UNIQUE INDEX ux_team_role_assignments
    ON team_role_assignments (team_id, role_id, COALESCE(host_id, ''), COALESCE(container_id, ''))
    WHERE is_deleted = 0;

-- 鉴权热路径：一次查询拿全该团队的有效分配
CREATE INDEX ix_team_role_assignments_team
    ON team_role_assignments (team_id)
    WHERE is_deleted = 0;

-- ---------------------------------------------------------------------------
-- 6. security_settings：密码策略（单行表）
--
-- 单行表而不是「配置项表」：这份设置是**实例级**的，只有一份，
-- 而权限目录那一类「编译期事实」才值得做成多行。
--
-- 与 0.12.0 的 authorization_settings 同口径：id 固定为 1 并加 CHECK。
-- ---------------------------------------------------------------------------

CREATE TABLE security_settings
(
    id                             INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),

    -- 密码最小长度。默认 10（与 0.x 固定策略一致，改配置不改变历史账号的现状）。
    min_length                     INTEGER NOT NULL DEFAULT 10 CHECK (min_length >= 1),

    -- 是否要求包含字母 / 数字或符号。
    require_letter                 INTEGER NOT NULL DEFAULT 1,
    require_digit_or_symbol        INTEGER NOT NULL DEFAULT 1,

    -- 密码有效期（天）。0 = 永不过期。
    password_expiry_days           INTEGER NOT NULL DEFAULT 0 CHECK (password_expiry_days >= 0),

    -- 新建用户首次登录是否强制改密。
    must_change_on_first_login     INTEGER NOT NULL DEFAULT 1
);

INSERT INTO security_settings (id, min_length, require_letter, require_digit_or_symbol,
                               password_expiry_days, must_change_on_first_login)
VALUES (1, 10, 1, 1, 0, 1);

-- ---------------------------------------------------------------------------
-- 7. 内置 admin 补种团队权限
--
-- ⚠️ 权限目录是编译期事实，内置角色的含义也应是可复现的——
--    因此种子写进迁移，而不是运行时种（与 0006_rbac.sql 同口径）。
--
-- 时间戳沿用 0006 的固定常量，保证「同一份迁移序列」产出的库完全一致。
-- ---------------------------------------------------------------------------

INSERT INTO role_permissions (role_id, permission, creation_time, creator_id, last_modification_time, last_modifier_id)
VALUES
    ('00000000-0000-0000-0000-0000000000a1', 'team.read',  '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000'),
    ('00000000-0000-0000-0000-0000000000a1', 'team.write', '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000');
