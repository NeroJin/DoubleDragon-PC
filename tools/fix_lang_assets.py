#!/usr/bin/env python3
"""修复 UI 语言资源布局（game/ 是外部素材，不入库，重新解包后需要重跑）。

背景：
  game/ui/<lang>/StringTable.xus 才是游戏真正读的串表（XGetLanguage 选目录），
  game/ui/screens/<lang>/ 下的 .xus/png 其实各语言完全相同（文本都来自串表）。

本机素材里的两个坑：
  1. game/ui/en-en/StringTable.xus 是 de-de 的副本（内容为德语），
     所以默认（英文）界面显示的是德语文案。
     -> 用 game/ui/StringTable.xus（确认为英文）覆盖它。
  2. 部分语言目录缺 5 张通用图片（legal_bg/legal_empire/legal_text/
     LargeDDLogo/razorworks_logo），运行时会反复读取失败再回退。
     -> 从 en-en 补齐。

用法：
  python tools/fix_lang_assets.py [--dry-run]
"""

from __future__ import annotations

import hashlib
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UI = ROOT / "game" / "ui"

LANGS = [
    "en-en", "de-de", "fr-fr", "it-it", "es-es",
    "pt-pt", "ja-jp", "ko-kr", "zh-cht",
]

# 这些文件在各语言目录里内容一致，只存在于一部分目录中
NEUTRAL_SCREENS = [
    "legal_bg.png",
    "legal_empire.png",
    "legal_text.png",
    "LargeDDLogo.png",
    "razorworks_logo.png",
]


def md5(path: Path) -> str:
    return hashlib.md5(path.read_bytes()).hexdigest()


def fix_string_tables(dry: bool) -> None:
    root_table = UI / "StringTable.xus"
    if not root_table.is_file():
        print("  ! 缺少 game/ui/StringTable.xus（英文串表），跳过串表修复")
        return

    english = md5(root_table)
    german = md5(UI / "de-de" / "StringTable.xus") if (UI / "de-de" / "StringTable.xus").is_file() else None
    if german and english == german:
        print("  ! game/ui/StringTable.xus 与 de-de 相同，无法判定英文源，跳过")
        return

    for lang in LANGS:
        target = UI / lang / "StringTable.xus"
        if not target.is_file():
            print(f"  {lang}: 缺 StringTable.xus，从英文表补")
            if not dry:
                shutil.copy2(root_table, target)
            continue
        h = md5(target)
        if h == english:
            continue
        if lang == "en-en":
            print(f"  en-en: 串表不是英文（md5={h[:8]}），用英文表覆盖")
            if not dry:
                shutil.copy2(root_table, target)
        else:
            print(f"  {lang}: 串表与英文不同（md5={h[:8]}），视为该语言自有版本，保留")


def fix_neutral_screens(dry: bool) -> None:
    src_dir = UI / "screens" / "en-en"
    if not src_dir.is_dir():
        print("  ! 缺少 game/ui/screens/en-en，跳过图片补齐")
        return
    for lang in LANGS:
        d = UI / "screens" / lang
        if not d.is_dir():
            continue
        for name in NEUTRAL_SCREENS:
            src = src_dir / name
            dst = d / name
            if not src.is_file() or dst.is_file():
                continue
            print(f"  {lang}: 补 {name}")
            if not dry:
                shutil.copy2(src, dst)


def main() -> None:
    dry = "--dry-run" in sys.argv
    if not UI.is_dir():
        sys.exit(f"missing {UI}")
    print("string tables:")
    fix_string_tables(dry)
    print("neutral screens:")
    fix_neutral_screens(dry)
    print("done" + (" (dry-run)" if dry else ""))


if __name__ == "__main__":
    main()
