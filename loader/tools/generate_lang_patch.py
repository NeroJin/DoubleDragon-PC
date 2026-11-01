#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
生成 DoubleDragon Loader 的简体中文 .xus 字节补丁（DDLP v1）。

背景
----
游戏侧 lang_redirect 会把内存里的语言目录名 zh-cht 改写成 zh-cn，于是读取
game/ui/zh-cn/ 下的资源。原版 XBLA 包没有 zh-cn，所以 Loader 在启动时重建它。

42 个简体 .xus 与游戏自带繁体 zh-cht **长度完全相等**（繁→简是 1:1 码位替换，
UTF-16 定长），因此可以只用 (offset, len, bytes) 做纯偏移替换还原，无需
Xdelta/bsdiff 这类支持插入删除的二进制 diff。本脚本把 zh-cht→zh-cn 的差异
编码进 patch_v1.bin，Loader 运行时读游戏自带的 zh-cht 作基底、校验 md5 后套用。

补丁格式 DDLP v1（小端）
------------------------
    magic   "DDLP"            4 bytes
    ver     uint16            固定 1
    count   uint16            文件条目数
    每文件:
      pathLen  uint16
      path     utf-8           相对 game/UI 的路径，形如 screens/zh-cn/x.xus
      baseSize uint32         基底(zh-cht)字节数
      baseMd5  16 bytes       基底 md5，运行时校验，不符则退化为繁体
      runCount uint16
      每段:
        offset uint32
        len    uint16
        data   len bytes

用法
----
    # 全部使用默认路径（项目内 game/UI 作简体源，项目内 zh-cht 作基底）
    python generate_lang_patch.py

    # 指定外部原版游戏目录作基底，自定义输出
    python generate_lang_patch.py --base-ui "D:/Games/DoubleDragon(xbla)/game/UI"

    # 查看参数
    python generate_lang_patch.py -h

产物默认写入 loader/src/DoubleDragonLoader/lang_zhcn/：
    patch_v1.bin         给 Loader 解析的二进制补丁
    patch_v1.index.txt   人类可读索引（路径/基底大小/段数/差异字节/基底 md5）
"""
import argparse
import hashlib
import os
import struct
import sys

MAGIC = b"DDLP"
VERSION = 1


def find_repo_root() -> str:
    """由脚本位置推断仓库根：<repo>/loader/tools/generate_lang_patch.py"""
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.dirname(os.path.dirname(here))


def read_manifest_s_files(manifest_path: str):
    """从 manifest.txt 里取出所有 S 类（随补丁还原的 .xus）目标相对路径。"""
    entries = []
    with open(manifest_path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line:
                continue
            parts = line.split("|")
            if len(parts) >= 2 and parts[1] == "S":
                entries.append(parts[0])
    return entries


def diff_runs(base: bytes, target: bytes):
    """返回连续的差异段 [(offset, bytes)]；要求等长。"""
    if len(base) != len(target):
        raise ValueError(f"长度不等: {len(base)} != {len(target)}")
    runs = []
    i, n = 0, len(base)
    while i < n:
        if base[i] != target[i]:
            j = i
            while j < n and base[j] != target[j]:
                j += 1
            runs.append((i, target[i:j]))
            i = j
        else:
            i += 1
    return runs


def build_bin(records):
    """records: [(rel, base_bytes, runs)] -> bytes"""
    buf = bytearray()
    buf += MAGIC
    buf += struct.pack("<HH", VERSION, len(records))
    for rel, base, runs in records:
        pb = rel.encode("utf-8")
        buf += struct.pack("<H", len(pb)) + pb
        buf += struct.pack("<I", len(base))
        buf += hashlib.md5(base).digest()
        buf += struct.pack("<H", len(runs))
        for off, data in runs:
            buf += struct.pack("<IH", off, len(data)) + data
    return bytes(buf)


def parse_bin(blob: bytes):
    """按 C# 侧同样的偏移规则解析，用于往返自检。"""
    if blob[:4] != MAGIC:
        raise ValueError("magic 不符")
    ver, count = struct.unpack_from("<HH", blob, 4)
    if ver != VERSION:
        raise ValueError(f"不支持的版本 {ver}")
    pos, out = 8, []
    for _ in range(count):
        (plen,) = struct.unpack_from("<H", blob, pos)
        pos += 2
        rel = blob[pos:pos + plen].decode("utf-8")
        pos += plen
        (bsize,) = struct.unpack_from("<I", blob, pos)
        pos += 4
        bmd5 = blob[pos:pos + 16]
        pos += 16
        (rc,) = struct.unpack_from("<H", blob, pos)
        pos += 2
        runs = []
        for _ in range(rc):
            off, dl = struct.unpack_from("<IH", blob, pos)
            pos += 6
            runs.append((off, blob[pos:pos + dl]))
            pos += dl
        out.append((rel, bsize, bmd5, runs))
    if pos != len(blob):
        raise ValueError(f"尾部有多余字节: {pos} != {len(blob)}")
    return out


def main(argv=None) -> int:
    root = find_repo_root()
    default_ui = os.path.join(root, "game", "UI")
    default_payload = os.path.join(root, "loader", "src", "DoubleDragonLoader", "lang_zhcn")

    ap = argparse.ArgumentParser(description="生成简体中文 .xus 字节补丁 (DDLP v1)")
    ap.add_argument("--project-ui", default=default_ui,
                    help="简体源所在 game/UI（含 screens/zh-cn 等），默认: %(default)s")
    ap.add_argument("--base-ui", default=None,
                    help="基底繁体所在 game/UI（含 screens/zh-cht），默认与 --project-ui 相同")
    ap.add_argument("--payload-dir", default=default_payload,
                    help="lang_zhcn 目录（读 manifest、写补丁），默认: %(default)s")
    ap.add_argument("--manifest", default=None,
                    help="manifest.txt 路径，默认 <payload-dir>/manifest.txt")
    ap.add_argument("--out", default=None,
                    help="输出补丁路径，默认 <payload-dir>/patch_v1.bin")
    ap.add_argument("--index", default=None,
                    help="输出索引路径，默认 <payload-dir>/patch_v1.index.txt")
    ap.add_argument("--no-verify", action="store_true",
                    help="跳过往返校验（默认开启）")
    args = ap.parse_args(argv)

    project_ui = os.path.abspath(args.project_ui)
    base_ui = os.path.abspath(args.base_ui or args.project_ui)
    payload = os.path.abspath(args.payload_dir)
    manifest = os.path.abspath(args.manifest or os.path.join(payload, "manifest.txt"))
    out_bin = os.path.abspath(args.out or os.path.join(payload, "patch_v1.bin"))
    out_idx = os.path.abspath(args.index or os.path.join(payload, "patch_v1.index.txt"))

    if not os.path.isfile(manifest):
        print(f"[错误] manifest 不存在: {manifest}", file=sys.stderr)
        return 2

    s_files = read_manifest_s_files(manifest)
    if not s_files:
        print("[错误] manifest 中没有 S 类条目", file=sys.stderr)
        return 2

    records, bad_len = [], []
    for rel in s_files:
        target_path = os.path.join(project_ui, rel.replace("/", os.sep))
        base_rel = rel.replace("zh-cn", "zh-cht")
        base_path = os.path.join(base_ui, base_rel.replace("/", os.sep))
        if not os.path.isfile(target_path):
            print(f"[错误] 缺少简体源: {rel}", file=sys.stderr)
            return 2
        if not os.path.isfile(base_path):
            print(f"[错误] 缺少基底 zh-cht: {base_rel}", file=sys.stderr)
            return 2
        target = open(target_path, "rb").read()
        base = open(base_path, "rb").read()
        if len(target) != len(base):
            bad_len.append((rel, len(base), len(target)))
            continue
        records.append((rel, base, diff_runs(base, target), target))

    if bad_len:
        print("[错误] 以下文件长度不等，无法用纯偏移补丁（需改用 Xdelta/bsdiff 等支持插入删除的方案）:",
              file=sys.stderr)
        for rel, bl, tl in bad_len:
            print(f"   {rel}: base={bl} target={tl}", file=sys.stderr)
        return 2

    recs = [(rel, base, runs) for rel, base, runs, _ in records]
    blob = build_bin(recs)
    os.makedirs(os.path.dirname(out_bin), exist_ok=True)
    with open(out_bin, "wb") as f:
        f.write(blob)

    total_base = sum(len(r[1]) for r in recs)
    total_diff = sum(len(d) for _, _, runs in recs for _, d in runs)
    total_runs = sum(len(runs) for _, _, runs in recs)

    with open(out_idx, "w", encoding="utf-8") as f:
        f.write(f"# 补丁索引 v1  files={len(recs)}  bin={len(blob)}B\n")
        f.write("# path\tbaseSize\truns\tdiffBytes\tbaseMd5\n")
        for rel, base, runs in recs:
            d = sum(len(x[1]) for x in runs)
            f.write(f"{rel}\t{len(base)}\t{len(runs)}\t{d}\t{hashlib.md5(base).hexdigest()}\n")
        f.write(f"# total diff bytes = {total_diff}\n")

    print(f"生成 {out_bin}  {len(blob)} 字节 ({len(blob) / 1024:.2f} KB)")
    print(f"索引 {out_idx}")
    print(f"文件 {len(recs)}  差异 {total_diff}/{total_base} 字节 "
          f"({total_diff * 100.0 / total_base:.2f}%)  段 {total_runs}")

    if not args.no_verify:
        parsed = parse_bin(blob)
        by_rel = {p[0]: p for p in parsed}
        ok = fail = 0
        for rel, base, _runs, target in records:
            _rel2, bsize, bmd5, runs2 = by_rel[rel]
            base_again = open(os.path.join(base_ui, rel.replace("zh-cn", "zh-cht").replace("/", os.sep)), "rb").read()
            if len(base_again) != bsize or hashlib.md5(base_again).digest() != bmd5:
                fail += 1
                print("  基底校验失败:", rel, file=sys.stderr)
                continue
            buf = bytearray(base_again)
            for off, data in runs2:
                buf[off:off + len(data)] = data
            if bytes(buf) == target:
                ok += 1
            else:
                fail += 1
                print("  往返失败:", rel, file=sys.stderr)
        print(f"往返校验: OK={ok}  FAIL={fail}")
        if fail:
            return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
