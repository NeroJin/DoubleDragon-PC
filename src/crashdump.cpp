// DoubleDragon - ReXGlue Recompiled Project
//
// 开发期辅助：guest 崩溃时打印 PPC 调用栈。
//
// 动机：recomp 出问题时的日志只有 "Unhandled guest access violation: read of guest 0x...",
// 不知道是哪条 PPC 指令、被谁调用。这里在未处理异常时取当前线程的 PPCContext，
// 沿 r1 栈回溯（PPC ABI：帧首 4 字节是上一帧 sp，sp+4 是保存的 LR），把 guest 调用链打出来，
// 地址可直接对照 generated/ 里的 sub_XXXXXXXX。
//
// 正常情况下零开销：只装一个过滤器，不参与执行路径。

#include <cstdint>
#include <cstdio>
#include <cstring>

#include <windows.h>

#include <rex/memory.h>
#include <rex/ppc/func.h>
#include <rex/system/thread_state.h>

// 由 generated/default/DoubleDragon_init.cpp 定义，{guest, host} 表，{0,nullptr} 结束。
extern PPCFuncMapping PPCFuncMappings[];

namespace {

bool g_installed = false;

// guest 内存是 big-endian 存放的，读出来要翻转。
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

LONG WINAPI GuestCrashFilter(EXCEPTION_POINTERS* info) {
  const EXCEPTION_RECORD* rec = info->ExceptionRecord;
  const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
  const auto fault = reinterpret_cast<uintptr_t>(rec->ExceptionAddress);
  std::fprintf(stderr, "\n===== GUEST CRASH =====\n");
  std::fprintf(stderr, "host: code=0x%08lX fault=%p base=%p rva=0x%llX\n",
               static_cast<unsigned long>(rec->ExceptionCode), rec->ExceptionAddress,
               reinterpret_cast<void*>(base),
               static_cast<unsigned long long>(fault - base));

  auto* ts = rex::runtime::ThreadState::Get();
  if (!ts) {
    std::fprintf(stderr, "no ThreadState bound on this thread (not a guest thread?)\n");
    return EXCEPTION_EXECUTE_HANDLER;
  }
  auto* ctx = ts->context();
  auto* mem = ts->memory();
  std::fprintf(stderr, "guest: thread=%u lr=0x%08X r1=0x%08X r3=0x%08X r4=0x%08X\n",
               ts->thread_id(), static_cast<uint32_t>(ctx->lr),
               static_cast<uint32_t>(ctx->r1.u64), ctx->r3.u32, ctx->r4.u32);
  std::fprintf(stderr, "       r5=0x%08X r6=0x%08X r7=0x%08X r8=0x%08X r9=0x%08X r10=0x%08X\n",
               ctx->r5.u32, ctx->r6.u32, ctx->r7.u32, ctx->r8.u32, ctx->r9.u32, ctx->r10.u32);

  // 反查崩溃落在哪个重编译函数：遍历 {guest, host} 表，取 host 不大于崩溃地址的最近一项。
  uintptr_t best_host = 0;
  uint64_t best_guest = 0;
  for (auto* m = PPCFuncMappings; m->host != nullptr; ++m) {
    const auto h = reinterpret_cast<uintptr_t>(m->host);
    if (h <= fault && h > best_host) {
      best_host = h;
      best_guest = m->guest;
    }
  }
  if (best_host != 0) {
    std::fprintf(stderr, "in-function: sub_%08llX (host=0x%llX) + 0x%llX\n",
                 static_cast<unsigned long long>(best_guest),
                 static_cast<unsigned long long>(best_host),
                 static_cast<unsigned long long>(fault - best_host));
  } else {
    std::fprintf(stderr, "in-function: <no mapping covers fault address>\n");
  }

  uint32_t sp = static_cast<uint32_t>(ctx->r1.u64);
  for (int i = 0; i < 24 && sp != 0; ++i) {
    bool ok_prev = false, ok_lr = false;
    uint32_t prev = ReadGuestU32(mem, sp, &ok_prev);
    uint32_t saved_lr = ReadGuestU32(mem, sp + 4, &ok_lr);
    std::fprintf(stderr, "  #%02d sp=0x%08X prev_sp=0x%08X saved_lr=0x%08X%s\n", i, sp, prev,
                 saved_lr, (ok_prev && ok_lr) ? "" : "  <unreadable>");
    if (!ok_prev || !ok_lr) break;
    if (prev <= sp || (prev - sp) > 0x20000) break;
    sp = prev;
  }

  // 宿主调用栈：配合 /Zi + /DEBUG 生成的 pdb，可用 llvm-symbolizer 直接反查函数名。
  {
    void* frames[24] = {};
    const USHORT n = CaptureStackBackTrace(0, 24, frames, nullptr);
    std::fprintf(stderr, "  -- host backtrace (rva) --\n");
    for (USHORT i = 0; i < n; ++i) {
      const auto a = reinterpret_cast<uintptr_t>(frames[i]);
      std::fprintf(stderr, "  [%02u] rva=0x%llX\n", i, static_cast<unsigned long long>(a - base));
    }
  }

  // 栈帧链断了（例如线程入口/宿主回调）时，直接扫栈找落在代码段里的返回值。
  {
    const uint32_t kCodeBase = 0x82080000, kCodeEnd = 0x823AD7DC;
    const uint32_t start = static_cast<uint32_t>(ctx->r1.u64);
    std::fprintf(stderr, "  -- scan stack for code addrs --\n");
    for (uint32_t off = 0; off < 0x400; off += 4) {
      bool ok = false;
      uint32_t v = ReadGuestU32(mem, start + off, &ok);
      if (!ok) break;
      if (v >= kCodeBase && v < kCodeEnd) {
        std::fprintf(stderr, "  +0x%03X: 0x%08X\n", off, v);
      }
    }
  }
  std::fprintf(stderr, "===== END CRASH =====\n");
  std::fflush(stderr);
  return EXCEPTION_EXECUTE_HANDLER;
}

}  // namespace

void InstallGuestCrashDump() {
  if (g_installed) return;
  g_installed = true;
  SetUnhandledExceptionFilter(GuestCrashFilter);
}
