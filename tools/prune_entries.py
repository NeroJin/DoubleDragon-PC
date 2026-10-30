"""逐个验证"运行时拼出来的短 thunk"，只保留不会破坏函数划分的入口。

背景：这类地址（如 0x820EE688 的 `lis + addi + b` 三指令跳板）不会出现在
.pdata、也没有静态 call 指向，codegen 不会生成；但把它们写进 manifest 当函数
入口时，如果地址其实是某个函数体中间的位置，就会把原函数切断，codegen 随即
报一堆 `Unresolved branch`。

所以：一个一个试。加进去后 codegen 干净（退出码 0、没有 Unresolved）就保留，
否则撤回。顺序按地址升序，前面的结果会影响后面的划分，因此是累积验证。
"""
from __future__ import annotations

import importlib.util
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MAN = ROOT / "DoubleDragon_manifest.toml"
REXGLUE = r"D:/ReverseTools/ReXGlueSDK/bin/rexglue.exe"
MARKER = "# 调试探针（临时）"
BEGIN = "# >>> 验证通过的 thunk 入口（tools/prune_entries.py 生成）"
END = "# <<< 验证通过的 thunk 入口"

_spec = importlib.util.spec_from_file_location("gce", ROOT / "tools/gen_callback_entries.py")
gce = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(gce)


def thunk_len(data, a, limit=16):
    from capstone import Cs, CS_ARCH_PPC, CS_MODE_32, CS_MODE_BIG_ENDIAN

    md = Cs(CS_ARCH_PPC, CS_MODE_32 | CS_MODE_BIG_ENDIAN)
    pos = a
    for i in range(limit):
        off = gce.IMG_OFF + (pos - gce.IMG_BASE)
        ins = list(md.disasm(data[off:off + 4], pos))
        if not ins:
            return None
        pos += 4
        if ins[0].mnemonic in ("b", "blr", "bctr", "rfi"):
            return i + 1
    return None


def run_codegen() -> tuple[int, bool]:
    p = subprocess.run([REXGLUE, "codegen", "DoubleDragon_manifest.toml", "--ignore-stamp"],
                       cwd=str(ROOT), capture_output=True, text=True, errors="ignore")
    out = (p.stdout or "") + (p.stderr or "")
    return p.returncode, ("Unresolved" in out or "Failed" in out)


def strip_block(text: str) -> str:
    if BEGIN in text:
        a = text.index(BEGIN)
        b = text.index(END) + len(END)
        text = text[:a] + text[b:]
    return text


def write_block(addrs: list[int], text: str) -> str:
    text = strip_block(text)
    lines = [BEGIN, ""] + [f'0x{a:08X} = {{ name = "FUN_{a:08X}" }}' for a in addrs] + ["", END, ""]
    i = text.index(MARKER)
    return text[:i] + "\n".join(lines) + text[i:]


def main() -> None:
    data = gce.XEX.read_bytes()
    reg = (ROOT / "generated/default/DoubleDragon_register.cpp").read_text(encoding="utf-8", errors="ignore")
    declared = set(int(x, 16) for x in re.findall(r"SetFunction\(0x([0-9A-Fa-f]{8})", reg))
    cands = [c for c in gce.valid_starts(data, gce.scan(data)) if c not in declared]
    cands = [c for c in sorted(cands) if thunk_len(data, c)]
    print(f"待验证候选 {len(cands)} 个")

    base = strip_block(MAN.read_text(encoding="utf-8"))
    MAN.write_text(base, encoding="utf-8", newline="\n")
    rc, bad = run_codegen()
    print(f"基线 codegen: exit={rc} 有问题={bad}")

    kept: list[int] = []
    for idx, a in enumerate(cands):
        MAN.write_text(write_block(kept + [a], base), encoding="utf-8", newline="\n")
        rc, bad = run_codegen()
        if rc == 0 and not bad:
            kept.append(a)
        if (idx + 1) % 20 == 0 or idx + 1 == len(cands):
            print(f"  [{idx + 1}/{len(cands)}] 保留 {len(kept)}", flush=True)

    MAN.write_text(write_block(kept, base), encoding="utf-8", newline="\n")
    rc, bad = run_codegen()
    print(f"最终 codegen: exit={rc} 有问题={bad}；保留入口 {len(kept)} 个")
    print("保留:", " ".join(f"{a:08X}" for a in kept))


if __name__ == "__main__":
    main()
