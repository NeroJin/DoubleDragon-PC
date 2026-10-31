// DoubleDragon - ReXGlue Recompiled Project
//
// Customize your app by overriding virtual hooks from rex::ReXApp.

#pragma once

#include <rex/rex_app.h>
#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/system/thread_state.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xthread.h>
#include <rex/ui/keybinds.h>

#include <filesystem>
#include <windows.h>

#include "generated/default/DoubleDragon_init.h"  // PPCImageConfig

#include "achievements_dialog.h"

// crashdump.cpp：guest 崩溃时打印 PPC 调用栈（开发期辅助）
void InstallGuestCrashDump();

// lang_redirect.cpp：ui_lang_simp=true 时把 guest 内存里的语言目录名
// "zh-cht" 改写成 "zh-cn"，使游戏读取简体中文串表（见 lang_redirect.cpp 注释）。
void DoubleDragon_InstallUiLanguageRedirect(rex::runtime::ThreadState* ts);

// 从 exe 所在目录向上回溯，找到含 "game" 子目录的项目根，
// 把 game_data_root 钉死到 <root>/game。
// 默认 OnConfigurePaths 按 CWD 解析 game_root：双击 exe（CWD=exe 目录）
// 或从其子目录启动时会找不到 game/ -> "game_dir not mounted" Fatal Error。
// 这里改为按 exe 位置回溯，使挂载与启动目录无关。
inline std::filesystem::path ResolveGameDataRoot() {
  namespace fs = std::filesystem;
  fs::path exe_dir;
  wchar_t buf[MAX_PATH] = {0};
  if (GetModuleFileNameW(nullptr, buf, MAX_PATH)) {
    exe_dir = fs::path(buf).parent_path();
  } else {
    exe_dir = fs::current_path();
  }
  fs::path cur = exe_dir;
  for (int i = 0; i < 8; ++i) {
    if (fs::exists(cur / "game")) return cur / "game";
    fs::path parent = cur.parent_path();
    if (parent == cur) break;
    cur = parent;
  }
  // 兜底：直接用 exe 目录下的 game（即便不存在也给出确定路径，便于排查）
  return exe_dir / "game";
}

class DoubledragonApp;

// App 单实例指针。定义在 src/hooks.cpp（xbl_off.cpp 的成就钩子也用它）。
extern DoubledragonApp* g_doubledragon_app;

class DoubledragonApp : public rex::ReXApp {
 public:
  using rex::ReXApp::ReXApp;

  static std::unique_ptr<rex::ui::WindowedApp> Create(
      rex::ui::WindowedAppContext& ctx) {
    auto app = std::unique_ptr<DoubledragonApp>(new DoubledragonApp(ctx, "DoubleDragon",
        PPCImageConfig));
    g_doubledragon_app = app.get();
    return app;
  }

  // 供 PPC 钩子调用（xbl_off.cpp 的 dd_hook_XamShowAchievementsUI）：
  // 切到 UI 线程再动 ImGui 对象（ImGui 非线程安全）。
  // F7 与游戏菜单共用本函数 → 全程只有浮层的单一实例。
  void ToggleAchievementsOverlay() {
    app_context().CallInUIThreadDeferred([this]() {
      if (achievements_dialog_) {
        achievements_dialog_->CloseFromApp();  // 下一帧 Draw 末尾自删
      } else {
        OpenAchievementsOverlay();
      }
    });
  }

  // 启用 Xenos GPU 模拟插件。
  // RuntimeConfig::gpu_plugin 为空 = 完全没有 GPU 模拟：guest 写入的命令缓冲
  // 无人消费，帧循环会在 sub_82134910 里死等 GPU 进度计数器（表现为黑屏且
  // 进程 100%+ CPU 自旋）。CMake 的 GPU_PLUGINS xenos 只负责把
  // rexgpu-xenos.dll 拷到 exe 旁边，运行时必须在这里显式指定插件名。
  void OnPreSetup(rex::RuntimeConfig& config) override {
    config.gpu_plugin = "xenos";
  }

  // 主 guest 线程已创建、尚未开始执行：在这里装崩溃转储，能盖掉运行时自己的过滤器。
  // ui_lang_simp=true 时同时改写 guest 内存里的语言目录名（zh-cht -> zh-cn），
  // 此时镜像已载入、游戏尚未读任何 UI 资源，是最安全的改写时机。
  void OnPostLaunchModule(rex::system::XThread* thread) override {
    InstallGuestCrashDump();
    if (thread) {
      DoubleDragon_InstallUiLanguageRedirect(thread->thread_state());
    }
  }

  // 保持垂直同步开启。
  // recomp 的 vblank（guest 侧 VdSwap 的节拍）由 presenter 的 vsync worker 驱动：
  // 关掉 vsync 后 Present 立即返回，vblank 中断就按 CPU 速度狂发，游戏逻辑跑得
  // 比实时快（实测"速度变快"）。黑屏排查期临时关 vsync 只是为了绕开当时的上屏
  // 阻塞，真正根因是 gpu_plugin 为空，已修；这里恢复默认的实时节拍。
  // 若用户从命令行显式改过（--vsync=false 等），尊重用户选择不覆盖。
  // host_present_from_non_ui_thread：游戏主循环跑在 guest 线程并直接调 VdSwap，
  // 默认 presenter 会把上屏派发到 UI 线程并等待 -> UI 线程没在泵消息时死锁，
  // 所以允许 guest 线程直接上屏。
  void OnPreLaunchModule() override {
    if (!rex::cvar::HasNonDefaultValue("vsync")) {
      rex::cvar::SetFlagByName("vsync", "true");
    }
    rex::cvar::SetFlagByName("host_present_from_non_ui_thread", "true");
    REXLOG_INFO("present config: vsync={} host_present_from_non_ui_thread={}",
                rex::cvar::GetFlagByName("vsync"),
                rex::cvar::GetFlagByName("host_present_from_non_ui_thread"));
  }

  // 默认字体只带简体常用字形（GetGlyphRangesChineseSimplifiedCommon），
  // 而成就/串表文本来自原版（繁体），「兩」「續」「擊」这类字会显示成 `?`。
  // 在图集构建前合并一份系统字体的完整 CJK 字形，覆盖全部汉字。
  void OnConfigureFonts(ImFontAtlas* atlas) override {
    if (!atlas) return;
    ImFontConfig cfg;
    cfg.MergeMode = true;          // 合并进默认字体，沿用其行高/字号
    cfg.PixelSnapH = true;
    cfg.GlyphOffset = ImVec2(0.0f, 0.0f);
    static const char* kCandidates[] = {
        "C:/Windows/Fonts/msyh.ttc",    // 微软雅黑
        "C:/Windows/Fonts/msyhl.ttc",
        "C:/Windows/Fonts/simhei.ttf",  // 黑体
        "C:/Windows/Fonts/simsun.ttc",  // 宋体
    };
    for (const char* path : kCandidates) {
      if (GetFileAttributesA(path) == INVALID_FILE_ATTRIBUTES) continue;
      if (atlas->AddFontFromFileTTF(path, 16.0f, &cfg,
                                    atlas->GetGlyphRangesChineseFull())) {
        REXLOG_INFO("imgui: 合并中文字形 {}", path);
        break;
      }
    }
  }

  // 把运行时日志落盘，抓 GPU/present/输入等真实报错（GUI 下 stderr 看不到）。
  void OnConfigureLogging(rex::LogConfig& config) override {
    config.log_file = ResolveGameDataRoot().parent_path() / "rex_runtime.log";
    config.default_level = spdlog::level::debug;
    config.log_to_console = false;
  }

  // 把 game_data_root 钉死到项目根下的 game/，与启动目录（CWD）无关，
  // 避免双击 exe（CWD=exe 目录）时 "game_dir not mounted"。
  void OnConfigurePaths(rex::PathConfig& paths) override {
    paths.game_data_root = ResolveGameDataRoot();
  }

 protected:
  // SetupOverlays 末尾调用（内置键位已注册）。把内置 F7 绑定替换成
  // 我们的 Toggle——否则内置绑定会创建第二份浮层实例，互相"看起来失灵"。
  void OnCreateDialogs(rex::ui::ImGuiDrawer* drawer) override {
    (void)drawer;
    rex::ui::UnregisterBind("bind_achievements");
    rex::ui::RegisterBind("bind_achievements", "F7",
                          "Toggle achievements overlay",
                          [this] { ToggleAchievementsOverlay(); });
  }

  // 关机时浮层可能还开着：先手动删（此时 drawer/input system 仍存活），
  // 避免其析构回调在 App 部分析构后触碰 runtime()。
  void OnDestroy() override {
    if (achievements_dialog_) {
      auto* dialog = achievements_dialog_;
      achievements_dialog_ = nullptr;  // 防止析构回调重入
      delete dialog;
    }
  }

 private:
  void OpenAchievementsOverlay() {
    // 浮层打开期间手柄输入不进游戏（guest 读到空手柄），关浮层即恢复。
    auto* input = static_cast<rex::input::InputSystem*>(runtime()->input_system());
    if (input) {
      input->AddUIInputBlocker();
    }
    achievements_dialog_ = new DoubleDragonAchievementsDialog(
        imgui_drawer(), immediate_drawer(), runtime(), &achievements(),
        [this] {
          achievements_dialog_ = nullptr;
          auto* in =
              static_cast<rex::input::InputSystem*>(runtime()->input_system());
          if (in) {
            in->RemoveUIInputBlocker();
          }
        });
  }

  // 带手柄导航的成就浮层。ImGuiDialog 遵循 self-deleting 惯例
  // （Close 后下一帧 Draw 末尾 delete this，析构回调清指针），
  // 所以这里必须是裸指针而非 unique_ptr。
  DoubleDragonAchievementsDialog* achievements_dialog_ = nullptr;
};
