// DoubleDragon - 界面语言重定向（简体中文 zh-cn）
//
// 原理（逆向确认，dd_dec.xex 偏移 0x60B0）：
//   guest 镜像里有一张 UTF-16BE 打包的语言目录名宽字符串表：
//     "en-us" "pt-pt" "zh-cht" "ko-kr" "it-it" "es-es" "fr-fr" "de-de" "ja-jp" "en-en"
//   游戏按 XGetLanguage() 的枚举从中取目录名，拼出 game\ui\<目录>\StringTable.xus
//   与 game\ui\skins\Graphics\<目录>\... 的资源路径。
//
//   开关 ui_lang_simp 打开时，在 XEX 载入后、guest 线程运行前，把内存里的
//   "zh-cht" 就地改写为 "zh-cn\0"（等长 12 字节，含终止符；原 6 字符 + 1 终止符
//   = 7 个宽字符，改后 5 字符 + 2 终止符，总宽字符数不变，表内后续条目不受影响）。
//   于是游戏实际读取 game\ui\zh-cn\（简体串表）与 skins\Graphics\zh-cn\（按键音）。
//   原版繁体 zh-cht/ 目录零改动，开关关闭时完全不生效。
//
// 简体资源由 tools/t2s_stringtable.py 生成（OpenCC t2s，UTF-16 字节数不变）。

#include <cstring>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/memory.h>
#include <rex/system/xthread.h>

#include <windows.h>

// hooks.cpp 提供的 guest 页可读性探测（VirtualQuery 校验，不会触发 AV）。
bool ProbeReadable(rex::memory::Memory* mem, uint32_t addr);

REXCVAR_DEFINE_BOOL(ui_lang_simp, false, "Game",
                    "界面语言重定向：zh-cht -> zh-cn（简体中文串表）");

namespace {

constexpr uint32_t kScanBegin = 0x82000000;  // XEX 镜像基址
constexpr uint32_t kScanEnd = 0x84000000;    // 覆盖代码+数据段，不可读页自动跳过
constexpr uint32_t kPageSize = 0x1000;

// UTF-16BE "zh-cht"（6 字符）
constexpr uint8_t kPatZht[12] = {0x00, 0x7A, 0x00, 0x68, 0x00, 0x2D,
                                 0x00, 0x63, 0x00, 0x68, 0x00, 0x74};
// UTF-16BE "zh-cn\0"（5 字符 + 终止符，总宽字符数与上面一致）
constexpr uint8_t kRepZcn[12] = {0x00, 0x7A, 0x00, 0x68, 0x00, 0x2D,
                                 0x00, 0x63, 0x00, 0x6E, 0x00, 0x00};

}  // namespace

// OnPostLaunchModule 里调用（主 guest 线程已创建、尚未执行，镜像已在内存里）。
void DoubleDragon_InstallUiLanguageRedirect(rex::runtime::ThreadState* ts) {
  if (!ts || !ts->memory()) return;
  auto* mem = ts->memory();

  if (!REXCVAR_GET(ui_lang_simp)) {
    REXLOG_INFO("[lang] ui_lang_simp=false，保持原版语言目录");
    return;
  }

  int patches = 0;
  for (uint32_t page = kScanBegin; page < kScanEnd; page += kPageSize) {
    if (!ProbeReadable(mem, page)) continue;
    auto* p = mem->TranslateVirtual<uint8_t*>(page);
    if (!p) continue;
    for (uint32_t i = 0; i + sizeof(kPatZht) <= kPageSize; ++i) {
      if (std::memcmp(p + i, kPatZht, sizeof(kPatZht)) != 0) continue;
      // 命中点多半落在代码/数据段，运行时把它映射成只读页：直接写会触发
      // guest AV（实测 0x820040B0 写失败）。先 VirtualProtect 开写保护再写。
      void* target = p + i;
      MEMORY_BASIC_INFORMATION mbi{};
      if (::VirtualQuery(target, &mbi, sizeof(mbi)) == 0 || mbi.State != MEM_COMMIT) {
        continue;
      }
      DWORD old_protect = 0;
      if (!::VirtualProtect(target, sizeof(kRepZcn), PAGE_READWRITE, &old_protect)) {
        REXLOG_WARN("[lang] VirtualProtect 失败 @guest 0x{:08X}", page + i);
        continue;
      }
      std::memcpy(target, kRepZcn, sizeof(kRepZcn));
      DWORD restored = 0;
      ::VirtualProtect(target, sizeof(kRepZcn), old_protect, &restored);
      ++patches;
      if (patches <= 8) {
        REXLOG_INFO("[lang] zh-cht -> zh-cn @guest 0x{:08X}", page + i);
      }
    }
  }

  if (patches > 0) {
    REXLOG_INFO("[lang] 语言重定向完成：{} 处 zh-cht 已改写为 zh-cn", patches);
  } else {
    REXLOG_WARN("[lang] 未在 guest 镜像中找到 zh-cht 目录名，重定向未生效");
  }
}
