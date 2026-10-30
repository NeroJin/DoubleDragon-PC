// DoubleDragon - ReXGlue Recompiled Project
//
// Customize your app by overriding virtual hooks from rex::ReXApp.

#pragma once

#include <rex/rex_app.h>
#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/system/thread_state.h>
#include <rex/system/xthread.h>

#include <filesystem>
#include <windows.h>

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

class DoubledragonApp : public rex::ReXApp {
 public:
  using rex::ReXApp::ReXApp;

  static std::unique_ptr<rex::ui::WindowedApp> Create(
      rex::ui::WindowedAppContext& ctx) {
    return std::unique_ptr<DoubledragonApp>(new DoubledragonApp(ctx, "DoubleDragon",
        PPCImageConfig));
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

  // Override virtual hooks for customization:
  // void OnPostInitLogging() override {}
  // void OnPreSetup(rex::RuntimeConfig& config) override {}
  // void OnLoadXexImage(std::string& xex_image) override {}
  // void OnPostLoadXexImage() override {}
  // void OnPostSetup() override {}
  // void OnCreateDialogs(rex::ui::ImGuiDrawer* drawer) override {}
  // std::unique_ptr<rex::ui::ImGuiDialog> CreateAchievementsOverlay() override;
  // std::unique_ptr<rex::ui::AchievementNotificationDialog>
  // CreateAchievementNotificationDialog() override;
  // void OnShutdown() override {}
  // void OnConfigurePaths(rex::PathConfig& paths) override {}
};
