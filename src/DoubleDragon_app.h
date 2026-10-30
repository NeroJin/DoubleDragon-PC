// DoubleDragon - ReXGlue Recompiled Project
//
// Customize your app by overriding virtual hooks from rex::ReXApp.

#pragma once

#include <rex/rex_app.h>

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

  // 主 guest 线程已创建、尚未开始执行：在这里装崩溃转储，能盖掉运行时自己的过滤器。
  void OnPostLaunchModule(rex::system::XThread* thread) override {
    (void)thread;
    InstallGuestCrashDump();
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
