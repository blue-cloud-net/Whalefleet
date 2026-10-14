-- ============================================================================
-- 0005_host_groups.sql —— 主机分组
--
-- 分组是**展示性**概念（总览页按组聚合），**不是**权限边界：
-- 「谁能操作哪台主机」由 0.3.0 的资源权限表承担（见 docs/architecture.md）。
-- 让分组承担授权会使得「把主机挪进某个分组」变成一次隐式提权。
--
-- hosts.group_id 可空，NULL = 未分组。这里用 ALTER TABLE ADD COLUMN 而不是重建表：
-- hosts 本体的列一个都不变，重建只会连带重建索引与约束，风险与收益不成比例。
-- 注意 ALTER 无法给已有表加 NOT NULL 约束，因此「必须属于某组」这种规则本就不存在——
-- 未分组是合法且常见的初始状态。
--
-- 种入三个内置分组（固定主键，便于运维脚本引用）：production / staging / local。
-- 名字用英文标识而非中文展示名：库里的名字是**管理员可改的数据**，
-- 前端的展示名对这三个已知名字取 i18n 文案，自定义分组则原样显示用户输入。
-- 把中文写进种子会让英文界面下出现中文分组名，而把 i18n key 写进库里则更糟——
-- 那等于把「数据」当成「翻译键」用，管理员一改名文案就变成 key 路径。
-- ============================================================================

CREATE TABLE host_groups
(
    id                     TEXT    NOT NULL PRIMARY KEY,
    name                   TEXT    NOT NULL,
    description            TEXT    NULL,
    sort_order             INTEGER NOT NULL DEFAULT 0,
    creation_time          TEXT    NOT NULL,
    creator_id             TEXT    NOT NULL,
    last_modification_time TEXT    NULL,
    last_modifier_id       TEXT    NOT NULL,
    is_deleted             INTEGER NOT NULL DEFAULT 0,
    deletion_time          TEXT    NULL,
    deleter_id             TEXT    NOT NULL
);

-- 分组名不区分大小写且唯一；删除后名称可再次使用（与 hosts / roles 同一套做法）
CREATE UNIQUE INDEX ux_host_groups_name
    ON host_groups (name COLLATE NOCASE)
    WHERE is_deleted = 0;

ALTER TABLE hosts ADD COLUMN group_id TEXT NULL REFERENCES host_groups(id);

CREATE INDEX ix_hosts_group_id ON hosts (group_id);

-- 种入内置分组。审计人取默认值哨兵（启动引导没有登录用户），与默认主机种子一致。
INSERT INTO host_groups
    (id, name, description, sort_order, creation_time, creator_id, last_modifier_id, deleter_id)
VALUES
    ('00000000-0000-0000-0000-0000000000a1', 'production', NULL, 10,
     '2026-01-01T00:00:00+00:00', '00000000-0000-0000-0000-000000000000',
     '00000000-0000-0000-0000-000000000000', '00000000-0000-0000-0000-000000000000'),
    ('00000000-0000-0000-0000-0000000000a2', 'staging', NULL, 20,
     '2026-01-01T00:00:00+00:00', '00000000-0000-0000-0000-000000000000',
     '00000000-0000-0000-0000-000000000000', '00000000-0000-0000-0000-000000000000'),
    ('00000000-0000-0000-0000-0000000000a3', 'local', NULL, 30,
     '2026-01-01T00:00:00+00:00', '00000000-0000-0000-0000-000000000000',
     '00000000-0000-0000-0000-000000000000', '00000000-0000-0000-0000-000000000000');

-- 注意：默认主机（name = 'local'）的分组归属**不在这里**设置。
-- 迁移先于种子执行，此刻 hosts 表还是空的，这里写 UPDATE 是句空话（踩过一次）。
-- 归属由 DatabaseInitializer.SeedDefaultHostAsync 在种入主机时一并写入。
