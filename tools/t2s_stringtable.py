#!/usr/bin/env python3
"""t2s_stringtable.py — 把 XUI StringTable.xus（繁体）转换成简体，生成 zh-cn 语言目录。

XUIS 格式（已逆向确认）：
    0x00  "XUIS"
    0x04  u32 LE  版本 (=1)
    0x08  u16 BE  文件大小（字节）
    0x0A  u16 BE  字符串「对」数（值+键名算一对，流里实际条目×2）
    0x0C  字符串流：重复 [u16 BE 长度][UTF-16BE 数据×len]
          每「值、键名」成对出现，键名是 ASCII（TXT_*），值可含 CJK。

繁->简是逐字 1:1 映射，UTF-16 下字节数不变，文件大小字段通常无需变动；
脚本仍会重算并写回，保证幂等正确。

用法:
    python tools/t2s_stringtable.py            # 生成/刷新 game/ui/zh-cn/
    python tools/t2s_stringtable.py --check    # 只校验，不写文件
"""
import struct
import sys
from pathlib import Path

from opencc import OpenCC  # opencc-python-reimplemented

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "game" / "ui" / "zh-cht" / "StringTable.xus"
DST_DIR = ROOT / "game" / "ui" / "zh-cn"
DST = DST_DIR / "StringTable.xus"

# zh-cn 语言目录还需要这些（游戏按 ui\skins\Graphics\<lang>\ 找按键音）
SKIN_SRC = ROOT / "game" / "ui" / "skins" / "Graphics"
SKIN_SOUNDS = ["btn_Select.wav", "Select.wav", "btn_Back.wav", "btn_Focus.wav"]
SKIN_DST = ROOT / "game" / "ui" / "skins" / "Graphics" / "zh-cn"

MAGIC = b"XUIS"


def parse(data: bytes):
    """返回 (字符串列表, 头部信息)。字符串流从 0x0C 开始。"""
    if data[:4] != MAGIC:
        raise SystemExit(f"不是 XUIS 文件: {SRC}")
    version = struct.unpack("<I", data[4:8])[0]
    declared_size = struct.unpack(">H", data[8:10])[0]
    count = struct.unpack(">H", data[10:12])[0]
    strings = []
    off = 0x0C
    while off < len(data):
        (ln,) = struct.unpack(">H", data[off:off + 2])
        off += 2
        raw = data[off:off + ln * 2]
        if len(raw) != ln * 2:
            raise SystemExit(f"字符串流在 0x{off:X} 截断")
        strings.append(raw.decode("utf-16-be"))
        off += ln * 2
    if len(strings) != count * 2:
        raise SystemExit(f"字符串数不符: 声明 {count} 对, 实际 {len(strings)} 条")
    if declared_size != len(data):
        raise SystemExit(f"声明大小 {declared_size} != 实际 {len(data)}")
    return strings, version


def build(strings, version: int) -> bytes:
    body = bytearray()
    for s in strings:
        raw = s.encode("utf-16-be")
        body += struct.pack(">H", len(s)) + raw
    size = 0x0C + len(body)
    if size > 0xFFFF:
        raise SystemExit("文件超过 u16 大小上限")
    head = MAGIC + struct.pack("<I", version) + struct.pack(">H", size) + struct.pack(">H", len(strings))
    return bytes(head) + bytes(body)


def main() -> int:
    check_only = "--check" in sys.argv
    data = SRC.read_bytes()
    strings, version = parse(data)

    cc = OpenCC("t2s")
    converted = [cc.convert(s) for s in strings]

    # 找出有变化的字符串，打印对照（只显示含 CJK 的）
    changed = [(a, b) for a, b in zip(strings, converted) if a != b]
    print(f"共 {len(strings)} 条字符串，繁->简变动 {len(changed)} 条：")
    for a, b in changed:
        print(f"  {a}  ->  {b}")

    out = build(converted, version)
    print(f"\n原始 {len(data)} B -> 转换后 {len(out)} B（应一致）")
    if len(out) != len(data):
        print("警告: 大小不一致，重查 OpenCC 是否有 1->N 映射")

    if check_only:
        return 0

    DST_DIR.mkdir(parents=True, exist_ok=True)
    DST.write_bytes(out)
    print(f"已写出 {DST}")

    # 其余语言资源：zh-cht 目录里除串表外的文件原样复制（目前为空集，留作通用逻辑）
    for f in (SRC.parent).iterdir():
        if f.is_file() and f.name != "StringTable.xus":
            (DST_DIR / f.name).write_bytes(f.read_bytes())
            print(f"已复制 {f.name} -> zh-cn/")

    # 按键音效与皮肤图：游戏按 ui\skins\Graphics\<lang>\ 查找，en-en 目录内容最全，
    # 整目录镜像到 zh-cn（多复制无害，缺了才会读失败）。
    SKIN_EN = SKIN_SRC / "en-en"
    SKIN_DST.mkdir(parents=True, exist_ok=True)
    if SKIN_EN.exists():
        for f in SKIN_EN.iterdir():
            if f.is_file():
                (SKIN_DST / f.name).write_bytes(f.read_bytes())
                print(f"已复制 skins/{f.name} -> zh-cn/")
    for wav in SKIN_SOUNDS:  # 根目录兜底（如 en-en 缺某文件）
        src = SKIN_SRC / wav
        dst = SKIN_DST / wav
        if src.exists() and not dst.exists():
            dst.write_bytes(src.read_bytes())
            print(f"已复制 skins/{wav} -> zh-cn/")

    # skins\<lang>\：每语言的 skin_main.xus 等皮肤资源，同样整目录镜像。
    SKIN_LANG_SRC = SKIN_SRC.parent / "zh-cht"
    SKIN_LANG_DST = SKIN_SRC.parent / "zh-cn"
    if SKIN_LANG_SRC.exists():
        SKIN_LANG_DST.mkdir(parents=True, exist_ok=True)
        for f in SKIN_LANG_SRC.iterdir():
            if f.is_file():
                (SKIN_LANG_DST / f.name).write_bytes(f.read_bytes())
                print(f"已复制 skins(语言目录)/{f.name} -> zh-cn/")

    # screens/<lang>/：语言目录里是各语言的 .xus 画面与图，游戏还会按语言目录
    # 相对查找一批共享图（screens 根目录有，但 zh-cht 子目录缺）。
    # 实测缺失清单（rex_runtime.log NtCreateFile FAILED 汇总）都在 screens 根，
    # 这里整目录镜像 zh-cht 再补齐根目录里的同名缺失文件。
    SCREENS = ROOT / "game" / "ui" / "screens"
    SCR_SRC = SCREENS / "zh-cht"
    SCR_EN = SCREENS / "en-en"
    SCR_DST = SCREENS / "zh-cn"
    if SCR_SRC.exists():
        SCR_DST.mkdir(parents=True, exist_ok=True)
        for f in SCR_SRC.iterdir():
            if f.is_file():
                (SCR_DST / f.name).write_bytes(f.read_bytes())
        copied = len(list(SCR_DST.iterdir()))
        for f in SCREENS.iterdir():
            if f.is_file() and not (SCR_DST / f.name).exists():
                (SCR_DST / f.name).write_bytes(f.read_bytes())
        print(f"已生成 screens/zh-cn/（{copied} 个语言文件 + 共享图补齐，共 "
              f"{len(list(SCR_DST.iterdir()))} 个）")

    return 0


if __name__ == "__main__":
    sys.exit(main())
