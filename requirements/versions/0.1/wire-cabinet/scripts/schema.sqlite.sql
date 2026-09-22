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
    is_uncommanded INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_app_operation_log_ts ON app_operation_log(ts);
CREATE INDEX IF NOT EXISTS ix_app_operation_log_lot_no ON app_operation_log(lot_no);
CREATE INDEX IF NOT EXISTS ix_app_operation_log_slot_no ON app_operation_log(slot_no);
CREATE INDEX IF NOT EXISTS ix_app_operation_log_operator_id ON app_operation_log(operator_id);
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
