// DoubleDragon - ReXGlue Recompiled Project
//
// Customize your app by overriding virtual hooks from rex::ReXApp.

#pragma once

#include <rex/rex_app.h>
#include <rex/cvar.h>
#include <rex/logging.h>

#include <filesystem>
#include <windows.h>

// crashdump.cpp：guest 崩溃时打印 PPC 调用栈（开发期辅助）
void InstallGuestCrashDump();

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
  void OnPostLaunchModule(rex::system::XThread* thread) override {
    (void)thread;
    InstallGuestCrashDump();
  }

  // guest 启动前关闭 vsync：recomp 的 D3D12 presenter 默认 Present(1) 等待 vsync，
  // 在无头/合成异常等环境下 vsync 永不到来 -> 第 2 次上屏永久阻塞（黑屏）。
  // 关掉后 Present(0) 立即返回，避免上屏死锁。
  // host_present_from_non_ui_thread：游戏主循环跑在 guest 线程并直接调 VdSwap，
  // 默认 presenter 会把上屏派发到 UI 线程等待 -> UI 线程没在泵消息时死锁。
  // 允许 guest 线程直接上屏，绕开跨线程派发死锁。
  void OnPreLaunchModule() override {
    rex::cvar::SetFlagByName("vsync", "false");
    rex::cvar::SetFlagByName("host_present_from_non_ui_thread", "true");
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
