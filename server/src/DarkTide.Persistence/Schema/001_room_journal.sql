-- 房间命令日志。每条已接收命令的最终结果（执行或规则拒绝）按顺序写入一次；
-- 进程重启后按序号重放即可重建房间。
--
-- 约束是正确性的一部分，不只是优化：
--   * (room_id, sequence) 主键：两个进程同时写同一房间时，后写的一方因序号冲突失败，而不是悄悄分叉。
--   * (room_id, command_id) 唯一：同一命令编号不会被记录两次，缓存淘汰也不解除幂等。

CREATE TABLE IF NOT EXISTS rooms (
    room_id     uuid        PRIMARY KEY,
    seat_count  smallint    NOT NULL CHECK (seat_count BETWEEN 2 AND 8),
    created_at  timestamptz NOT NULL DEFAULT now(),
    closed_at   timestamptz
);

CREATE TABLE IF NOT EXISTS room_commands (
    room_id          uuid        NOT NULL REFERENCES rooms (room_id),
    sequence         bigint      NOT NULL CHECK (sequence >= 1),
    command_id       uuid        NOT NULL,
    command_kind     text        NOT NULL,
    -- 版本化 JSON（见 CommandCodec）。发牌命令含整副牌序：本表不对玩家开放，导出与备份按受限数据处理。
    payload          jsonb       NOT NULL,
    applied_version  bigint      CHECK (applied_version >= 0),
    rejection_reason integer,
    rejection_text   text,
    recorded_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (room_id, sequence),
    CONSTRAINT room_commands_command_id_unique UNIQUE (room_id, command_id),
    -- 结果只能是二者之一：成功带版本，拒绝带原因。
    CONSTRAINT room_commands_one_outcome CHECK (
        (applied_version IS NOT NULL AND rejection_reason IS NULL AND rejection_text IS NULL)
        OR (applied_version IS NULL AND rejection_reason IS NOT NULL AND rejection_text IS NOT NULL))
);

CREATE INDEX IF NOT EXISTS rooms_open ON rooms (room_id) WHERE closed_at IS NULL;
