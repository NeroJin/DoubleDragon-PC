"""发现"运行时拼出来的代码地址"（回调/成员函数适配器），写进 manifest 当函数入口。

为什么需要：游戏里大量出现

    0x820EE6D8  lis  r11, 0x820F
    0x820EE6F0  addi r4, r11, -0x1978     ; r4 = 0x820EE688 —— 一个 12 字节的 jmp thunk
    0x820EE6F4  bl   0x820E41E0           ; 存进回调结构

这个地址既不在 .pdata（无 unwind 信息），也没有静态 call/branch 指向，
codegen 不会为它生成函数，运行到就
    [FATAL] Call to invalid or unregistered function at guest address 0x820EE688

做法：线性扫描 .text，跟踪「某个寄存器当前持有哪个 64KB 页基址」（lis 的效果），
遇到 addi/ori 就把高低位拼起来；落在代码区内的结果即候选入口。
控制流转移处不清空状态（保守，宁可多收几个也不会漏）。

用法:
    python tools/gen_callback_entries.py            # 写入 manifest
    python tools/gen_callback_entries.py --dry-run   # 只统计
"""
from __future__ import annotations

import re
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
XEX = ROOT / "dd_dec.xex"
MANIFEST = ROOT / "DoubleDragon_manifest.toml"
REG = ROOT / "generated/default/DoubleDragon_register.cpp"

IMG_BASE = 0x82000000
IMG_OFF = 0x2000
TEXT_LO = 0x82080000
TEXT_HI = 0x823AD7DC

BEGIN = "# >>> 运行时拼地址的回调入口（tools/gen_callback_entries.py 生成，勿手改）"
END = "# <<< 运行时拼地址的回调入口"


def sext16(v: int) -> int:
    return v - 0x10000 if v & 0x8000 else v


def scan(data: bytes) -> list[int]:
    found: list[int] = []
    page: dict[int, int] = {}  # reg -> 高 16 位基址（0x820F0000 这种）
    for addr in range(TEXT_LO, TEXT_HI, 4):
        w = struct.unpack(">I", data[IMG_OFF + (addr - IMG_BASE):IMG_OFF + (addr - IMG_BASE) + 4])[0]
        op = w >> 26
        rd = (w >> 21) & 0x1F
        ra = (w >> 16) & 0x1F
        imm = w & 0xFFFF
        if op == 15:  # lis / addis
            page[rd] = (imm << 16) & 0xFFFF0000
            continue
        if op == 14:  # addi
            if ra in page:
                val = (page[ra] + sext16(imm)) & 0xFFFFFFFF
                if TEXT_LO <= val < TEXT_HI:
                    found.append(val)
            page[rd] = 0  # addi 结果不再是纯页基址
            continue
        if op == 24:  # ori
            if ra in page:
                val = page[ra] | imm
                if TEXT_LO <= val < TEXT_HI:
                    found.append(val)
            page[rd] = 0
            continue
        # 其它写 rD 的指令：让该寄存器的页基址失效
        if op in (7, 8, 12, 13, 20, 21, 23, 25, 26, 28, 29, 31, 32, 34, 36, 40, 42, 44, 46, 48, 50, 52, 54, 56, 58, 60):
            page[rd] = 0
    return sorted(set(found))


def covered_map(data: bytes) -> bytearray:
    """用已知函数入口做线性扫描，标出被"某个函数体"覆盖的字节。

    只是粗略的够用近似：从入口顺着解码，直到遇到不落空的终结指令
    （b / blr / bctr / rfi）或解不出的数据为止。函数的"冷块"（blr 之后还有
    一批被分支回跳的代码）不会被标进来 —— 正好，那些空洞才是我们要找的
    运行时回调入口（例如 0x820EE688 夹在 bctr 和填充之间）。
    """
    from capstone import Cs, CS_ARCH_PPC, CS_MODE_32, CS_MODE_BIG_ENDIAN

    md = Cs(CS_ARCH_PPC, CS_MODE_32 | CS_MODE_BIG_ENDIAN)
    cov = bytearray(TEXT_HI - TEXT_LO)
    starts = []
    if REG.exists():
        starts = sorted(set(int(x, 16) for x in re.findall(
            r"SetFunction\(0x([0-9A-Fa-f]{8})", REG.read_text(encoding="utf-8", errors="ignore"))))
    for s in starts:
        if not (TEXT_LO <= s < TEXT_HI):
            continue
        pos = s
        for _ in range(4000):
            if pos + 4 > TEXT_HI:
                break
            off = IMG_OFF + (pos - IMG_BASE)
            insns = list(md.disasm(data[off:off + 4], pos))
            if not insns:
                break
            ins = insns[0]
            for k in range(4):
                cov[pos - TEXT_LO + k] = 1
            pos += 4
            if ins.mnemonic in ("b", "blr", "bctr", "rfi"):
                break
    return cov


def uncovered(data: bytes, addrs: list[int], cov: bytearray) -> list[int]:
    return [a for a in addrs if cov[a - TEXT_LO] == 0]


def valid_starts(data: bytes, addrs: list[int]) -> list[int]:
    """只留下 4 字节对齐、且首条指令能被 capstone 正常解码的地址。

    lis+addi 的线性跟踪会把一些位运算中间结果也当成地址（0x82240001 这种），
    这些既不对齐也解不出指令，直接丢掉。
    """
    from capstone import Cs, CS_ARCH_PPC, CS_MODE_32, CS_MODE_BIG_ENDIAN

    md = Cs(CS_ARCH_PPC, CS_MODE_32 | CS_MODE_BIG_ENDIAN)
    ok = []
    for a in addrs:
        if a % 4:
            continue
        code = data[IMG_OFF + (a - IMG_BASE): IMG_OFF + (a - IMG_BASE) + 4]
        if not list(md.disasm(code, a)):
            continue
        ok.append(a)
    return ok


def main() -> None:
    dry = "--dry-run" in sys.argv
    data = XEX.read_bytes()
    cands = uncovered(data, valid_starts(data, scan(data)), covered_map(data))
    text = MANIFEST.read_text(encoding="utf-8")
    # 先摘掉上一次生成的块，否则会把自己写过的条目当成"已声明"
    if BEGIN in text:
        a = text.index(BEGIN)
        b = text.index(END) + len(END)
        text = text[:a] + text[b:]
    declared = set(int(x, 16) for x in re.findall(r"0x([0-9A-Fa-f]{8})\s*=\s*\{", text))
    if REG.exists():
        declared |= set(int(x, 16) for x in re.findall(
            r"SetFunction\(0x([0-9A-Fa-f]{8})", REG.read_text(encoding="utf-8", errors="ignore")))
    new = [a for a in cands if a not in declared]
    print(f"拼地址命中 {len(cands)} 个，其中未声明 {len(new)} 个")
    if dry:
        print([hex(a) for a in new[:20]])
        return

    lines = [BEGIN,
             "# 这些地址由 lis+addi/ori 拼出来当回调，静态分析看不到，不声明就 FATAL。",
             ""]
    lines += [f'0x{a:08X} = {{ name = "FUN_{a:08X}" }}' for a in new]
    lines += ["", END, ""]
    block = "\n".join(lines)

    marker = "# 调试探针（临时）"
    i = text.index(marker)
    text = text[:i] + block + text[i:]
    MANIFEST.write_text(text, encoding="utf-8", newline="\n")
    print(f"写入 {len(new)} 条")


if __name__ == "__main__":
    main()
