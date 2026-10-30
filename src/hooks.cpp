// DoubleDragon - 调试探针（临时，用完移除）
//
// v4：修正 v3 的字段读 bug（ReadGuestU64+bswap 后低 32 位是 addr+4 的字段，
// v3 的 DumpObject 全部错位 4 字节，导致"对象半初始化"的错误结论）。
// v4 全部字段读改用 ReadGuestU32（逐字段、正确）。
//
// 已证事实（v3，剔除错位读后）：
//   - 对象 0x7018EAC0 构造正常：+0=0x7018ECA0(栈上函数表), +16=1,
//     +24=0x400D4230(有效), +444=0x400D4160(已安装)。
//   - 821846B8→82207C30→86798→866B0 链各入口帧一致（0x7018E9F0/E980/E910/E890）。
//   - 866B0 内部的 [8677C]/[rest] 探针未命中、86798 的 [E4] 未命中
//     → 866B0 从未正常返回。
//   - 崩溃：ctx.r31=0，r1=0x7018EAF0（不属于本链任何帧），host PC 归属 86798+0x2C6。
// v4 目标：在 866B0 的两个 bctrl 返回点架探针，确定它死在哪一步。

#include <cstdint>
#include <cstdio>
#include <cstring>

#include <windows.h>

#include <rex/memory.h>
#include <rex/ppc/func.h>
#include <rex/system/thread_state.h>

namespace {

uint32_t g_86798_r1 = 0;
uint32_t g_866B0_r1 = 0;
int g_n86798 = 0;
int g_n866B0 = 0;

uint32_t ReadGuestU32(rex::memory::Memory* mem, uint32_t guest, bool* ok) {
  __try {
    const uint8_t* p = mem->TranslateVirtual<const uint8_t*>(guest);
    uint32_t be = 0;
    ::memcpy(&be, p, sizeof(be));
    *ok = true;
    return __builtin_bswap32(be);
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    *ok = false;
    return 0;
  }
}

void WriteGuestU32(rex::memory::Memory* mem, uint32_t guest, uint32_t value, bool* ok) {
  __try {
    uint8_t* p = mem->TranslateVirtual<uint8_t*>(guest);
    uint32_t be = __builtin_bswap32(value);
    ::memcpy(p, &be, sizeof(be));
    *ok = true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    *ok = false;
  }
}

// 对象关键字段快照（U32 逐字段，正确）。
void DumpObject(rex::memory::Memory* mem, uint32_t obj) {
  static const uint32_t offs[] = {0, 4, 8, 12, 16, 20, 24, 28, 32, 416,
                                  420, 424, 428, 440, 444, 448};
  std::fprintf(stderr, "    obj@0x%08X:", obj);
  for (uint32_t off : offs) {
    bool ok = false;
    uint32_t v = ReadGuestU32(mem, obj + off, &ok);
    std::fprintf(stderr, " +%u=%s0x%08X", off, ok ? "" : "BAD ", v);
  }
  std::fputc('\n', stderr);
}

}  // namespace

// ============ 修复探针：JPEG 扫描器空缓冲优雅退出 ============
//
// 根因链（v25 实测）：
//   866B0 的扫描子结构 *(obj+24) 的 {base,size} 为 0（数据未加载），
//   计数器变为 0xFFFFFFFF，从 guest 0 开始扫垃圾页 → 走错误路径 →
//   调 0x821478F0（SEH throw 助手，内部 RtlUnwind）。真机上 RtlUnwind
//   永不正常返回（函数本就没有 epilogue），recomp 未模拟 SEH 展开，
//   导致"返回"时 r1/r29/r31 全是垃圾 → 866B0 epilogue 从错误的 r1
//   恢复 r31=0 → 崩溃于 lwz r11,420(r31)。
//
// 修复：让空缓冲走 866B0 自带的优雅路径（计数耗尽 → 回调返回 0 → return 0）。
//   Hook A（0x821866FC 扫描循环头）：chunk base==0 时把计数器 r10 置 1，
//     使本次字节读完立即触发回调。
//   Hook B（0x82147970 回调 blr 前）：重载后的 base 仍为 0 时把 r3 置 0，
//     866B0 据此走 li r3,0 / return 0。
void DoubleDragon_ProbeScanLoop(PPCRegister& r10, PPCRegister& r11, PPCRegister& r31) {
  auto* ts = rex::runtime::ThreadState::Get();
  static int c = 0;
  if (c++ < 5) {
    std::fprintf(stderr, "[scan] fired r10=0x%08X r11=0x%08X r31=0x%08X ts=%d\n", r10.u32,
                 r11.u32, r31.u32, ts ? 1 : 0);
    if (ts) {
      // 转储缓冲区头 32 字节，判断是否 JPEG（FF D8 开头）
      auto* mem = ts->memory();
      std::fprintf(stderr, "[scan] buf@0x%08X:", r11.u32);
      for (int i = 0; i < 32; ++i) {
        bool ok = false;
        uint32_t b = ReadGuestU32(mem, r11.u32 + i, &ok) & 0xFF;
        std::fprintf(stderr, " %02X", ok ? b : 0);
      }
      std::fprintf(stderr, "\n");
    }
  }
  if (!ts) return;
  bool ok = false;
  uint32_t base = ReadGuestU32(ts->memory(), r31.u32 + 0, &ok);
  if (ok && base == 0) {
    r10.u32 = 1;  // 本次字节后立即耗尽计数 → 进入回调
    static int f = 0;
    if (f++ < 5) {
      std::fprintf(stderr, "[fix-scan] null chunk @867FC, scan=0x%08X -> force refill\n",
                   r11.u32);
    }
  }
}

void DoubleDragon_ProbeRefillRet(PPCRegister& r3, PPCRegister& r11) {
  static int c = 0;
  if (c++ < 5) {
    std::fprintf(stderr, "[refill] fired r3=0x%08X r11=0x%08X\n", r3.u32, r11.u32);
  }
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  bool ok = false;
  uint32_t base = ReadGuestU32(ts->memory(), r11.u32 + 0, &ok);
  if (ok && base == 0) {
    r3.u32 = 0;  // 无更多数据 → 866B0 走 li r3,0 优雅返回
    static int f = 0;
    if (f++ < 5) {
      std::fprintf(stderr, "[fix-refill] chunk still empty -> return 0\n");
    }
  }
}

// ---- 链路入口探针 ----

void DoubleDragon_ProbeEnter821846B8(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (c++ < 5) {
    std::fprintf(stderr, "[821846B8 #%d] r1=0x%08X r3=0x%08X lr=0x%08X\n", c,
                 ctx->r1.u32, r3.u32, static_cast<uint32_t>(ctx->lr));
    DumpObject(ts->memory(), r3.u32);
  }
}

void DoubleDragon_ProbeEnter82207C30(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (c++ < 5) {
    std::fprintf(stderr, "[82207C30 #%d] r1=0x%08X r3=0x%08X lr=0x%08X\n", c,
                 ctx->r1.u32, r3.u32, static_cast<uint32_t>(ctx->lr));
  }
}

void DoubleDragon_ProbeEnter8298(PPCRegister& r1, PPCRegister& r3, PPCRegister& r31) {
  g_86798_r1 = r1.u32;
  ++g_n86798;
  auto* ts = rex::runtime::ThreadState::Get();
  if (g_n86798 <= 10 && ts) {
    auto* ctx = ts->context();
    std::fprintf(stderr, "[86798 #%d] r1=0x%08X r3=0x%08X r31=0x%08X lr=0x%08X\n",
                 g_n86798, ctx->r1.u32, ctx->r3.u32, ctx->r31.u32,
                 static_cast<uint32_t>(ctx->lr));
  }
}

// 866B0 入口（__savegprlr_26 之前）：r1、r3、r31、*(r3+24)（将成为 866B0 的 r31）
// 及子结构 {0,4,12} 三个字。
void DoubleDragon_ProbeEnter866B0(PPCRegister& r3, PPCRegister& r31) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  g_866B0_r1 = ctx->r1.u32;
  ++g_n866B0;
  if (g_n866B0 <= 10) {
    auto* mem = ts->memory();
    bool ok = false;
    uint32_t sub = ReadGuestU32(mem, r3.u32 + 24, &ok);
    std::fprintf(stderr, "[866B0 #%d] r1=0x%08X r3=0x%08X r31=0x%08X *(r3+24)=%s0x%08X\n",
                 g_n866B0, ctx->r1.u32, r3.u32, r31.u32, ok ? "" : "BAD ", sub);
    if (ok && sub != 0) {
      bool o0 = false, o4 = false, o12 = false;
      uint32_t v0 = ReadGuestU32(mem, sub, &o0);
      uint32_t v4 = ReadGuestU32(mem, sub + 4, &o4);
      uint32_t v12 = ReadGuestU32(mem, sub + 12, &o12);
      std::fprintf(stderr, "    sub@0x%08X: [0]=%s0x%08X [4]=%s0x%08X [12]=%s0x%08X\n",
                   sub, o0 ? "" : "BAD ", v0, o4 ? "" : "BAD ", v4, o12 ? "" : "BAD ",
                   v12);
      // ==== 修复：非 JPEG 数据（PNG 等）不应走 SEH throw（recomp 未模拟 unwind）。
      // 真机上 866B0 的 FFD8 校验失败会 throw → RtlUnwind 被上层 catch 转其他
      // 加载路径；recomp 未模拟 SEH，throw 助手(0x821478F0)无 epilogue 且
      // "返回"会摧毁调用者寄存器。此处让非 JPEG 数据走 866B0 自带的优雅
      // 路径：清空当前/待装块 → refill 回调装 {0,0} → [fix-refill] 置 r3=0
      // → 866B0 走 li r3,0 优雅返回（等价于真机 catch 后的"本轮放弃"）。
      auto head_is_jpeg = [&](uint32_t addr, bool* okh) {
        uint32_t h = ReadGuestU32(mem, addr, okh);
        return (*okh) && ((h >> 16) == 0xFFD8);
      };
      bool fixed = false;
      static int c = 0;
      if (o4 && v4 != 0 && o0 && v0 != 0) {
        bool oh = false;
        if (!head_is_jpeg(v0, &oh)) {
          bool w1 = false, w2 = false;
          WriteGuestU32(mem, sub + 4, 0, &w1);
          WriteGuestU32(mem, sub + 28, 0, &w2);
          fixed = w1 && w2;
          if (fixed && c++ < 5) {
            std::fprintf(stderr,
                         "[fix-entry] current chunk @0x%08X not JPEG -> neutralize\n",
                         v0);
          }
        }
      } else if (o4 && v4 == 0) {
        bool op = false;
        uint32_t pending = ReadGuestU32(mem, sub + 28, &op);
        if (op && pending != 0) {
          bool oh = false;
          if (!head_is_jpeg(pending, &oh)) {
            bool ow = false;
            WriteGuestU32(mem, sub + 28, 0, &ow);
            fixed = ow;
            if (fixed && c++ < 5) {
              std::fprintf(stderr,
                           "[fix-entry] pending chunk @0x%08X not JPEG -> neutralize\n",
                           pending);
            }
          }
        }
      }
      (void)fixed;
    }
  }
}

// 866B0 内第一个 bctrl 返回点（0x821866E0，调 *(r31+12)，r3=obj）。
void DoubleDragon_Probe866E0(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (c++ < 10) {
    std::fprintf(stderr, "[866E0 #%d] r1=0x%08X r3=0x%08X r31=0x%08X\n", c, ctx->r1.u32,
                 ctx->r3.u32, ctx->r31.u32);
  }
}

// 866B0 内第二个 bctrl 返回点（0x82186720，调 *(r31+12)，r3=r29）。
void DoubleDragon_Probe86720(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (c++ < 10) {
    std::fprintf(stderr, "[86720 #%d] r1=0x%08X r3=0x%08X r31=0x%08X\n", c, ctx->r1.u32,
                 ctx->r3.u32, ctx->r31.u32);
  }
}

// 866B0 内第三个 bctrl 返回点（0x8218677C，调 *( *(r29) )，vtable 调用）。
void DoubleDragon_Probe8677C(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (c++ < 10) {
    std::fprintf(stderr, "[8677C #%d] r1=0x%08X r3=0x%08X r29=0x%08X r31=0x%08X\n", c,
                 ctx->r1.u32, ctx->r3.u32, ctx->r29.u32, ctx->r31.u32);
  }
}

// __restgprlr_26 入口：只在 866B0 返回那一次打印。
void DoubleDragon_ProbeRest(PPCRegister& r1) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  if (ctx->r1.u32 != g_866B0_r1) return;
  std::fprintf(stderr, "[rest] r1=0x%08X r31_before_ld=0x%08X\n", ctx->r1.u32,
               ctx->r31.u32);
}

// 86798 内 866B0 返回后（loc_821867E4）。
void DoubleDragon_ProbeE4(PPCRegister& r3, PPCRegister& r31) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  if (ctx->r1.u32 + 128 != g_86798_r1) return;
  std::fprintf(stderr, "[E4] r3=0x%08X r31=0x%08X r1=0x%08X (86798#%d 866B0#%d)\n",
               ctx->r3.u32, ctx->r31.u32, ctx->r1.u32, g_n86798, g_n866B0);
}
