-- 游客身份（2026-10-22：暂不做账号系统）。服务端生成玩家编号，客户端凭“重连凭据”找回同一身份。
-- 以后接入 Steam 等登录时，只是多一种“换取会话”的方式，玩家编号与本表不变。
--
-- 只存凭据的 SHA-256 摘要，不存原文：数据库泄露或备份外流时，拿到的摘要不能直接用来冒充玩家。
-- 凭据本身是 32 字节安全随机数，熵足够高，不需要慢哈希。

CREATE TABLE IF NOT EXISTS players (
    player_id   uuid        PRIMARY KEY,
    kind        text        NOT NULL CHECK (kind IN ('guest')),
    created_at  timestamptz NOT NULL DEFAULT now(),
    -- 封禁：之后不能再换取新的访问令牌。由运维账号设置，运行账号无权修改。
    disabled_at timestamptz
);

CREATE TABLE IF NOT EXISTS player_credentials (
    credential_hash bytea       PRIMARY KEY CHECK (octet_length(credential_hash) = 32),
    player_id       uuid        NOT NULL REFERENCES players (player_id),
    issued_at       timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,
    -- 每次使用都换一张新的（轮换）：旧的标记已替换，并指向替换它的那一张，用于识别重复使用。
    replaced_at     timestamptz,
    replaced_by     bytea       CHECK (replaced_by IS NULL OR octet_length(replaced_by) = 32),
    revoked_at      timestamptz,
    CONSTRAINT player_credentials_replaced_pair CHECK ((replaced_at IS NULL) = (replaced_by IS NULL))
);

CREATE INDEX IF NOT EXISTS player_credentials_player ON player_credentials (player_id);
-- 定期清理按过期时间删除。
CREATE INDEX IF NOT EXISTS player_credentials_expires ON player_credentials (expires_at);
