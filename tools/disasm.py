"""极简 PPC 反汇编助手：从 dd_dec.xex 里按 guest 地址读字节并用 capstone 反汇编。

用法:
    python tools/disasm.py 0x820EE610 30        # 从该地址反汇编 30 条
    python tools/disasm.py 0x820E4400 0x820E4450
"""
import struct
import sys
from pathlib import Path

from capstone import Cs, CS_ARCH_PPC, CS_MODE_32, CS_MODE_BIG_ENDIAN

XEX = Path(__file__).resolve().parent.parent / "dd_dec.xex"
IMG_BASE = 0x82000000
IMG_OFF = 0x2000


def read(addr: int, size: int) -> bytes:
    data = XEX.read_bytes()
    off = IMG_OFF + (addr - IMG_BASE)
    if off < 0 or off + size > len(data):
        raise SystemExit(f"0x{addr:08X} out of image")
    return data[off:off + size]


def disasm(start: int, count: int = 40):
    md = Cs(CS_ARCH_PPC, CS_MODE_32 | CS_MODE_BIG_ENDIAN)
    code = read(start, count * 4)
    for ins in md.disasm(code, start):
        print(f"0x{ins.address:08X}  {ins.bytes.hex():8s}  {ins.mnemonic:8s} {ins.op_str}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    a = int(sys.argv[1], 0)
    if len(sys.argv) > 2 and sys.argv[2].startswith("0x"):
        b = int(sys.argv[2], 0)
        disasm(a, (b - a) // 4)
    else:
        disasm(a, int(sys.argv[2]) if len(sys.argv) > 2 else 40)
