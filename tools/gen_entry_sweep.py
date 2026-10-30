"""补全"孤儿函数入口"：为 .text 里所有疑似函数开头、但 codegen 静态分析没发现的地址
在 manifest 里声明，让 codegen 生成对应函数。

背景：游戏大量使用「运行时拼地址」的回调，例如

    0x820EE6D8  lis  r11, 0x820F
    0x820EE6F0  addi r4, r11, -0x1978     ; r4 = 0x820EE688
    0x820EE6F4  bl   0x820E41E0           ; 把这个地址存进回调结构

这种地址既不在 .pdata（该函数没有 unwind 信息），也没有任何静态 call/branch 指向它，
codegen 因此完全不生成 —— 一旦被调用就
`[FATAL] Call to invalid or unregistered function`。

判定规则（保守，只认"前面一条是不落空的终结指令"这种布局）：
  * 前一条是 blr / bctr / bctrl / rfi，或
  * 前一条是 0 填充且再前一条是上述终结指令
这样能把连续布局里的叶子函数/thunk 全部捞出来；函数体内部的 switch case
（前面是无条件 b）故意不收，避免上万条误报。

用法:
    python tools/gen_entry_sweep.py           # 计算并写入 manifest
    python tools/gen_entry_sweep.py --dry-run # 只统计
"""
from __future__ import annotations

import re
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
XEX = ROOT / "dd_dec.xex"
MANIFEST = ROOT / "DoubleDragon_manifest.toml"

IMG_BASE = 0x82000000
IMG_OFF = 0x2000
TEXT_LO = 0x82080000
TEXT_HI = 0x823AD7DC

BEGIN = "# >>> 孤儿函数入口（tools/gen_entry_sweep.py 生成，勿手改）"
END = "# <<< 孤儿函数入口"

TERMINATORS = {
    0x4E800020,  # blr
    0x4E800420,  # bctr
    0x4E800421,  # bctrl
    0x4C000064,  # rfi
}


def load_words() -> bytes:
    return XEX.read_bytes()


def word_at(data: bytes, addr: int) -> int:
    off = IMG_OFF + (addr - IMG_BASE)
    return struct.unpack(">I", data[off:off + 4])[0]


def candidates(data: bytes) -> list[int]:
    out = []
    for addr in range(TEXT_LO + 4, TEXT_HI, 4):
        prev = word_at(data, addr - 4)
        if prev in TERMINATORS:
            out.append(addr)
        elif prev == 0 and word_at(data, addr - 8) in TERMINATORS:
            out.append(addr)
    return out


def main() -> None:
    dry = "--dry-run" in sys.argv
    data = load_words()
    text = MANIFEST.read_text(encoding="utf-8")

    # 已经有声明的不重复加（manifest 里任何位置出现过即算）
    declared = set(int(x, 16) for x in re.findall(r"0x([0-9A-Fa-f]{8})\s*=\s*\{", text))
    # 上一次 codegen 已经生成的函数也不用再声明（重复声明会报重复键）
    reg = ROOT / "generated/default/DoubleDragon_register.cpp"
    if reg.exists():
        declared |= set(
            int(x, 16)
            for x in re.findall(r"SetFunction\(0x([0-9A-Fa-f]{8})", reg.read_text(encoding="utf-8", errors="ignore"))
        )
    cands = [a for a in candidates(data) if a not in declared]
    print(f"候选孤儿入口 {len(cands)} 个（已排除 manifest 中已声明的 {len(declared)} 个）")

    # 去掉上一次生成的块
    if BEGIN in text:
        start = text.index(BEGIN)
        stop = text.index(END) + len(END)
        text = text[:start] + text[stop:]

    if dry:
        print("dry-run，未写入")
        return

    lines = [BEGIN,
             "# 理由见 tools/gen_entry_sweep.py 头部注释：回调地址是运行时用",
             "# lis+addi 拼出来的，静态不可见，不声明就会 FATAL。",
             ""]
    lines += [f'0x{a:08X} = {{ name = "FUN_{a:08X}" }}' for a in cands]
    lines += ["", END, ""]
    block = "\n".join(lines)

    marker = "# 分支目标补全（迭代生成）"
    if marker in text:
        idx = text.index(marker)
    else:
        idx = text.index("\n[[entrypoint.midasm_hook]]")
    text = text[:idx] + block + text[idx:]
    MANIFEST.write_text(text, encoding="utf-8", newline="\n")
    print(f"已写入 {len(cands)} 条到 {MANIFEST.name}")


if __name__ == "__main__":
    main()
