#!/usr/bin/env python3
"""在解压后的 XEX 中定位 PNG 解码器代码（引用 IHDR/IDAT/PLTE 字符串的函数）。

XEX2 解压后：headers 之后即镜像数据，镜像基址通常 0x82000000。
先用已知函数 0x8232E038（mr r3,r5; b ...）验证映射，再搜 xref。
"""
import struct
import sys

XEX = r"D:\Projects\DoubleDragon\dd_dec.xex"
BASE_GUESS = 0x82000000

data = open(XEX, "rb").read()
print("file size", hex(len(data)), "magic", data[:4])

# XEX2 header
magic = data[:4]
if magic != b"XEX2":
    print("not XEX2")
    sys.exit(1)
hdr_size = struct.unpack(">I", data[8:12])[0]
print("header size", hex(hdr_size))

def off2guest(off):
    return BASE_GUESS + (off - hdr_size)

def guest2off(guest):
    return hdr_size + (guest - BASE_GUESS)

# 验证：0x8232E038 处应为 7C A3 2B 78 (mr r3,r5)
g = 0x8232E038
off = guest2off(g)
print("check @%08X off=%X bytes=%s" % (g, off, data[off:off+8].hex()))

# 找字符串位置
targets = [b"IHDR", b"IDAT", b"IEND", b"PLTE", b"\x89PNG"]
str_addrs = {}
for t in targets:
    addrs = []
    start = 0
    while True:
        i = data.find(t, start)
        if i < 0:
            break
        addrs.append(off2guest(i))
        start = i + 1
    str_addrs[t] = addrs
    print(t, len(addrs), [hex(a) for a in addrs[:6]])

# 扫描代码：lis rD, imm16 == 0x4948 ('IH') 之类，找 hi16 等于目标字符串地址高 16 位的
want_hi = set()
for t, addrs in str_addrs.items():
    for a in addrs:
        want_hi.add(a >> 16)

code_lo, code_hi = 0x82080000, 0x82400000
hits = []
for off in range(guest2off(code_lo) & ~3, min(len(data) - 4, guest2off(code_hi)), 4):
    w = struct.unpack(">I", data[off:off+4])[0]
    if (w & 0xFFFF0000) == 0x3C000000:  # lis
        imm = w & 0xFFFF
        if imm in want_hi:
            rd = (w >> 21) & 31
            ga = off2guest(off)
            # 后续 12 条指令内找 addi/ori 同寄存器低 16 位
            for k in range(1, 13):
                o2 = off + k * 4
                if o2 + 4 > len(data):
                    break
                w2 = struct.unpack(">I", data[o2:o2+4])[0]
                op = w2 >> 26
                rd2 = (w2 >> 21) & 31
                ra2 = (w2 >> 16) & 31
                if op == 14 and rd2 == rd and ra2 == rd:  # addi rD,rD,lo
                    lo = w2 & 0xFFFF
                    if lo & 0x8000:
                        lo -= 0x10000
                    addr = (imm << 16) + lo
                    for t, addrs in str_addrs.items():
                        if addr in addrs:
                            hits.append((hex(ga), t.decode('latin1'), hex(addr),
                                         "addi+%d" % k))
                    break
                if op == 24 and rd2 == rd and ra2 == rd:  # ori
                    lo = w2 & 0xFFFF
                    addr = (imm << 16) | lo
                    for t, addrs in str_addrs.items():
                        if addr in addrs:
                            hits.append((hex(ga), t.decode('latin1'), hex(addr),
                                         "ori+%d" % k))
                    break

print("\n=== candidate xrefs to PNG strings ===")
for h in hits:
    print(h)
print("total", len(hits))
