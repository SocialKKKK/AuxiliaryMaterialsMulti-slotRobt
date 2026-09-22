-- MES mock（SQLite）表与演示数据。
--
-- 为什么需要它：本机/离线开发走 SqliteMockMesGateway，MES 只读查询直接打在
-- 这些 SQLite 表上，但 schema.sqlite.sql 只建应用库的表。此前只有
-- v_fw_material_indetail 由 WireMesDiscoSchema.EnsureMockMesColumn 建出（仅建表、无数据），
-- 其余 mock MES 表完全不存在——结果是操作员「存废取新」流程在本机根本跑不起来
-- （mv_fw_username / fw_eqpres_eqpinformation / fw_wip_trans / v_fw_wip_sublot 全部报无此表）。
--
-- 与本目录其它 seed 同样的风格：每次启动 DELETE + INSERT 重置为演示态，
-- 因此这里的焊丝批号必须与 seed-app_slot.sqlite.sql 里的格口数据保持一致。
-- 现场用 Oracle 时这些表不会被读（FlowEngine 对 mes_mock.* 条目会改走正式 catalog）。

CREATE TABLE IF NOT EXISTS mv_fw_username (
    usercode TEXT PRIMARY KEY,
    username TEXT
);

CREATE TABLE IF NOT EXISTS v_fw_material_indetail (
    sublot     TEXT,
    spec       TEXT,
    type       TEXT,
    code       TEXT,
    shelflife  TEXT,
    matlot     TEXT,
    qty        REAL,
    state      TEXT,
    DISCOEQPNO TEXT
);

CREATE TABLE IF NOT EXISTS fw_eqpres_eqpinformation (
    eqpno TEXT,
    step  TEXT
);

CREATE TABLE IF NOT EXISTS fw_wip_trans (
    lot    TEXT,
    eqp    TEXT,
    step   TEXT,
    task   TEXT,
    remark TEXT,
    dates  TEXT
);

CREATE TABLE IF NOT EXISTS v_fw_wip_sublot (
    lot   TEXT,
    qty   REAL,
    step  TEXT,
    state TEXT
);

-- ===== 操作员 =====
DELETE FROM mv_fw_username;
INSERT INTO mv_fw_username (usercode, username) VALUES
 ('1001', '张三'),
 ('1002', '李四');

-- ===== 焊丝明细 =====
-- 前三条与 seed-app_slot.sqlite.sql 中 F-B02(可用) / F-B03(归还) / F-C01(可用) 的批号一致；
-- 14Z1888-0160 是操作员归还流程录入的批号；
-- 14Z1888-0555 与 F-B02 同规格但过期更早，用于验证「按最先过期发放」。
DELETE FROM v_fw_material_indetail;
INSERT INTO v_fw_material_indetail (sublot, spec, type, code, shelflife, matlot, qty, state, DISCOEQPNO) VALUES
 ('14Z1888-0888', 'φ42(MAXSOFT)', '焊丝', 'WIRE-φ42', '2026-12-31 00:00:00', '14Z1888', 1.0, '可用', NULL),
 ('14Z1888-0777', 'φ42(MAXSOFT)', '焊丝', 'WIRE-φ42', '2026-10-31 00:00:00', '14Z1888', 1.0, '归还', NULL),
 ('14Z1999-0001', 'φ36(SOFT)',    '焊丝', 'WIRE-φ36', '2026-11-30 00:00:00', '14Z1999', 1.0, '可用', NULL),
 ('14Z1888-0160', 'φ42(MAXSOFT)', '焊丝', 'WIRE-φ42', '2026-12-31 00:00:00', '14Z1888', 1.0, '归还', NULL),
 ('14Z1888-0555', 'φ42(MAXSOFT)', '焊丝', 'WIRE-φ42', '2026-08-31 00:00:00', '14Z1888', 1.0, '可用', NULL);

-- ===== 机台 =====
DELETE FROM fw_eqpres_eqpinformation;
INSERT INTO fw_eqpres_eqpinformation (eqpno, step) VALUES
 ('1QH029', '焊线'),
 ('1QH030', '焊线');

-- ===== 在制品事务：最近完工产品批号 =====
DELETE FROM fw_wip_trans;
INSERT INTO fw_wip_trans (lot, eqp, step, task, remark, dates) VALUES
 ('P20260601-01', '1QH029', '焊线01', '完工', NULL,       '2026-06-01 08:00:00'),
 ('P20260602-01', '1QH029', '键合01', '完工', NULL,       '2026-06-02 09:30:00'),
 ('P20260602-09', '1QH029', '焊线01', '完工', '取消-误报', '2026-06-02 23:59:00');

-- ===== 在制品子批 =====
DELETE FROM v_fw_wip_sublot;
INSERT INTO v_fw_wip_sublot (lot, qty, step, state) VALUES
 ('P20260602-01', 3000, '焊线', '在制'),
 ('P20260602-01', 2000, '焊线', '在制'),
 ('P20260601-01', 1500, '键合', '关闭');
