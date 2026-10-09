-- 0.1 焊丝柜 SQLite 应用库

CREATE TABLE IF NOT EXISTS app_slot (
    slot_id     INTEGER PRIMARY KEY,
    slot_no     TEXT NOT NULL UNIQUE,
    door_position TEXT,
    slot_index  INTEGER,
    is_enabled  INTEGER NOT NULL DEFAULT 1,
    io_wired    INTEGER NOT NULL DEFAULT 1,
    usage_type  TEXT NOT NULL,
    biz_state   TEXT NOT NULL,
    door_state  TEXT NOT NULL DEFAULT 'closed',
    lock_state  TEXT NOT NULL DEFAULT 'locked',
    wire_lot_no TEXT,
    wire_spec   TEXT,
    wire_code   TEXT,
    shelflife   TEXT,
    matlot      TEXT,
    qty         REAL,
    wire_state  TEXT,
    updated_at  TEXT
);

CREATE TABLE IF NOT EXISTS welding_wire_materials (
    id                   INTEGER PRIMARY KEY,
    material_type        TEXT NOT NULL,
    specification_model  TEXT NOT NULL,
    empty_spool_weight   REAL NOT NULL,
    created_at           TEXT,
    updated_at           TEXT,
    UNIQUE (material_type, specification_model)
);

-- 物料存取记录（0.2）。新库一次建全；已存在的库由 OperationLogSchema.Ensure 幂等补列。
CREATE TABLE IF NOT EXISTS app_operation_log (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    ts             TEXT NOT NULL DEFAULT (datetime('now')),
    -- 0.1 既有列（保留不动）
    operator_id    TEXT,
    flow_id        TEXT,
    node_id        TEXT,
    slot_no        TEXT,
    lot_no         TEXT,
    message        TEXT,
    mes_result     TEXT,
    lock_result    TEXT,
    error_info     TEXT,
    -- 0.2 新增列
    operator_name  TEXT,
    operator_team  TEXT,
    operator_role  TEXT,
    action_type    TEXT,
    wire_spec      TEXT,
    eqp_no         TEXT,
    product_lot_no TEXT,
    qty            REAL,
    remain_qty     REAL,
    station        TEXT,
    result         TEXT,
    door_open_ts   TEXT,
    door_close_ts  TEXT,
    is_cleared     INTEGER NOT NULL DEFAULT 0,
    is_uncommanded INTEGER NOT NULL DEFAULT 0,
    -- 0.2 记录口径优化（2026-10-09）
    detail         TEXT,     -- JSON：批量格口清单 / 被清批号 / 重复失败统计
    slot_count     INTEGER,  -- 一次批量动作涉及的格口数
    corrects_id    INTEGER   -- 更正记录指向被更正的那条（B 档，查询默认取最新有效版本）
);

CREATE INDEX IF NOT EXISTS ix_app_operation_log_ts ON app_operation_log(ts);
CREATE INDEX IF NOT EXISTS ix_app_operation_log_lot_no ON app_operation_log(lot_no);
CREATE INDEX IF NOT EXISTS ix_app_operation_log_slot_no ON app_operation_log(slot_no);
CREATE INDEX IF NOT EXISTS ix_app_operation_log_operator_id ON app_operation_log(operator_id);

-- 记录表「业务事实不可改写」（口径优化 A 档，2026-10-09）。
-- 只允许把 NULL 的门时刻补齐、以及更新 detail（过程派生字段：格口清单 / 重复失败统计）：
-- 其它任何列一旦改动即 ABORT。detail 允许改是因为它承载「连续失败次数 / 首末时刻」这类
-- 会随后续事件增长的过程信息，不是业务事实；谁、何时、哪个格口、哪批料、结果仍不可改写。
-- 表「只增不删」的原始约束（禁止 UPDATE / DELETE）由 OperationLogRetentionTests 在源码层扫描；
-- 这里是库层的第二道防线，防止绕过源码扫描的写入。
CREATE TRIGGER IF NOT EXISTS trg_app_operation_log_append_only
BEFORE UPDATE ON app_operation_log
FOR EACH ROW
WHEN
       NEW.id             IS NOT OLD.id
    OR NEW.ts             IS NOT OLD.ts
    OR NEW.operator_id    IS NOT OLD.operator_id
    OR NEW.operator_name  IS NOT OLD.operator_name
    OR NEW.operator_team  IS NOT OLD.operator_team
    OR NEW.operator_role  IS NOT OLD.operator_role
    OR NEW.action_type    IS NOT OLD.action_type
    OR NEW.slot_no        IS NOT OLD.slot_no
    OR NEW.lot_no         IS NOT OLD.lot_no
    OR NEW.wire_spec      IS NOT OLD.wire_spec
    OR NEW.eqp_no         IS NOT OLD.eqp_no
    OR NEW.product_lot_no IS NOT OLD.product_lot_no
    OR NEW.station        IS NOT OLD.station
    OR NEW.qty            IS NOT OLD.qty
    OR NEW.remain_qty     IS NOT OLD.remain_qty
    OR NEW.result         IS NOT OLD.result
    OR NEW.message        IS NOT OLD.message
    OR NEW.mes_result     IS NOT OLD.mes_result
    OR NEW.lock_result    IS NOT OLD.lock_result
    OR NEW.error_info     IS NOT OLD.error_info
    OR NEW.is_cleared     IS NOT OLD.is_cleared
    OR NEW.is_uncommanded IS NOT OLD.is_uncommanded
    OR NEW.flow_id        IS NOT OLD.flow_id
    OR NEW.node_id        IS NOT OLD.node_id
    OR NEW.slot_count     IS NOT OLD.slot_count
    OR NEW.corrects_id    IS NOT OLD.corrects_id
    OR (OLD.door_open_ts  IS NOT NULL AND NEW.door_open_ts  IS NOT OLD.door_open_ts)
    OR (OLD.door_close_ts IS NOT NULL AND NEW.door_close_ts IS NOT OLD.door_close_ts)
BEGIN
    SELECT RAISE(ABORT, 'app_operation_log 业务事实不可改写：仅允许补齐 NULL 的门时刻与更新 detail');
END;
CREATE INDEX IF NOT EXISTS ix_app_operation_log_eqp_no ON app_operation_log(eqp_no);

-- 记录写入失败时的待补偿队列（写失败不阻断业务，整条记录转存于此）
-- status: pending / failed / success；next_retry_at 供后台按退避重试。
-- 老库已有的该表由 OperationLogSchema.Ensure 做幂等列迁移，这里改了要同步那边。
CREATE TABLE IF NOT EXISTS operation_log_pending (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    ts            TEXT NOT NULL DEFAULT (datetime('now')),
    entry_json    TEXT NOT NULL,
    status        TEXT NOT NULL DEFAULT 'pending',
    retry_count   INTEGER NOT NULL DEFAULT 0,
    next_retry_at TEXT,
    last_error    TEXT,
    last_try_ts   TEXT
);

CREATE TABLE IF NOT EXISTS agv_move_log (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ts              TEXT NOT NULL DEFAULT (datetime('now')),
    target_station  TEXT,
    order_id        TEXT,
    move_state      TEXT,
    battery_percent REAL,
    is_charge       INTEGER DEFAULT 0,
    message         TEXT
);

-- MH 存料流程异常中断快照（单行，重启弹窗提示用；不自动补账）
CREATE TABLE IF NOT EXISTS mh_interrupted_load (
    id              INTEGER PRIMARY KEY CHECK (id = 1),
    flow_id         TEXT NOT NULL,
    slot_id         INTEGER NOT NULL,
    slot_no         TEXT NOT NULL,
    wire_lot_no     TEXT NOT NULL,
    wire_spec       TEXT,
    interrupted_at  TEXT NOT NULL DEFAULT (datetime('now'))
);
