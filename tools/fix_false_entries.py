"""剔除"假入口"：sweep 里有些地址其实是某个函数体中间的位置。

表现：codegen 能从该地址生成函数，但函数里有分支跳到一个"死块"标签，
codegen 认定不可达没生成 → 编译报
    error : use of undeclared label 'loc_XXXXXXXX'

判定与修复：扫描 generated/*.cpp，找出引用了未定义 loc_ 的函数，
把该函数的入口地址从 manifest 的 sweep 块里删掉（它会重新归属到真正的函数），
重跑 codegen，直到没有未声明标签。

用法: python tools/fix_false_entries.py [--max 20]
"""
from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GEN = ROOT / "generated/default"
MANIFEST = ROOT / "DoubleDragon_manifest.toml"
REXGLUE = r"D:/ReverseTools/ReXGlueSDK/bin/rexglue.exe"

FUNC_RE = re.compile(r"^DEFINE_REX_FUNC\((\w+)\) \{", re.M)
LABEL_DEF_RE = re.compile(r"^(loc_[0-9A-Fa-f]{8}):", re.M)
GOTO_RE = re.compile(r"goto (loc_[0-9A-Fa-f]{8});")


def scan_undeclared() -> dict[int, str]:
    """返回 {函数入口地址: 函数名}，这些函数引用了本函数内未定义的标签。"""
    bad: dict[int, str] = {}
    for path in sorted(GEN.glob("DoubleDragon_recomp*.cpp")):
        text = path.read_text(encoding="utf-8", errors="surrogateescape")
        marks = [(m.start(), m.group(1)) for m in FUNC_RE.finditer(text)]
        if not marks:
            continue
        marks.append((len(text), ""))
        for (start, name), (end, _) in zip(marks, marks[1:]):
            body = text[start:end]
            defined = set(LABEL_DEF_RE.findall(body))
            used = set(GOTO_RE.findall(body))
            missing = used - defined
            if missing:
                m = re.match(r"(?:FUN|sub)_([0-9A-Fa-f]{8})$", name)
                if m:
                    bad[int(m.group(1), 16)] = name
                break  # 一个文件先报一处，下一轮再看
    return bad


def drop_from_manifest(addrs: set[int]) -> int:
    text = MANIFEST.read_text(encoding="utf-8")
    removed = 0
    lines = []
    for line in text.split("\n"):
        m = re.match(r"^0x([0-9A-Fa-f]{8}) = \{ name = \"", line)
        if m and int(m.group(1), 16) in addrs:
            removed += 1
            continue
        lines.append(line)
    MANIFEST.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    return removed


def run_codegen() -> int:
    p = subprocess.run([REXGLUE, "codegen", "DoubleDragon_manifest.toml", "--ignore-stamp"],
                       cwd=str(ROOT), capture_output=True, text=True, errors="ignore")
    return p.returncode


def main() -> None:
    max_iter = 20
    if "--max" in sys.argv:
        max_iter = int(sys.argv[sys.argv.index("--max") + 1])
    for it in range(max_iter):
        bad = scan_undeclared()
        if not bad:
            print(f"iter {it}: 没有未声明标签，收工")
            return
        print(f"iter {it}: 假入口 {len(bad)} 个 -> {[hex(a) for a in sorted(bad)]}")
        n = drop_from_manifest(set(bad))
        print(f"  从 manifest 删除 {n} 条")
        rc = run_codegen()
        print(f"  codegen exit={rc}")
    print("达到迭代上限")


if __name__ == "__main__":
    main()
