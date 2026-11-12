-- 容器分配复用现有表；保留全部现有主机与全局授权。
ALTER TABLE user_role_assignments ADD COLUMN container_id TEXT NULL
    CHECK (container_id IS NULL OR (host_id IS NOT NULL AND length(trim(container_id)) > 0));
DROP INDEX ux_user_role_assignments;
CREATE UNIQUE INDEX ux_user_role_assignments
    ON user_role_assignments (user_id, role_id, COALESCE(host_id, ''), COALESCE(container_id, ''))
    WHERE is_deleted = 0;
CREATE TABLE authorization_settings (
    id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    container_permission_mode TEXT NOT NULL CHECK (container_permission_mode IN ('inheritHost', 'requireAssignment'))
);
INSERT INTO authorization_settings VALUES (1, 'inheritHost');
