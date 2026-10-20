-- ============================================================================
-- 0007_repositories.sql —— 私有仓库（仓库）配置
--
-- 本迭代（0.4.0）引入「推镜像到私有仓库」，因此需要一份可管理的凭据存储。
--
-- ## 表的职责
-- repositories 一行 = 一条「某个仓库地址 + 一套登录凭据」的配置。
-- 别名（name）给人看，地址（server_address）给机器用。
--
-- ## 为什么口令列存的是密文而不是明文
-- 口令是长期有效的凭据。库文件在数据卷上，备份会被复制到多处（scripts/backup.sh），
-- 明文口令一旦入库，等于「备份文件里躺着一把万能钥匙」。
-- 因此这一列存 AES-GCM 密文（由 Infrastructure/Security/RepositorySecretProtector 负责），
-- 解密只发生在「真的要拿凭据去连 Docker」的那一刻。
--
-- ⚠️ 密钥本身**不在这张表里**：它与 JWT 签名密钥同源，走 SecretFileStore 落到
-- /app/data 下（0600），并随数据卷一起备份。理由与 jwt.key 相同：
-- 密钥丢了，已保存的凭据就全部解不开（表现是「认证失败」，但根因是密钥没了）。
--
-- ## 唯一性
-- 别名与服务地址各占一个部分唯一索引（WHERE is_deleted = 0）：
--   * 同名会让管理界面出现两条分不清的记录；
--   * **同址更危险**——它会让「推镜像时该用哪条凭据」变成随机行为。
-- 软删除后两者都可再次使用。
--
-- ## 为什么加密后的列不建索引
-- 密文每次加密都不同（随机 nonce），无法用于查找。需要按地址找配置时，
-- 走 server_address（明文、规范化后存）这一列。
-- ============================================================================

CREATE TABLE repositories
(
    id                     TEXT    NOT NULL PRIMARY KEY,

    -- 别名：管理员可读的名字（如「内网 Harbor」）。不区分大小写且唯一。
    name                   TEXT    NOT NULL,

    -- 规范化后的服务地址（host 或 host:port）。小写存储。
    -- 刻意存**明文**：它不是秘密（Docker 客户端本来就要把它放进镜像引用），
    -- 而且必须能按它查找配置。
    server_address         TEXT    NOT NULL,

    -- 登录账号。空串表示匿名访问。
    username               TEXT    NOT NULL DEFAULT '',

    -- 口令密文（Base64 文本）。空串表示该配置不带口令。
    -- ⚠️ 绝不在此存明文，也绝不把它写进日志。
    encrypted_password     TEXT    NOT NULL DEFAULT '',

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

-- 别名不区分大小写且唯一；删除后别名可再次使用
CREATE UNIQUE INDEX ux_repositories_name
    ON repositories (name COLLATE NOCASE)
    WHERE is_deleted = 0;

-- 服务地址唯一（同样不区分大小写）；删除后地址可再次使用
CREATE UNIQUE INDEX ux_repositories_server_address
    ON repositories (server_address COLLATE NOCASE)
    WHERE is_deleted = 0;

-- ---------------------------------------------------------------------------
-- 内置角色的权限种子补齐
--
-- 0.4.0 新增了两个 Global 权限：repository.read / repository.write。
-- 权限目录是编译期事实（Core/Permissions/Permission.cs），因此内置角色的
-- 权限集必须与之一致地种进来——否则「权限矩阵里列出了 repository.write
-- 但 admin 点不动」会成为最难查的一类不一致。
--
-- ⚠️ 只给 admin 加：repository.* 是**全局**权限（仓库配置与主机无关），
-- 给 operator / viewer / auditor 加就等于把「管理私有仓库凭据」下放给
-- 主机级运维，那是一次静默提权。
--
-- ⚠️ 内置角色的权限集被 Role.SetPermissions 锁死，改权限要靠改迁移——
-- 这正是「权限目录是编译期事实」这一设计的代价，也是它的好处。
-- ---------------------------------------------------------------------------
INSERT INTO role_permissions (role_id, permission, creation_time, creator_id, last_modification_time, last_modifier_id)
VALUES
    ('00000000-0000-0000-0000-0000000000a1', 'repository.read',  '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000'),
    ('00000000-0000-0000-0000-0000000000a1', 'repository.write', '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000000', NULL, '00000000-0000-0000-0000-000000000000');
