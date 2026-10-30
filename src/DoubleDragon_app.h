// DoubleDragon - ReXGlue Recompiled Project
//
// Customize your app by overriding virtual hooks from rex::ReXApp.

#pragma once

#include <rex/rex_app.h>

// crashdump.cpp：guest 崩溃时打印 PPC 调用栈（开发期辅助）
void InstallGuestCrashDump();

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
