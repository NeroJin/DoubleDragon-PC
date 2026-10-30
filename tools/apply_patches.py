#!/usr/bin/env python3
"""在 rexglue codegen 重新生成 generated/ 之后，把本项目手工补丁重新打回去。

codegen 会整体重写 generated/default/*，所以每次跑完 codegen 都要执行一次本脚本。

补丁清单：
  1. DoubleDragon_pch.h
     - 加入调试陷阱 DoubleDragon_TrapDataCall（间接调用目标非法时转储现场）
     - 改造 REX_CALL_INDIRECT_FUNC：未注册目标先转储再 Resolve
  2. DEFINE_REX_FUNC(sub_82186528)（JPEG 标记扫描器）
     - 坏流/死循环判定成立时直接 return 0（DD_TakeDecodeAbort）
  3. DEFINE_REX_FUNC(sub_821497F8)（JPEG 解码器入口）
     - 输入不是 JPEG（非 FFD8）时直接返回 E_FAIL，不进解码器

脚本是幂等的：已经打过就跳过。
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GEN = ROOT / "generated" / "default"

TRAP_FUNC = r'''
//=============================================================================
// 临时诊断陷阱（tools/apply_patches.py 注入）：间接调用目标非法时转储现场。
// 输出 D:/Projects/DoubleDragon/trap_dump.txt（GUI 子系统下 stderr 会丢）。
// 只记录前几次命中，不退出进程。
//=============================================================================
inline void DoubleDragon_TrapDataCall(PPCContext& ctx, uint8_t* base, uint32_t target) {
  static int s_hits = 0;
  static int s_dumped = 0;
  if (s_hits >= 8) return;
  ++s_hits;
  FILE* fp = fopen("D:/Projects/DoubleDragon/trap_dump.txt", "a");
  if (!fp) return;
  auto rd = [&](uint32_t a) -> uint32_t {
    if (a < 0x1000u || a >= 0xE0000000u) return 0xDEADBEEF;
    return __builtin_bswap32(*(volatile uint32_t*)(base + a));
  };
  fprintf(fp, "[TRAP #%d] target=0x%08X lr=0x%08X r1=0x%08X r3=0x%08X "
              "r4=0x%08X r5=0x%08X r29=0x%08X r30=0x%08X r31=0x%08X\n",
          s_hits, target, (uint32_t)ctx.lr, ctx.r1.u32, ctx.r3.u32, ctx.r4.u32,
          ctx.r5.u32, ctx.r29.u32, ctx.r30.u32, ctx.r31.u32);
  if (s_dumped == 0) {
    s_dumped = 1;
    uint32_t obj = ctx.r3.u32;
    if (obj >= 0x1000u && obj < 0xE0000000u) {
      uint32_t vptr = rd(obj);
      fprintf(fp, "  obj=0x%08X vptr=0x%08X\n", obj, vptr);
      if (vptr >= 0x1000u && vptr < 0xE0000000u) {
        fprintf(fp, "  vtable slots:");
        for (int i = 0; i < 24; ++i) fprintf(fp, " %08X", rd(vptr + i * 4));
        fprintf(fp, "\n");
      }
      fprintf(fp, "  obj words:");
      for (int i = 0; i < 32; ++i) fprintf(fp, " %08X", rd(obj + i * 4));
      fprintf(fp, "\n");
    }
    fprintf(fp, "  target bytes:");
    for (int i = 0; i < 8; ++i) fprintf(fp, " %08X", rd(target + i * 4));
    fprintf(fp, "\n");
    fprintf(fp, "  r25=0x%08X r26=0x%08X r27=0x%08X r28=0x%08X\n", ctx.r25.u32,
            ctx.r26.u32, ctx.r27.u32, ctx.r28.u32);
  }
  fflush(fp);
  fclose(fp);
}

'''

MACRO_OLD = r'''#define REX_LOOKUP_FUNC(x, y) \
  (*(PPCFunc**)(x + REX_IMAGE_BASE + REX_IMAGE_SIZE + (u64(u32(y) - REX_CODE_BASE) * 2)))
'''

MACRO_NEW = MACRO_OLD + TRAP_FUNC + r'''
#define REX_CALL_INDIRECT_FUNC(x)                                                          \
  do {                                                                                     \
    REX_PROFILE_INDIRECT_DISPATCH();                                                       \
    uint32_t rex_indirect_target_ = (uint32_t)(x);                                         \
    PPCFunc* rex_indirect_func_;                                                           \
    const bool rex_in_code_ =                                                              \
        (uint32_t)(rex_indirect_target_ - REX_CODE_BASE) <                                 \
        REX_CODE_SIZE + REX_THUNK_RESERVE_SIZE;                                            \
    if (rex_in_code_) [[likely]] {                                                         \
      rex_indirect_func_ = REX_LOOKUP_FUNC(base, rex_indirect_target_);                    \
    } else {                                                                               \
      rex_indirect_func_ = nullptr;                                                        \
    }                                                                                      \
    if (!rex_indirect_func_) [[unlikely]] {                                                \
      /* 代码区内但没有翻译（跳表/计算跳转目标漏扫）或数据区被当函数调用： */              \
      if (rex_in_code_ || rex_indirect_target_ < REX_CODE_BASE)                            \
        DoubleDragon_TrapDataCall(ctx, base, rex_indirect_target_);                        \
      ctx.last_indirect_target = rex_indirect_target_;                                     \
      rex_indirect_func_ = rex::runtime::ResolveIndirectFunction(rex_indirect_target_);    \
    }                                                                                      \
    rex_indirect_func_(ctx, base);                                                         \
  } while (0)
'''

ABORT_ANCHOR = "DEFINE_REX_FUNC(sub_82186528) {\n\tREX_FUNC_PROLOGUE();\n"
ABORT_EXTERN = "extern bool DD_TakeDecodeAbort();\n\n"
ABORT_BODY = """\t// hooks.cpp：坏流/死循环判定成立时直接 return 0（= 没有更多数据），
\t// 让 sub_82186798 干净失败退出，而不是在 marker 循环里永远转。
\tif (DD_TakeDecodeAbort()) {
\t\tctx.r3.s64 = 0;
\t\treturn;
\t}
"""

SKIP_ANCHOR = "DEFINE_REX_FUNC(sub_821497F8) {\n\tREX_FUNC_PROLOGUE();\n"
SKIP_EXTERN = "extern bool DoubleDragon_SkipJpeg(PPCRegister& r4, PPCRegister& r5);\n\n"
SKIP_BODY = """\t// hooks.cpp：非 JPEG 数据不进 JPEG 解码器（否则走 error_exit → SEH throw 未模拟
\t// → 状态被毁 → FATAL）。返回 E_FAIL 让格式嗅探器试下一种格式。
\tif (DoubleDragon_SkipJpeg(ctx.r4, ctx.r5)) {
\t\tctx.r3.s64 = 0x80004005;
\t\treturn;
\t}
"""


def patch_pch() -> bool:
    p = GEN / "DoubleDragon_pch.h"
    s = p.read_text(encoding="utf-8", errors="surrogateescape")
    if "DoubleDragon_TrapDataCall" in s:
        print("  pch: already patched")
        return False
    if MACRO_OLD not in s:
        sys.exit("pch: REX_LOOKUP_FUNC anchor not found")
    start = s.index("#define REX_CALL_INDIRECT_FUNC(x)")
    end = s.index("} while (0)", start) + len("} while (0)\n")
    s = s[:start] + MACRO_NEW + s[end:]
    p.write_text(s, encoding="utf-8", errors="surrogateescape")
    print("  pch: trap + macro patched")
    return True


def patch_func(addr: str, extern_line: str, body: str, marker: str) -> bool:
    anchor = f"DEFINE_REX_FUNC(sub_{addr}) {{\n\tREX_FUNC_PROLOGUE();\n"
    hits = 0
    for p in sorted(GEN.glob("DoubleDragon_recomp*.cpp")):
        s = p.read_text(encoding="utf-8", errors="surrogateescape")
        if anchor not in s:
            continue
        hits += 1
        if marker in s:
            print(f"  {p.name}: already patched")
            continue
        replacement = extern_line + anchor + body
        s = s.replace(anchor, replacement, 1)
        p.write_text(s, encoding="utf-8", errors="surrogateescape")
        print(f"  {p.name}: patched sub_{addr}")
    if hits == 0:
        print(f"  WARNING: sub_{addr} not found in generated code")
    return hits > 0


def patch_orphan_gotos() -> int:
    """把指向"独立函数"的跨函数 goto 改成尾调用。

    codegen 偶尔会在函数 A 里生成 `goto loc_X`，而 X 因为被（我们 sweep 出来的）
    入口拆成了独立函数，A 内部就没有 loc_X 这个标签 -> 编译报
    "use of undeclared label"。

    语义上这就是一次尾跳转：被跳转的块以 blr 结束，等价于调用后立即 return。
    """
    # 所有已生成的函数名（sub_XXXX / FUN_XXXX）
    defined = {}
    for p in GEN.glob("DoubleDragon_recomp*.cpp"):
        s = p.read_text(encoding="utf-8", errors="surrogateescape")
        for name in re.findall(r"^DEFINE_REX_FUNC\((\w+)\) \{", s, re.M):
            m = re.match(r"(?:sub|FUN)_([0-9A-Fa-f]{8})$", name)
            if m:
                defined[int(m.group(1), 16)] = name

    label_def = re.compile(r"^(loc_[0-9A-Fa-f]{8}):", re.M)
    goto_re = re.compile(r"goto (loc_([0-9A-Fa-f]{8}));")
    fixed = 0
    for p in sorted(GEN.glob("DoubleDragon_recomp*.cpp")):
        s = p.read_text(encoding="utf-8", errors="surrogateescape")
        starts = [m.start() for m in re.finditer(r"^DEFINE_REX_FUNC\((\w+)\) \{", s, re.M)]
        if not starts:
            continue
        bounds = list(zip(starts, starts[1:] + [len(s)]))
        new_s = s
        changed = 0
        used_names: list[str] = []
        # 从后往前替换，避免偏移失效
        for start, end in reversed(bounds):
            body = s[start:end]
            missing = set(goto_re.findall(body)) - set(label_def.findall(body))
            for label, hexaddr in missing:
                fname = defined.get(int(hexaddr, 16))
                if not fname:
                    continue  # 找不到对应函数，交给编译报错暴露
                body = body.replace(
                    f"goto {label};",
                    "{ %s(ctx, base); return; } /* 跨函数尾跳转 */" % fname,
                )
                changed += 1
                if fname not in used_names:
                    used_names.append(fname)
            if changed:
                new_s = new_s[:start] + body + new_s[end:]
        if changed:
            # 这些函数定义在别的 .cpp 里，而本文件只 include 了按需裁剪的
            # funcs.N.h，所以要自己补声明，否则编译报 undeclared identifier。
            decls = "".join(f"DECLARE_REX_FUNC({n});\n" for n in used_names
                            if f"DECLARE_REX_FUNC({n});" not in new_s)
            if decls:
                first_nl = new_s.index("\n")
                new_s = new_s[:first_nl + 1] + "\n" + decls + new_s[first_nl + 1:]
            p.write_text(new_s, encoding="utf-8", errors="surrogateescape")
            print(f"  {p.name}: 跨函数 goto -> 尾调用 {changed} 处")
            fixed += changed
    if fixed == 0:
        print("  orphan goto: 无需修补")
    return fixed


def main() -> None:
    if not GEN.exists():
        sys.exit(f"missing {GEN}")
    print("applying project patches to generated code...")
    patch_pch()
    patch_func("82186528", ABORT_EXTERN, ABORT_BODY, "DD_TakeDecodeAbort()")
    patch_func("821497F8", SKIP_EXTERN, SKIP_BODY, "DoubleDragon_SkipJpeg(ctx.r4")
    patch_orphan_gotos()
    print("done")


if __name__ == "__main__":
    main()
