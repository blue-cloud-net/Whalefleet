-- ============================================================================
-- 0002_hosts.sql —— Docker 主机表
--
-- 认证信息以两列存储：
--   auth_kind        'none' | 'tls' | 'basic'（小写文本，与枚举一一对应）
--   auth_config_json 认证载荷（不透明 JSON）；none 时为 NULL
--
-- 之所以不用单列 JSON：方式（kind）需要能被独立读出与索引，而载荷结构
-- 尚未定型（0.0.8 才定义 TLS / Basic 的具体字段）。
-- 端点存储**规范化前**的原文；客户端缓存的键由 DockerEndpointNormalizer
-- 在取连接时现算，因此库中写法不影响连接复用。
-- ============================================================================

CREATE TABLE hosts
(
    id                     TEXT    NOT NULL PRIMARY KEY,
    name                   TEXT    NOT NULL,
    type                   TEXT    NOT NULL,
    endpoint               TEXT    NOT NULL,
    auth_kind              TEXT    NOT NULL,
    auth_config_json       TEXT    NULL,
    enabled                INTEGER NOT NULL DEFAULT 1,
    creation_time          TEXT    NOT NULL,
    creator_id             TEXT    NOT NULL,
    last_modification_time TEXT    NULL,
    last_modifier_id       TEXT    NOT NULL,
    is_deleted             INTEGER NOT NULL DEFAULT 0,
    deletion_time          TEXT    NULL,
    deleter_id             TEXT    NOT NULL
);

-- 主机显示名不区分大小写且唯一；删除后名称可再次使用
CREATE UNIQUE INDEX ux_hosts_name
    ON hosts (name COLLATE NOCASE)
    WHERE is_deleted = 0;
