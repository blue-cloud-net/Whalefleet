-- ============================================================================
-- 0010_stacks.sql —— 编排 Stack（Docker Compose 项目）
--
-- 本迭代（0.10.0）交付 Compose / Stack 基础功能。**本表存的不是「容器的当前状态」**，
-- 而是两件守护进程管不了的事实：
--   1. **原始 YAML 文本**——更新与 webhook 重放都要靠它重新部署；
--   2. **「这个项目是本平台部署的」**——managed 的判据。
-- 容器状态的事实源永远是守护进程（靠 com.docker.compose.project 标签发现）。
--
-- ## 为什么建表而不是只靠标签
-- 标签能告诉你「有哪些 compose 项目」，但回答不了两个问题：
--   * 「用户当初贴的是哪份 YAML？」（更新 / 重放需要原文）
--   * 「这个项目是我们部署的，还是用户在别的机器上 `docker compose up` 的？」
--     后者决定能不能「更新」——外部项目我们没有它的 YAML，只能停 / 删。
-- 因此标签（发现）与本表（记忆）**并存**，列表接口把两边合并呈现。
--
-- ## 名称即 Compose 项目名
-- name 会作为 com.docker.compose.project 标签值落到容器 / 网络 / 卷上，
-- 因此必须符合 Compose 的项目名规则（小写字母、数字、-、_，字母或数字开头）。
-- 这条规则由应用层（Core/Stacks/Stack.cs）校验，本表只负责存。
--
-- ## 唯一性
-- (host_id, name) 组合唯一（部分唯一索引 WHERE is_deleted = 0）：
-- 同一台机器上不能有两个同名 Stack，但不同机器上可以各有一个同名项目。
-- 软删除后名称可再次使用。
--
-- ## host_id 存的是 hosts.id（Guid 的小写 36 位文本），**不是主机名**
-- 主机名可以改，而编排项目与主机的从属关系不该因改名断掉。与 host_tag_assignments 同口径。
-- ============================================================================

CREATE TABLE stacks
(
    id                     TEXT    NOT NULL PRIMARY KEY,

    -- 所属主机（hosts.id）。
    host_id                TEXT    NOT NULL REFERENCES hosts (id),

    -- Stack 名称 = Compose 项目名。会作为 com.docker.compose.project 标签值。
    name                   TEXT    NOT NULL,

    -- Compose 文件原文（YAML）。更新与 webhook 重放都以它为输入。
    -- ⚠️ 存原文而不存解析后的结构：解析规则会演进，存原文才能在规则变化后
    --    用新规则重新解释同一份文件；反过来只存结构，规则一改就永远回不去了。
    compose_content        TEXT    NOT NULL,

    -- Webhook 令牌的 SHA-256 哈希（小写十六进制）。NULL = 未启用。
    -- ⚠️ **只存哈希，不存明文**：令牌等价于「对这台主机重放部署」的权限，
    --    泄露的代价是任何人都能触发你的编排。明文只在生成那一刻返回一次。
    webhook_token_hash     TEXT    NULL,

    -- Webhook 令牌提示（明文前若干位），仅供界面显示「哪一个令牌」。
    webhook_token_hint     TEXT    NULL,

    -- Webhook 是否启用。吊销时哈希与提示一并清空。
    webhook_enabled        INTEGER NOT NULL DEFAULT 0,

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

-- 同一台机器上不能有两个同名 Stack；软删除后名称可再次使用
CREATE UNIQUE INDEX ux_stacks_host_name
    ON stacks (host_id, name)
    WHERE is_deleted = 0;

-- Webhook 令牌哈希唯一。**部分索引（WHERE webhook_enabled = 1）**：
-- 吊销后哈希被清空，因此这里实际上不会有两个相同的有效哈希；
-- 但保留唯一索引是为了「轮换时误用旧哈希」这类情况能被数据库挡住，而不是靠代码自觉。
CREATE UNIQUE INDEX ux_stacks_webhook_token
    ON stacks (webhook_token_hash)
    WHERE is_deleted = 0 AND webhook_enabled = 1 AND webhook_token_hash IS NOT NULL;

-- 按主机列 Stack（列表接口的主查询路径）
CREATE INDEX ix_stacks_host_id
    ON stacks (host_id)
    WHERE is_deleted = 0;

-- ---------------------------------------------------------------------------
-- 内置角色的权限种子：stack.read / stack.write
--
-- ⚠️ **必须种进迁移**（而不是运行时种）：权限目录是编译期事实，内置角色的含义
-- 也应是可复现的。若放进 DatabaseInitializer，每次重建库都会依赖「当时目录里有什么」，
-- 测试无法断言、运维脚本也无法假设。与 0006 同口径。
--
-- 为什么 stack.write 要单列而不是复用 container.write：
-- 部署一个 Stack 会创建网络、卷与一组容器、还会拉镜像，影响面远大于「启停一个容器」。
-- 绑到 container.write 上会让「能启停容器的人顺手就能部署整套编排」。
--
-- 时间戳固定为常量，与 0004 / 0006 同一口径。
-- ---------------------------------------------------------------------------
INSERT INTO role_permissions (role_id, permission, creation_time, creator_id, last_modification_time, last_modifier_id)
VALUES
    -- admin = 全部权限（显式行）。代码里仍按角色名短路，新权限不会静默漏给管理员。
    ('00000000-0000-0000-0000-0000000000a1', 'stack.read',  '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000'),
    ('00000000-0000-0000-0000-0000000000a1', 'stack.write', '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000'),

    -- operator = 能看 + 能操作该主机的资源（含编排）
    ('00000000-0000-0000-0000-0000000000a2', 'stack.read',  '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000'),
    ('00000000-0000-0000-0000-0000000000a2', 'stack.write', '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000'),

    -- viewer = 只读（能看编排，不能部署 / 更新 / 停止 / 删除）
    ('00000000-0000-0000-0000-0000000000a3', 'stack.read',  '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000'),

    -- auditor = 只读 + 审计（默认角色）
    ('00000000-0000-0000-0000-0000000000a4', 'stack.read',  '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000');
