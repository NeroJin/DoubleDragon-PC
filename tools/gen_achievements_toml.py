"""把运行时 dump 的成就原文转成简体，生成 game/achievements.toml 覆盖层。

背景：成就文本来自 xex 的 XDBF 元数据，是原版繁体（label 已经是简体，
描述仍是「獲得/單人遊戲」这类繁体）。SDK 的 AchievementManager 支持
`LoadMetadataFile()` 用 TOML 合并覆盖同名 id 的条目，所以这里离线生成一份
简体 TOML 即可，不需要在运行时做繁简转换。

取原文的方式（一次性）：
  在 achievements_dialog.h 的构造函数里临时遍历 `ListAchievements()`
  把 id/gamerscore/image_id/flags/label/description/unachieved_description
  写到 achievements_dump.txt（格式见本脚本 parse_dump）。

用法：
  python tools/gen_achievements_toml.py [dump.txt] [-o game/achievements.toml]

依赖：opencc（pip install opencc-python-reimplemented）
"""

import argparse
import re
import sys
from pathlib import Path

HEADER_RE = re.compile(
    r"id=(\d+) g=(\d+) image=(\d+) flags=(\d+) icon=(.*)")


def parse_dump(text: str):
    for block in text.split("----"):
        block = block.strip()
        if not block:
            continue
        meta = None
        fields = {}
        for line in block.splitlines():
            if line.startswith("id="):
                meta = HEADER_RE.match(line)
            elif line.startswith(("label=", "desc=", "unach=")):
                k, v = line.split("=", 1)
                fields[k] = v
        if not meta or "label" not in fields:
            continue
        aid, g, image, flags, icon = meta.groups()
        yield dict(id=int(aid), gamerscore=int(g), image_id=int(image),
                   flags=int(flags), icon=icon,
                   label=fields["label"], desc=fields["desc"],
                   unach=fields["unach"])


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("dump", nargs="?", default="build/Release/achievements_dump.txt")
    ap.add_argument("-o", "--out", default="game/achievements.toml")
    args = ap.parse_args()

    import opencc
    cc = opencc.OpenCC("t2s")

    dump_path = Path(args.dump)
    if not dump_path.exists():
        print(f"dump 不存在：{dump_path}（先按脚本头的说明生成一次）", file=sys.stderr)
        return 1

    # opencc 不做词组级修正，个别字手工收口：
    #   「帶著作」→ 「带着作」(著/着 助词)
    fixes = {"带著": "带着", "帶著": "带着"}

    entries = []
    for a in parse_dump(dump_path.read_text(encoding="utf-8")):
        conv = lambda s: _apply_fixes(cc.convert(s), fixes)
        lines = [
            "[[achievements]]",
            f"id = {a['id']}",
            f'label = "{conv(a["label"])}"',
            f'description = "{conv(a["desc"])}"',
            f'unachieved_description = "{conv(a["unach"])}"',
            f"gamerscore = {a['gamerscore']}",
            f"image_id = {a['image_id']}",
            f"flags = {a['flags']}",
        ]
        if a["icon"]:
            lines.append(f'icon_path = "{a["icon"]}"')
        entries.append("\n".join(lines))

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("\n\n".join(entries) + "\n", encoding="utf-8")
    print(f"写入 {out}：{len(entries)} 条成就")
    return 0


def _apply_fixes(s: str, fixes) -> str:
    for old, new in fixes.items():
        s = s.replace(old, new)
    return s


if __name__ == "__main__":
    raise SystemExit(main())
