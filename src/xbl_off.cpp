// DoubleDragon - Xbox LIVE 联网观察/拦截层
//
// 背景：游戏会频繁尝试联网（登录 Xbox Live、排行榜、多人会话），在 360 服务器
// 早已关闭的今天这些尝试只会换来错误弹窗。本模块把游戏导入映射表
// （DoubleDragon_init.cpp，由 tools/apply_patches.py 在 codegen 后改写）里选定的
// XAM 调用改道到本文件，以便：
//   1) 记录游戏到底在弹什么框（标题/正文 UTF-16BE 读出转日志）；
//   2) xbl_off=true 时按策略静默处理（策略随观察结论演进，见各 hook 注释）。
//
// 注意：XAM 的 UI 调用是异步的（XOVERLAPPED 完成事件），"不调原函数"会让游戏
// 永远等不到完成事件而挂死——所以默认一律调用原实现，只做观察。

#include <cstdint>
#include <cstring>
#include <string>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>
#include <rex/system/kernel_state.h>

#include <windows.h>

#include "DoubleDragon_app.h"

// 原实现（rexruntime.dll 导入符号；apply_patches.py 已把 init.cpp 的映射指到
// 下面的 dd_hook_*，这里再回调原实现，行为除日志外完全不变）
extern "C" {
void __imp__XamShowAchievementsUI(PPCContext& __restrict, uint8_t*);
void __imp__XamShowMessageBoxUI(PPCContext& __restrict, uint8_t*);
void __imp__XamShowMessageBoxUIEx(PPCContext& __restrict, uint8_t*);
void __imp__XamShowSigninUI(PPCContext& __restrict, uint8_t*);
void __imp__XamUserGetSigninState(PPCContext& __restrict, uint8_t*);
void __imp__XamUserCheckPrivilege(PPCContext& __restrict, uint8_t*);
void __imp__XamContentGetLicenseMask(PPCContext& __restrict, uint8_t*);
}

REXCVAR_DEFINE_BOOL(xbl_off, true, "Game",
                    "关闭 Xbox LIVE 联网提示：静默吞掉登录/未连线类弹窗");

namespace {

// XN_SYS_UI：系统 UI（含消息框）显示状态通知，运行时自身完成消息框时广播 id=9。
constexpr uint32_t kXnSysUi = 9;

// 第 9 个参数（XOVERLAPPED*）的位置：由 sub_821215B8 的生成代码确认——
// 该包装函数 stwu r1,-96 后 `lwz r11,180(r1); stw r11,84(r1)`，即 r1+0x54。
constexpr uint32_t kOverlappedStackOff = 0x54;

// guest 平铺映射：base + guest_addr。越界/未提交页用 SEH 兜底，绝不带崩进程。
bool SafeRead(uint8_t* base, uint32_t addr, void* out, size_t len) {
  if (addr < 0x1000 || addr > 0xFFFF0000u - len) return false;
  __try {
    ::memcpy(out, base + addr, len);
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}

// 读 UTF-16BE 字符串（XBOX 全系统宽字符串都是大端），转 UTF-8 方便日志。
std::string ReadUtf16BE(uint8_t* base, uint32_t addr) {
  std::string out;
  if (addr == 0) return "<null>";
  for (uint32_t i = 0; i < 2048; ++i) {
    uint8_t raw[2];
    if (!SafeRead(base, addr + i * 2, raw, 2)) return out + "<unreadable>";
    uint16_t ch = (uint16_t(raw[0]) << 8) | raw[1];
    if (ch == 0) break;
    if (ch < 0x80) {
      out += char(ch);
    } else if (ch < 0x800) {
      out += char(0xC0 | (ch >> 6));
      out += char(0x80 | (ch & 0x3F));
    } else {
      out += char(0xE0 | (ch >> 12));
      out += char(0x80 | ((ch >> 6) & 0x3F));
      out += char(0x80 | (ch & 0x3F));
    }
  }
  return out;
}

// 判断字符串是否含联网相关关键词（读出转 UTF-8 后匹配；繁简体关键词都列上，
// 因为 UI 语言可切换）。
bool ContainsLiveKeyword(uint8_t* base, uint32_t addr) {
  if (addr == 0) return false;
  std::string s = ReadUtf16BE(base, addr);
  for (char& c : s) {
    if (c >= 'A' && c <= 'Z') c = char(c - 'A' + 'a');
  }
  static const char* kKeywords[] = {
      "xbox", "live", "連線", "连线", "網路", "网络", "网路", "登入",
      "登录", "登錄", "線上", "线上", "伺服", "服務", "服务",
  };
  for (const char* kw : kKeywords) {
    if (s.find(kw) != std::string::npos) return true;
  }
  return false;
}

// guest 内存是**大端**（XBOX 360），读写都要翻转。
bool ReadGuestU32(uint8_t* base, uint32_t addr, uint32_t* out) {
  uint8_t raw[4];
  if (!SafeRead(base, addr, raw, 4)) return false;
  *out = (uint32_t(raw[0]) << 24) | (uint32_t(raw[1]) << 16) | (uint32_t(raw[2]) << 8) | raw[3];
  return true;
}

bool WriteGuestU32(uint8_t* base, uint32_t addr, uint32_t value) {
  if (addr < 0x1000 || addr > 0xFFFF0000u - 4) return false;
  uint8_t raw[4] = {uint8_t(value >> 24), uint8_t(value >> 16), uint8_t(value >> 8), uint8_t(value)};
  __try {
    ::memcpy(base + addr, raw, 4);
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}

// 静默吞掉联网提示框：不调原实现（原实现会在画面上叠一个常驻 ImGui 窗口），
// 但要把它本该完成的异步事件补上——写回按钮结果(0=首个按钮) + 完成 overlapped，
// 否则等这个事件的游戏线程会卡死。
bool TrySuppressLivePopup(PPCContext& ctx, uint8_t* base, const char* which,
                          uint32_t overlapped) {
  if (!REXCVAR_GET(xbl_off)) return false;
  // 只吞开局自动弹的"未连线至 Xbox Live…不会发布最高分数"提示；
  // 成就/排行榜等由用户主动触发的功能提示照常显示（与 xenia_canary 行为一致）。
  std::string text = ReadUtf16BE(base, ctx.r5.u32);
  static const char* kNagKeywords[] = {"最高分", "最高分数", "最高分數",
                                       "并未连线", "並未連線"};
  bool is_nag = false;
  for (const char* kw : kNagKeywords) {
    if (text.find(kw) != std::string::npos) { is_nag = true; break; }
  }
  if (!is_nag) return false;
  if (overlapped < 0x1000 || overlapped >= 0xE0000000u) return false;  // 拿不到就保守放行

  WriteGuestU32(base, ctx.r10.u32, 0);  // pResult 指向的按钮索引 = 0

  auto* ks = rex::runtime::current_kernel_state();
  if (ks) {
    ks->BroadcastNotification(rex::system::XNotificationID(kXnSysUi), 1);
    ks->CompleteOverlappedImmediate(overlapped, 0);
    ks->BroadcastNotification(rex::system::XNotificationID(kXnSysUi), 0);
  }
  REXLOG_INFO("[xbl] 静默拦截 {}：'{}'（overlapped={:#x}）", which,
              ReadUtf16BE(base, ctx.r5.u32), overlapped);
  ctx.r3.u64 = 0;  // ERROR_SUCCESS
  return true;
}

}  // namespace

//-----------------------------------------------------------------------------
// Hooks
//-----------------------------------------------------------------------------

// XamShowAchievementsUI(user, viewID, xuid(64位占 r5:r6), pdwResult*, overlapped*)
// 运行时此函数是 STUB（直接返回、不完成 overlapped），游戏等不到结果就会空指针
// 崩溃（演示流程自动进"成就"画面时必现）。xbl_off 时直接补完成事件。
extern "C" void dd_hook_XamShowAchievementsUI(PPCContext& ctx, uint8_t* base) {
  // 真实签名：DWORD XamShowAchievementsUI(DWORD user, DWORD flags, XOVERLAPPED* ovl)
  // —— 游戏侧包装 sub_82121590 是 `li r4,0; b 0x823ad0bc`，所以 overlapped 在 r5。
  REXLOG_INFO("[xbl] ShowAchievementsUI user={} flags={} ovl={:#x}", ctx.r3.u32,
              ctx.r4.u32, ctx.r5.u32);
  // 完全不碰 overlapped / XN_SYS_UI，先让原实现（运行时是 STUB）照常返回。
  // 游戏因此认为"成就 UI 没打开"，继续按原来的错误分支走（实测不崩），
  // 我们只把本地成就浮层叠上去 —— 和 TimePilot 的 hook 同一思路。
  // 反例（都试过，全部崩）：
  //   a) CompleteOverlappedImmediate + 广播 XN_SYS_UI 1/0 → guest 内立即重入
  //      完成回调，read of guest 0x14（vtable 为 NULL）；
  //   b) 只广播"打开"、留 overlapped 到浮层关闭再完成 → 完成动作只能在 UI
  //      线程发起，运行时内部要 ThreadState，崩在 "no ThreadState bound"；
  //   c) 只 deferred 完成不广播 → 游戏当帧就去读结果，read of guest 0x3F9。
  __imp__XamShowAchievementsUI(ctx, base);
  if (REXCVAR_GET(xbl_off) && g_doubledragon_app) {
    REXLOG_INFO("[xbl] 打开本地成就浮层");
    g_doubledragon_app->ToggleAchievementsOverlay();
  }
}

// XamShowMessageBoxUI(user, title*, text*, cButtons, buttons**, focus, flags,
//                     pResult*, overlapped*)
extern "C" void dd_hook_XamShowMessageBoxUI(PPCContext& ctx, uint8_t* base) {
  REXLOG_INFO("[xbl] MessageBoxUI title='{}' text='{}' buttons={} r7={:#x} r8={:#x}",
              ReadUtf16BE(base, ctx.r4.u32), ReadUtf16BE(base, ctx.r5.u32),
              ctx.r6.u32, ctx.r7.u32, ctx.r8.u32);
  uint32_t overlapped = 0;
  ReadGuestU32(base, ctx.r1.u32 + kOverlappedStackOff, &overlapped);
  if (TrySuppressLivePopup(ctx, base, "MessageBoxUI", overlapped)) return;
  __imp__XamShowMessageBoxUI(ctx, base);
}

// XamShowMessageBoxUIEx —— 参数布局同上（Ex 多一个扩展位，前 5 参一致）
extern "C" void dd_hook_XamShowMessageBoxUIEx(PPCContext& ctx, uint8_t* base) {
  REXLOG_INFO("[xbl] MessageBoxUIEx title='{}' text='{}' buttons={} r7={:#x} r8={:#x}",
              ReadUtf16BE(base, ctx.r4.u32), ReadUtf16BE(base, ctx.r5.u32),
              ctx.r6.u32, ctx.r7.u32, ctx.r8.u32);
  __imp__XamShowMessageBoxUIEx(ctx, base);
}

// XamShowSigninUI(user, flags, overlapped*)
extern "C" void dd_hook_XamShowSigninUI(PPCContext& ctx, uint8_t* base) {
  REXLOG_INFO("[xbl] ShowSigninUI user={} flags={:#x}", ctx.r3.u32, ctx.r4.u32);
  if (REXCVAR_GET(xbl_off) && ctx.r5.u32 >= 0x1000 && ctx.r5.u32 < 0xE0000000u) {
    auto* ks = rex::runtime::current_kernel_state();
    if (ks) {
      ks->BroadcastNotification(rex::system::XNotificationID(kXnSysUi), 1);
      ks->CompleteOverlappedImmediate(ctx.r5.u32, 0);
      ks->BroadcastNotification(rex::system::XNotificationID(kXnSysUi), 0);
    }
    REXLOG_INFO("[xbl] 静默拦截 ShowSigninUI（overlapped={:#x}）", ctx.r5.u32);
    ctx.r3.u64 = 0;
    return;
  }
  __imp__XamShowSigninUI(ctx, base);
}

// XamUserGetSigninState(user) -> state（r3 既是入参也是返回值寄存器）
extern "C" void dd_hook_XamUserGetSigninState(PPCContext& ctx, uint8_t* base) {
  uint32_t user = ctx.r3.u32;
  __imp__XamUserGetSigninState(ctx, base);
  static int s_logged = 0;
  if (s_logged < 8) {
    ++s_logged;
    REXLOG_INFO("[xbl] GetSigninState(user={}) -> {}", user, ctx.r3.u32);
  }
}

// XamUserCheckPrivilege(user, privilege, pbResult*) -> hr
extern "C" void dd_hook_XamUserCheckPrivilege(PPCContext& ctx, uint8_t* base) {
  uint32_t user = ctx.r3.u32, priv = ctx.r4.u32;
  __imp__XamUserCheckPrivilege(ctx, base);
  static int s_logged = 0;
  if (s_logged < 16) {
    ++s_logged;
    uint8_t result = 0;
    SafeRead(base, ctx.r5.u32, &result, 1);
    REXLOG_INFO("[xbl] CheckPrivilege(user={}, priv={:#x}) -> hr={:#x} out={}",
                user, priv, ctx.r3.u32, result);
  }
}

// XamContentGetLicenseMask(pLicenseMask*, overlapped*) -> hr
extern "C" void dd_hook_XamContentGetLicenseMask(PPCContext& ctx, uint8_t* base) {
  uint32_t mask_ptr = ctx.r3.u32;
  __imp__XamContentGetLicenseMask(ctx, base);
  static int s_logged = 0;
  if (s_logged < 4) {
    ++s_logged;
    uint32_t mask = 0;
    SafeRead(base, mask_ptr, &mask, 4);
    REXLOG_INFO("[xbl] GetLicenseMask -> hr={:#x} mask={:#x}", ctx.r3.u32, mask);
  }
}

// 供 apply_patches.py 校验的符号名列表（与 init.cpp 改写一一对应）
// clang-format off
//   dd_hook_XamShowMessageBoxUI        <- __imp__XamShowMessageBoxUI
//   dd_hook_XamShowMessageBoxUIEx      <- __imp__XamShowMessageBoxUIEx
//   dd_hook_XamShowSigninUI            <- __imp__XamShowSigninUI
//   dd_hook_XamUserGetSigninState      <- __imp__XamUserGetSigninState
//   dd_hook_XamUserCheckPrivilege      <- __imp__XamUserCheckPrivilege
//   dd_hook_XamContentGetLicenseMask   <- __imp__XamContentGetLicenseMask
// clang-format on
