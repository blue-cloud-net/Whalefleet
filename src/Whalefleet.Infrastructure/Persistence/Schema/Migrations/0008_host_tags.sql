-- ============================================================================
-- 0008_host_tags.sql —— 主机标签（Tag）
--
-- 本迭代（0.6.0）把主机组织能力补齐：分组是**单一归属**，标签是**多对多自由打标**。
-- 两者的职责在 docs/roadmap.md 里写死，避免「两套组织方式打架」：
--   * Group = 一个主机只属于一个分组，是选择 / 过滤 / 批量操作的边界；
--   * Tag   = 一个主机可以挂多个标签，一个标签也可以挂多台主机，只用于过滤与批量选择。
-- 两者**都不参与授权**：授权边界只有一条，就是分组与角色分配。若让标签参与授权，
-- 「给主机打个标签」就会变成一次隐式提权，而界面上完全看不出来。
--
-- ## 为什么标签也要建表
-- 与 host_groups 同因：标签是运行时可变的数据（管理员随时增删改），枚举会把
-- 「有哪些标签」焊死在编译期。名字是**数据而非翻译键**，前端原样展示用户输入。
--
-- ## 唯一性
-- 标签名不区分大小写且唯一（部分唯一索引 WHERE is_deleted = 0）：
-- 同名标签会让过滤下拉出现两条分不清的选项。软删除后名称可再次使用。
--
-- ## 关联表为什么是硬删除
-- host_tag_assignments 只是「关系」，没有独立生命周期与审计价值：
-- 撤销打标就是把这行删掉。给它加 is_deleted 会让每一条读路径都要过滤，
-- 而收益为零（历史关系没有任何地方要查）。
--
-- ## 外键
-- 两个外键都指向真实存在的表，且**不级联删除**：删除主机与删除标签都由应用服务
-- 显式处理（删标签时先清关联），级联删除会让「删标签」悄悄改掉主机侧的数据，
-- 而这里的删除本来就应该只有一处发生。
-- ============================================================================

CREATE TABLE host_tags
(
    id                     TEXT    NOT NULL PRIMARY KEY,

    -- 标签名：管理员可读的名字（如「生产」「GPU」）。不区分大小写且唯一，最长 32。
    name                   TEXT    NOT NULL,

    -- 备注（可为空）。
    description            TEXT    NULL,

    -- 展示颜色（#RRGGBB 文本）。NULL = 用界面默认色。
    -- ⚠️ 只存规范化的 6 位十六进制：存 #RGB、rgb()、颜色名会让前端与后端各写一套解析。
    color                  TEXT    NULL,

    creation_time          TEXT    NOT NULL,
    creator_id             TEXT    NOT NULL,
    last_modification_time TEXT    NULL,
    last_modifier_id       TEXT    NOT NULL,
    is_deleted             INTEGER NOT NULL DEFAULT 0,
    deletion_time          TEXT    NULL,
    deleter_id             TEXT    NOT NULL
);

-- 标签名不区分大小写且唯一；删除后名称可再次使用
CREATE UNIQUE INDEX ux_host_tags_name
    ON host_tags (name COLLATE NOCASE)
    WHERE is_deleted = 0;

-- ---------------------------------------------------------------------------
-- 主机 ↔ 标签（多对多）
--
-- 复合主键而不是自增行号：
--   * 打标/清标天然是幂等的（重复打同一个标签不该报错），复合主键让
--     INSERT OR IGNORE 直接把幂等性交给数据库；
--   * 「某台主机有哪些标签」「某个标签挂了哪些主机」两个方向都要查，
--     因此除主键（host_id, tag_id）外再给 tag_id 单独建索引。
--
-- host_id 存的是 hosts.id（Guid 的小写 36 位文本），**不是主机名**：
-- 主机名可以改，而标签关系不该因为改名断掉。
-- ---------------------------------------------------------------------------
CREATE TABLE host_tag_assignments
(
    host_id       TEXT NOT NULL REFERENCES hosts (id),
    tag_id        TEXT NOT NULL REFERENCES host_tags (id),
    creation_time TEXT NOT NULL,
    creator_id    TEXT NOT NULL,
    PRIMARY KEY (host_id, tag_id)
);

-- 反向查询（按标签找主机）需要独立索引；正向查询由主键前缀覆盖
CREATE INDEX ix_host_tag_assignments_tag_id ON host_tag_assignments (tag_id);
