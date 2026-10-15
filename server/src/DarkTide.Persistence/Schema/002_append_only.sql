-- 命令日志只能追加：任何账号（包括表的所有者）都不能修改或删除已有记录。
-- 权限可以被误配，触发器在权限之外再挡一层；TRUNCATE 不触发行级触发器，所以单独用语句级触发器拦截。
-- 运行账号的权限由 grant_runtime_role() 授予，见 SchemaMigrator.GrantRuntimeRoleAsync。

CREATE OR REPLACE FUNCTION room_commands_reject_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'room_commands 只能追加，不允许 %', TG_OP
        USING ERRCODE = 'insufficient_privilege';
END;
$$;

DROP TRIGGER IF EXISTS room_commands_append_only ON room_commands;
CREATE TRIGGER room_commands_append_only
    BEFORE UPDATE OR DELETE ON room_commands
    FOR EACH ROW EXECUTE FUNCTION room_commands_reject_change();

DROP TRIGGER IF EXISTS room_commands_no_truncate ON room_commands;
CREATE TRIGGER room_commands_no_truncate
    BEFORE TRUNCATE ON room_commands
    FOR EACH STATEMENT EXECUTE FUNCTION room_commands_reject_change();

-- rooms 只允许把未关闭的房间标记为关闭，其余列一律不可改，也不可删除。
CREATE OR REPLACE FUNCTION rooms_guard_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'rooms 不允许 %', TG_OP USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NEW.room_id IS DISTINCT FROM OLD.room_id
        OR NEW.seat_count IS DISTINCT FROM OLD.seat_count
        OR NEW.created_at IS DISTINCT FROM OLD.created_at
        OR OLD.closed_at IS NOT NULL
        OR NEW.closed_at IS NULL THEN
        RAISE EXCEPTION 'rooms 只允许把未关闭的房间标记为关闭' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS rooms_guard ON rooms;
CREATE TRIGGER rooms_guard
    BEFORE UPDATE OR DELETE ON rooms
    FOR EACH ROW EXECUTE FUNCTION rooms_guard_change();

DROP TRIGGER IF EXISTS rooms_no_truncate ON rooms;
CREATE TRIGGER rooms_no_truncate
    BEFORE TRUNCATE ON rooms
    FOR EACH STATEMENT EXECUTE FUNCTION room_commands_reject_change();
