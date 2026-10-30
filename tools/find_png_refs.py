#!/usr/bin/env python3
"""列出所有 lis/addi 组成的、指向 PNG 相关 .rdata 页的代码地址。"""
import struct

XEX = r"D:\Projects\DoubleDragon\dd_dec.xex"
BASE = 0x82000000
data = open(XEX, "rb").read()
hdr = struct.unpack(">I", data[8:12])[0]

def off2guest(off):
    return BASE + (off - hdr)

def guest2off(g):
    return hdr + (g - BASE)

# 目标数据页（含 IHDR/IDAT/\x89PNG 常量的区域）
PAGES = [(0x8201B000, 0x8201C000), (0x82040000, 0x82042000), (0x82019000, 0x8201A000)]

lo, hi = 0x82080000, 0x82400000
pairs = []
n = (min(len(data) - 4, guest2off(hi)) - (guest2off(lo) & ~3)) // 4
for i in range(n):
    off = (guest2off(lo) & ~3) + i * 4
    w = struct.unpack(">I", data[off:off+4])[0]
    if (w & 0xFFFF8000) != 0x3C000000:  # lis (RA=0)
        continue
    imm = w & 0xFFFF
    rd = (w >> 21) & 31
    for k in range(1, 9):
        o2 = off + k * 4
        w2 = struct.unpack(">I", data[o2:o2+4])[0]
        op = w2 >> 26
        rd2 = (w2 >> 21) & 31
        ra2 = (w2 >> 16) & 31
        if rd2 != rd or ra2 != rd:
            continue
        if op == 14:
            l = w2 & 0xFFFF
            if l & 0x8000:
                l -= 0x10000
            addr = (imm << 16) + l
        elif op == 24:
            addr = (imm << 16) | (w2 & 0xFFFF)
        else:
            continue
        for (plo, phi) in PAGES:
            if plo <= addr < phi:
                pairs.append((off2guest(off), hex(addr), "op%d+%d" % (op, k)))
        break

print("=== refs into PNG .rdata pages:", len(pairs))
for p in pairs[:80]:
    print("code @%08X -> data %s (%s)" % p)
