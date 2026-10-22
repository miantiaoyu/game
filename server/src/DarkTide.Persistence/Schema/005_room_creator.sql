-- 房主：建房的玩家。只有房主能开始整场；恢复房间时一并恢复。
-- 允许为空：此前建的房间没有房主，也有可信流程（测试、运维）建的房间。
ALTER TABLE rooms ADD COLUMN IF NOT EXISTS created_by uuid;
CREATE INDEX IF NOT EXISTS rooms_open_by_creator ON rooms (created_by) WHERE closed_at IS NULL;

-- 守卫触发器同时保护新列：房主写入后不能改，否则可以把别人的房间改成自己的。
CREATE OR REPLACE FUNCTION rooms_guard_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'rooms 不允许 %', TG_OP USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NEW.room_id IS DISTINCT FROM OLD.room_id
        OR NEW.seat_count IS DISTINCT FROM OLD.seat_count
        OR NEW.created_at IS DISTINCT FROM OLD.created_at
        OR NEW.rules_version IS DISTINCT FROM OLD.rules_version
        OR NEW.created_by IS DISTINCT FROM OLD.created_by
        OR OLD.closed_at IS NOT NULL
        OR NEW.closed_at IS NULL THEN
        RAISE EXCEPTION 'rooms 只允许把未关闭的房间标记为关闭' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;
