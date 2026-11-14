"""扫描 EPLAN .ema 宏，统计每个宏自带的图形线段(O6)与连接点(O37)数量。
用途：找出"自带连线"的宏，用于拼装 ABB 布局图。
"""
import re, glob, os, json, sys

MACRO_ROOT = r"D:\EPLAN\Platform Data\2.9.4\Macros"

# O6 = 图形线段(有 A83 长度)；O37 = 连接点/连接拓扑(有 A683 引用表)；O17 = 符号实例
RE_O6 = re.compile(r'<O6\b[^>]*>')
RE_O37 = re.compile(r'<O37\b[^>]*>')
RE_O17 = re.compile(r'<O17\b[^>]*>')
RE_A683 = re.compile(r'A683="([^"]*)"')
RE_A1631 = re.compile(r'A1631="([^"]*)"')   # 图形折线点串

def count_macros():
    return len(glob.glob(os.path.join(MACRO_ROOT, "**", "*.ema"), recursive=True))

def analyze(path):
    try:
        raw = open(path, "r", encoding="utf-8", errors="ignore").read()
    except Exception:
        return None
    if not raw.lstrip().startswith("<?xml") and "EplanPxfRoot" not in raw[:400]:
        return None  # 非 PXF 格式，跳过

    o6 = RE_O6.findall(raw)          # 线段
    o37 = RE_O37.findall(raw)        # 连接拓扑
    o17 = RE_O17.findall(raw)        # 符号实例

    # 连接点总数 = 各 O37 的 A683 引用列表里 "类型/ID" 的条目数
    conn_refs = 0
    for m in o37:
        a683 = RE_A683.search(m)
        if a683:
            conn_refs += len([x for x in a683.group(1).split(";") if x.strip()])

    # 折线（真正"画线"的图元）
    polylines = 0
    for m in raw.splitlines():
        a = RE_A1631.search(m)
        if a and a.group(1).strip():
            polylines += 1

    return {
        "name": os.path.basename(path),
        "rel": os.path.relpath(path, MACRO_ROOT).replace("\\", "/"),
        "lines": len(o6),
        "conn_topo": len(o37),
        "conn_refs": conn_refs,
        "symbols": len(o17),
        "polylines": polylines,
        "size": os.path.getsize(path),
    }

def main():
    files = glob.glob(os.path.join(MACRO_ROOT, "**", "*.ema"), recursive=True)
    sys.stderr.write("扫描 %d 个宏...\n" % len(files))
    rows = []
    skipped = 0
    for i, f in enumerate(files):
        r = analyze(f)
        if r is None:
            skipped += 1
            continue
        rows.append(r)
        if (i + 1) % 200 == 0:
            sys.stderr.write("  ...%d/%d\n" % (i + 1, len(files)))

    out = r"D:\默认存储空间\EPLAN\macro_scan.json"
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(rows, fh, ensure_ascii=False, indent=1)

    # 排序：有连线的优先
    rows.sort(key=lambda r: (r["polylines"] + r["lines"], r["conn_refs"]), reverse=True)

    print("=" * 78)
    print("宏扫描完成: 成功 %d, 跳过(非Pxf格式) %d" % (len(rows), skipped))
    print("=" * 78)
    print("\n【Top 30】自带连线最多的宏 (polylines+lines, 连接引用, 符号数)")
    print("%-52s %6s %6s %6s" % ("宏名", "线", "连线", "符号"))
    print("-" * 78)
    for r in rows[:30]:
        n = r["name"][:50]
        print("%-52s %6d %6d %6d" % (n, r["polylines"] + r["lines"], r["conn_refs"], r["symbols"]))

    # 统计概览
    with_conn = [r for r in rows if r["conn_refs"] > 0]
    with_line = [r for r in rows if (r["polylines"] + r["lines"]) > 0]
    print("\n" + "=" * 78)
    print("统计: 有连接拓扑的宏 %d/%d ; 有图形线的宏 %d/%d" %
          (len(with_conn), len(rows), len(with_line), len(rows)))
    print("结果已存: %s" % out)

if __name__ == "__main__":
    main()
