"""让 codegen 收敛：既补"跳不到的缺口"，又剔除"把函数切坏的多余入口"。

两类反馈：

1. `UnresolvedCall ... target not in any function`
   —— 目标落在没人覆盖的空洞里，补入 manifest 当函数入口。

2. `Unresolved (conditional) branch to T from F`
   —— F 里的分支跳到 T，但 T 被判给了另一个函数，说明 F 和 T 之间那个
      入口是"把函数切断"的多余声明：把 (F, T] 区间里最靠后的那条声明删掉。

反复跑到 codegen 退出码 0 且没有再报未解析分支。
"""
from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MAN = ROOT / "DoubleDragon_manifest.toml"
REXGLUE = r"D:/ReverseTools/ReXGlueSDK/bin/rexglue.exe"

CALL_RX = re.compile(r"0x([0-9A-Fa-f]{8}) from 0x[0-9A-Fa-f]{8}:.*target not in any function")
# "Unresolved branch/b target 0xT from 0xF" / "Unresolved function 0xT from 0xF" /
# "Unresolved conditional branch to 0xT from 0xF"
BR_RX = re.compile(r"Unresolved (?!Call)[^\n]*?0x([0-9A-Fa-f]{8}) from 0x([0-9A-Fa-f]{8})")
ENTRY_RX = re.compile(r"^0x([0-9A-Fa-f]{8}) = \{")


def declared(lines: list[str]) -> dict[int, int]:
    out = {}
    for i, line in enumerate(lines):
        m = ENTRY_RX.match(line)
        if m:
            out[int(m.group(1), 16)] = i
    return out


def main() -> None:
    for it in range(40):
        p = subprocess.run([REXGLUE, "codegen", "DoubleDragon_manifest.toml", "--ignore-stamp"],
                           cwd=str(ROOT), capture_output=True, text=True, errors="ignore")
        out = (p.stdout or "") + (p.stderr or "")
        lines = MAN.read_text(encoding="utf-8").split("\n")
        ent = declared(lines)

        adds = sorted({int(x, 16) for x in CALL_RX.findall(out)} - set(ent))
        brs = sorted({(int(t, 16), int(f, 16)) for t, f in BR_RX.findall(out)})
        print(f"iter {it}: exit={p.returncode} 缺口={len(adds)} 未解析分支={len(brs)} 条目={len(ent)}")

        if p.returncode == 0 and not brs:
            print("收敛完成")
            return

        drop: set[int] = set()
        for t, f in brs:
            # 分支两端之间的入口一定是"把函数切断"的那条声明，删掉它让函数重新连通。
            lo, hi = min(t, f), max(t, f)
            cand = [e for e in ent if lo < e <= hi]
            if cand:
                drop.add(max(cand))

        if adds:
            i = next(k for k, l in enumerate(lines) if l.startswith("# 调试探针（临时）"))
            block = ["# 缺口补全（tools/iterate_codegen.py 自动生成）"] + \
                    [f'0x{a:08X} = {{ name = "FUN_{a:08X}" }}' for a in adds] + [""]
            lines[i:i] = block
            print(f"  补入缺口 {len(adds)} 条")

        if drop:
            lines = [l for l in lines
                     if not (ENTRY_RX.match(l) and int(ENTRY_RX.match(l).group(1), 16) in drop)]
            print(f"  剔除切分点 {len(drop)} 条")

        if not adds and not drop:
            print("无法继续收敛，剩余输出：")
            print(out[-1500:])
            return

        MAN.write_text("\n".join(lines), encoding="utf-8", newline="\n")

    print("达到迭代上限")


if __name__ == "__main__":
    main()
