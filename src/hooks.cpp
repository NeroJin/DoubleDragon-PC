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
#include <rex/system/xmemory.h>

namespace {

uint32_t g_86798_r1 = 0;
uint32_t g_866B0_r1 = 0;
int g_n86798 = 0;
int g_n866B0 = 0;

// 带上限的打印计数器（clamp 防 int 溢出后重新开闸——run32 打了 1300 万行）
bool PrintCap(int* c, int cap) {
  if (*c >= cap) return false;
  ++(*c);
  return true;
}

uint32_t ReadGuestU32(rex::memory::Memory* mem, uint32_t guest, bool* ok) {
  if (guest < 0x1000) {  // 低地址（含 0）实测会绕过 __except 直接崩进程（run31）
    *ok = false;
    return 0;
  }
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

// guest 区间可读性校验（用 xmemory 的堆查询，不触发 AV）。
// run38：扫描器拿到 base=0x40510000（未映射）仍去 lbz → 未处理 AV。
bool GuestRangeReadable(rex::memory::Memory* mem, uint32_t addr, uint32_t size) {
  if (addr < 0x1000) return false;
  if (size == 0) return false;
  if (size > 0x40000000u) return false;
  uint32_t last = addr + size - 1;
  if (last < addr) return false;  // 溢出
  auto* heap = mem->LookupHeap(addr);
  if (!heap) return false;
  if (!mem->LookupHeap(last)) return false;
  auto access = heap->QueryRangeAccess(addr, last);
  return access != rex::memory::PageAccess::kNoAccess;
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
      // 转储缓冲区头 32 字节，判断是否 JPEG（FF D8 开头）。
      // r11 可能为 0（空 base）——低地址读会崩进程，先防护。
      auto* mem = ts->memory();
      if (r11.u32 >= 0x1000) {
        std::fprintf(stderr, "[scan] buf@0x%08X:", r11.u32);
        for (int i = 0; i < 32; ++i) {
          bool ok = false;
          uint32_t b = ReadGuestU32(mem, r11.u32 + i, &ok) & 0xFF;
          std::fprintf(stderr, " %02X", ok ? b : 0);
        }
        std::fprintf(stderr, "\n");
      } else {
        std::fprintf(stderr, "[scan] buf r11=0x%08X too low, skip dump\n", r11.u32);
      }
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
  auto* ts = rex::runtime::ThreadState::Get();
  auto* mem = ts ? ts->memory() : nullptr;
  if (!mem) return;
  bool ok = false;
  uint32_t base = ReadGuestU32(mem, r11.u32 + 0, &ok);
  bool os = false;
  uint32_t size = ReadGuestU32(mem, r11.u32 + 4, &os);
  if (PrintCap(&c, 12)) {
    std::fprintf(stderr, "[refill] fired r3=0x%08X r11=0x%08X -> base=%s0x%08X size=%s0x%08X\n",
                 r3.u32, r11.u32, ok ? "" : "BAD ", base, os ? "" : "BAD ", size);
  }
  // 无效块判定：base 空 / size 0 / 区间不可读 → 视为流结束（r3=0），
  // 让扫描器走各自自带的"无数据 → return"优雅路径。
  bool bad = !ok || base == 0 || !os || size == 0 ||
             !GuestRangeReadable(mem, base, size);
  if (bad) {
    r3.u32 = 0;
    static int f = 0;
    if (PrintCap(&f, 12)) {
      std::fprintf(stderr, "[fix-refill] chunk invalid (base=0x%08X size=0x%08X) -> return 0\n",
                   base, size);
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
      // 中和辅助：清 size / pending base / pending size，
      // 使 866B0 走自带优雅路径（size==0 → refill → ProbeRefillRet 置 r3=0 → return 0）
      auto neutralize = [&](const char* why, uint32_t chunk) {
        bool w1 = false, w2 = false, w3 = false;
        WriteGuestU32(mem, sub + 4, 0, &w1);
        WriteGuestU32(mem, sub + 28, 0, &w2);
        WriteGuestU32(mem, sub + 32, 0, &w3);
        fixed = w1 && w2 && w3;
        if (fixed && c++ < 8) {
          std::fprintf(stderr, "[fix-entry] %s chunk @0x%08X -> neutralize\n", why,
                       chunk);
        }
      };
      if (o4 && o0 && v0 == 0) {
        // v7（run31）：base==0 但 size!=0 时扫描循环会 lbz guest 0 → 崩溃。
        // base==0 一律视为坏块，无条件中和。
        neutralize("null-base", 0);
      } else if (o4 && v4 != 0 && o0 && v0 != 0) {
        bool oh = false;
        if (!head_is_jpeg(v0, &oh)) {
          neutralize("not-JPEG", v0);
        }
      } else if (o4 && v4 == 0 && o0 && v0 != 0) {
        // 当前块空：pending 有数据且非 JPEG → 清掉，防后续搬入
        bool op = false;
        uint32_t pending = ReadGuestU32(mem, sub + 28, &op);
        if (op && pending != 0) {
          bool oh = false;
          if (!head_is_jpeg(pending, &oh)) {
            bool ow = false;
            WriteGuestU32(mem, sub + 28, 0, &ow);
            bool ow2 = false;
            WriteGuestU32(mem, sub + 32, 0, &ow2);
            fixed = ow && ow2;
            if (fixed && c++ < 8) {
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

// ============ v5 探针：定位 BD0→A18 链上 r1 被摧毁的位置 ============
//
// build29 崩溃：sub_82184A18 首个 bctrl 之前的 *(r31+424) 解引用（host+0x14F），
// lr=0x82184D34 未被覆盖 → 崩在 A18 早期。但 r3=0x7018F050（=sub_821497F8 的
// r1+96，工厂对象）要求 497F8 的 r1=0x7018EFF0，而崩溃 r1=0x7018E9F0 对应
// A18 入口 r1=0x7018EA60 —— 帧算术矛盾 → r1 在 BD0 体内某个 bctrl 期间
// 被摧毁（疑似又一处 SEH 截断函数）。v5 全链探针抓现行。

// sub_82186528（JPEG 标记扫描器，与 866B0 同族的另一个解码步骤）入口。
// run38：入口块 {base,size} 中 base 不可读时直接 lbz → 未处理 AV (0x40510000)。
// 与 866B0 的 fix-entry 同规则：坏块就清 size/pending，逼它走 refill →
// [fix-refill] 判无效 → return 0 优雅退出。
void DoubleDragon_ProbeEnter86528(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* mem = ts->memory();
  bool ok = false;
  uint32_t sub = ReadGuestU32(mem, r3.u32 + 24, &ok);  // 同 866B0：流子结构
  static int c = 0;
  if (!ok || sub == 0) return;
  bool ob = false, os = false;
  uint32_t base = ReadGuestU32(mem, sub + 0, &ob);
  uint32_t size = ReadGuestU32(mem, sub + 4, &os);
  if (PrintCap(&c, 12)) {
    std::fprintf(stderr, "[86528] obj=0x%08X sub=0x%08X base=%s0x%08X size=%s0x%08X\n",
                 r3.u32, sub, ob ? "" : "BAD ", base, os ? "" : "BAD ", size);
  }
  if (size == 0) return;  // 空块：它自己的 refill 路径，交给 [fix-refill] 管
  if (!ob || !os || !GuestRangeReadable(mem, base, size)) {
    bool w1 = false, w2 = false, w3 = false;
    WriteGuestU32(mem, sub + 4, 0, &w1);
    WriteGuestU32(mem, sub + 28, 0, &w2);
    WriteGuestU32(mem, sub + 32, 0, &w3);
    if (w1 && w2 && w3) {
      std::fprintf(stderr,
                   "[fix-86528] obj=0x%08X bad chunk base=0x%08X size=0x%08X -> neutralize\n",
                   r3.u32, base, size);
    }
  }
}

// ============ v9 探针：格式分发循环 sub_8214B318 观察 ============
// 该函数把 [0,5,4,1,3,8,7,2,6] 共 9 种格式逐个尝试，返回值 <0 视为失败
// （清理后试下一个），>=0 视为成功。这里给每个解码器入口打点。
namespace {
void TraceFmt(const char* tag, uint32_t r3, uint32_t r4, uint32_t r5) {
  static int c = 0;
  if (!PrintCap(&c, 60)) return;
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  std::fprintf(stderr, "[fmt:%s] r1=0x%08X this=0x%08X buf=0x%08X size=0x%08X\n", tag,
               ctx->r1.u32, r3, r4, r5);
}
}  // namespace

void DoubleDragon_TraceFmt0(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5) { TraceFmt("0", r3.u32, r4.u32, r5.u32); }
void DoubleDragon_TraceFmt1(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5) { TraceFmt("1-JPEG", r3.u32, r4.u32, r5.u32); }
void DoubleDragon_TraceFmt2(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5) { TraceFmt("2", r3.u32, r4.u32, r5.u32); }
void DoubleDragon_TraceFmt3(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5) { TraceFmt("3", r3.u32, r4.u32, r5.u32); }
void DoubleDragon_TraceFmt4(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5) { TraceFmt("4", r3.u32, r4.u32, r5.u32); }
void DoubleDragon_TraceFmt5(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5) { TraceFmt("5", r3.u32, r4.u32, r5.u32); }
void DoubleDragon_TraceFmt6(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5) { TraceFmt("6", r3.u32, r4.u32, r5.u32); }
void DoubleDragon_TraceFmt7(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5) { TraceFmt("7", r3.u32, r4.u32, r5.u32); }
void DoubleDragon_TraceFmt8(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5) { TraceFmt("8", r3.u32, r4.u32, r5.u32); }

// 0x8214B4AC：每个解码器返回后（r3=返回值，r30 将成为判定依据）
void DoubleDragon_TraceFmtRet(PPCRegister& r3) {
  static int c = 0;
  if (!PrintCap(&c, 80)) return;
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  std::fprintf(stderr, "[fmt-ret] r3=0x%08X r31(this)=0x%08X\n", r3.u32, ctx->r31.u32);
}

// 0x8214B560：某个格式成功，跳出循环
void DoubleDragon_TraceFmtOk(PPCRegister& r30) {
  static int c = 0;
  if (!PrintCap(&c, 30)) return;
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  std::fprintf(stderr, "[fmt-OK] ret=0x%08X this=0x%08X\n", r30.u32, ctx->r31.u32);
}

// sub_821497F8 入口（资源加载总入口，帧 1088）
void DoubleDragon_Probe497F8(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (PrintCap(&c, 10)) {
    std::fprintf(stderr,
                 "[497F8 #%d] r1=0x%08X r3=0x%08X r4=0x%08X r5=0x%08X lr=0x%08X\n", c,
                 ctx->r1.u32, r3.u32, ctx->r4.u32, ctx->r5.u32,
                 static_cast<uint32_t>(ctx->lr));
  }
}

// sub_823954B0 返回点（0x8214984C，文件打开尝试）
void DoubleDragon_Probe4984C(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (PrintCap(&c, 20)) {
    std::fprintf(stderr, "[4984C #%d] r1=0x%08X r3=0x%08X\n", c, ctx->r1.u32, r3.u32);
  }
}

// sub_82184950 入口（工厂驱动前半）
void DoubleDragon_Probe84950(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (PrintCap(&c, 10)) {
    std::fprintf(stderr, "[84950 #%d] r1=0x%08X r3=0x%08X lr=0x%08X\n", c, ctx->r1.u32,
                 r3.u32, static_cast<uint32_t>(ctx->lr));
  }
}

// 0x821498EC：sub_82184950 返回后
void DoubleDragon_Probe498EC(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (PrintCap(&c, 20)) {
    std::fprintf(stderr, "[498EC #%d] r1=0x%08X r3=0x%08X\n", c, ctx->r1.u32, r3.u32);
  }
}

// sub_82184BD0 入口（状态机驱动）：dump 工厂对象关键字段
void DoubleDragon_ProbeBD0(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (PrintCap(&c, 40)) {
    std::fprintf(stderr, "[BD0 #%d] r1=0x%08X r3=0x%08X lr=0x%08X\n", c, ctx->r1.u32,
                 r3.u32, static_cast<uint32_t>(ctx->lr));
    DumpObject(ts->memory(), r3.u32);
  }
}

// sub_822099E0 入口（state==202 处理器）
void DoubleDragon_Probe99E0(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (PrintCap(&c, 10)) {
    std::fprintf(stderr, "[99E0 #%d] r1=0x%08X r3=0x%08X lr=0x%08X\n", c, ctx->r1.u32,
                 r3.u32, static_cast<uint32_t>(ctx->lr));
  }
}

// 0x82184C28：BD0 内 202 检查之后（state ∈ {203..} 路径分叉点）
void DoubleDragon_ProbeC28(PPCRegister& r31) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (PrintCap(&c, 40)) {
    bool ok = false;
    uint32_t st = ReadGuestU32(ts->memory(), r31.u32 + 20, &ok);
    std::fprintf(stderr, "[C28 #%d] r1=0x%08X r31=0x%08X state=%s0x%08X\n", c,
                 ctx->r1.u32, r31.u32, ok ? "" : "BAD ", st);
  }
}

// 0x82184C60：BD0 读循环第一个 bctrl 返回点（调 *(r31+8)→vtable[0]）
void DoubleDragon_ProbeC60(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (PrintCap(&c, 30)) {
    std::fprintf(stderr, "[C60 #%d] r1=0x%08X r3=0x%08X r31=0x%08X\n", c, ctx->r1.u32,
                 r3.u32, ctx->r31.u32);
  }
}

// 0x82184C74：BD0 读循环第二个 bctrl 返回点（调 *(+440)→[0]，返回读状态）
void DoubleDragon_ProbeC74(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (PrintCap(&c, 30)) {
    bool ok = false;
    uint32_t iface = 0;
    if (ctx->r31.u32 != 0)
      iface = ReadGuestU32(ts->memory(), ctx->r31.u32 + 440, &ok);
    std::fprintf(stderr, "[C74 #%d] r1=0x%08X r3=0x%08X r31=0x%08X +440=%s0x%08X\n", c,
                 ctx->r1.u32, r3.u32, ctx->r31.u32, ok ? "" : "BAD ", iface);
  }
}

// 0x82184D2C：BD0 调 A18 前一刻（关键观察点：r1 是否已坏）
void DoubleDragon_ProbeD2C(PPCRegister& r3, PPCRegister& r31) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (PrintCap(&c, 40)) {
    auto* mem = ts->memory();
    bool o20 = false, o424 = false;
    uint32_t st = ReadGuestU32(mem, r31.u32 + 20, &o20);
    uint32_t p424 = ReadGuestU32(mem, r31.u32 + 424, &o424);
    std::fprintf(stderr,
                 "[D2C #%d] r1=0x%08X r3=0x%08X r31=0x%08X state=%s0x%08X +424=%s0x%08X\n",
                 c, ctx->r1.u32, r3.u32, r31.u32, o20 ? "" : "BAD ", st,
                 o424 ? "" : "BAD ", p424);
  }
}

// sub_82184A18 入口（崩溃函数）：r1、r3、state、+424
void DoubleDragon_ProbeA18(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (PrintCap(&c, 40)) {
    auto* mem = ts->memory();
    bool o20 = false, o424 = false;
    uint32_t st = ReadGuestU32(mem, r3.u32 + 20, &o20);
    uint32_t p424 = ReadGuestU32(mem, r3.u32 + 424, &o424);
    std::fprintf(stderr,
                 "[A18 #%d] r1=0x%08X r3=0x%08X state=%s0x%08X +424=%s0x%08X\n", c,
                 ctx->r1.u32, r3.u32, o20 ? "" : "BAD ", st, o424 ? "" : "BAD ", p424);
  }
}

// throw 助手 0x821478F0 入口（return=true 短路整个函数体）。
// 生成代码证明：该函数无 epilogue，末尾 bl 0x82395270（RtlUnwind 包装）在真机
// 永不返回；recomp 中 shim 直接返回 → 函数从末尾"掉出去"，r1/r31 全毁
// （build28 崩 86798 lwz r11,420(r31)、build30 崩 A18 的 *(+424) 解引用，同根）。
// return=true 让 throw 变成无害 no-op：调用者（BD0 状态 201 分发、A18 循环等）
// 拿到完好寄存器继续执行 —— 等价于"异常被吞掉"。
void DoubleDragon_FixThrow(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* ctx = ts->context();
  static int c = 0;
  if (PrintCap(&c, 20)) {
    std::fprintf(stderr, "[fix-throw #%d] swallowed r1=0x%08X r3=0x%08X lr=0x%08X\n", c,
                 ctx->r1.u32, r3.u32, static_cast<uint32_t>(ctx->lr));
  }
}

// A18（0x82184A18）第一个接口分发前（0x82184A3C = lwz r11,424(r31)）。
// state 201 的对象 +424 接口块从未安装（=0），原样执行会 lwz r11,0(0) 崩溃。
// run32 教训：指向 +440 的真实接口块 → 其 [8] 非零 → A18 循环死锁。
// run39 教训：scratch 接口块绝不能放进对象自身字段！该对象在
// sub_8214B318 的"9 种格式逐个尝试"循环里被反复复用，+420/+424/+428
// 是后续解码器会读的字段，写坏后最终 bctrl 到数据地址（FATAL 0x40D016D0）。
// 现在改为在 guest 堆里分配一个独立 16 字节块，一次性写入 {blr 空函数, 0, 0}。
uint32_t GetScratchIface(rex::memory::Memory* mem) {
  static uint32_t addr = 0;
  if (addr) return addr;
  auto* heap = mem->LookupHeapByType(false, 0x1000);
  if (!heap) {
    std::fprintf(stderr, "[fix-a18] no virtual heap for scratch\n");
    return 0;
  }
  uint32_t a = 0;
  if (!heap->Alloc(16, 16, static_cast<uint32_t>(rex::memory::AllocationType::kCommit),
                   static_cast<uint32_t>(rex::memory::PageAccess::kReadWrite), false, &a)) {
    std::fprintf(stderr, "[fix-a18] scratch Alloc failed\n");
    return 0;
  }
  bool o1 = false, o2 = false;
  WriteGuestU32(mem, a + 0, 0x823AC730, &o1);  // iface[0] = 已注册的纯 blr 空函数
  WriteGuestU32(mem, a + 8, 0, &o2);           // iface[8] = 0 → 跳过 A18 循环
  if (!o1 || !o2) {
    std::fprintf(stderr, "[fix-a18] scratch write failed @0x%08X\n", a);
    return 0;
  }
  std::fprintf(stderr, "[fix-a18] scratch iface allocated @0x%08X\n", a);
  addr = a;
  return addr;
}

void DoubleDragon_FixA18Iface(PPCRegister& r3) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* mem = ts->memory();
  bool o424 = false;
  uint32_t p424 = ReadGuestU32(mem, r3.u32 + 424, &o424);
  static int c = 0;
  if (PrintCap(&c, 20)) {
    std::fprintf(stderr, "[fix-a18 #%d] obj=0x%08X +424=%s0x%08X\n", c, r3.u32,
                 o424 ? "" : "BAD ", p424);
  }
  if (!o424 || p424 != 0) return;
  uint32_t scratch = GetScratchIface(mem);
  if (!scratch) return;
  bool w = false;
  WriteGuestU32(mem, r3.u32 + 424, scratch, &w);
  if (w) {
    static int c2 = 0;
    if (PrintCap(&c2, 20)) {
      std::fprintf(stderr, "[fix-a18] obj=0x%08X +424 := scratch 0x%08X\n", r3.u32,
                   scratch);
    }
  }
}

// A18 循环头（0x82184A70，loc_82184A70）：死循环保险。
// 循环退出依赖 *(*(obj+424)+8)==0，若该槽恒非零则无限循环（run32 实测）。
// 同一对象循环超过阈值 → 直接把 iface[8] 清零强制退出。
void DoubleDragon_GuardA18Loop(PPCRegister& r31) {
  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) return;
  auto* mem = ts->memory();
  static uint32_t last_obj = 0;
  static int iter = 0;
  if (r31.u32 != last_obj) {
    last_obj = r31.u32;
    iter = 0;
  }
  if (++iter < 2000) return;
  bool oIF = false;
  uint32_t iface = ReadGuestU32(mem, r31.u32 + 424, &oIF);
  if (oIF && iface >= 0x1000) {
    bool w = false;
    WriteGuestU32(mem, iface + 8, 0, &w);
    if (w) {
      std::fprintf(stderr, "[guard-a18] loop stuck obj=0x%08X iface=0x%08X -> force exit\n",
                   r31.u32, iface);
    }
  }
  iter = 0;
}
