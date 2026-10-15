-- 每个房间记录创建时的规则版本（见 RulesVersion）。恢复时核对，不一致就拒绝加载，
-- 并且启动时能按版本统计，提前发现“部署了不兼容的版本，还有旧房间没结束”。
-- 已有房间回填为 1：本列加入之前只有这一个版本。
ALTER TABLE rooms ADD COLUMN IF NOT EXISTS rules_version integer NOT NULL DEFAULT 1 CHECK (rules_version >= 1);
-- 去掉默认值：之后每次建房都必须显式写入版本，不能因为漏写而被悄悄记成 1。
ALTER TABLE rooms ALTER COLUMN rules_version DROP DEFAULT;

-- 只追加触发器要同时保护新列：版本一旦写入就不能改，否则可以把旧房间改成“兼容”骗过核对。
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
        OR OLD.closed_at IS NOT NULL
        OR NEW.closed_at IS NULL THEN
        RAISE EXCEPTION 'rooms 只允许把未关闭的房间标记为关闭' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;
