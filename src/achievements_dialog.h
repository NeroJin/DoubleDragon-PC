// DoubleDragon - ReXGlue Recompiled Project
//
// 带手柄滚动的成就浮层（从 TimePilot 项目移植，逻辑一致）。
//
// 为什么不用 SDK 内置浮层：内置 AchievementsOverlayDialog 的行是
// Dummy/Image/Text 组合，没有可聚焦控件，ImGui 导航无处落焦；且其
// GetIcon 为 private、手柄输入路径压根不存在。这里自己画一份并把手柄
// 状态映射成滚动量。
//
// 背景：ReXGlue 的 ImGuiDrawer 只接收窗口键鼠事件（imgui_drawer.cpp 中
// 没有任何手柄输入路径），运行时手柄输入全部送进 guest。另外内置成就
// 浮层的列表行是 Dummy/Image/Text 组合，没有任何可聚焦控件，ImGui 的
// 导航系统无处落焦——所以手柄十字键/摇杆默认推不动列表。
//
// 做法（与 UnleashedRecomp 等项目同思路）：每帧 OnDraw 前轮询
// InputSystem::GetStateForUI（专供模拟器自身 UI 在 guest 被阻塞时读取），
// 把手柄状态映射为滚动：左摇杆 = 模拟量平滑滚动，十字键 = 恒速滚动；
// B 键关闭浮层。打开期间 App 持有 UIInputBlocker，手柄不进游戏。

#pragma once

#include <functional>

#include <imgui.h>

#include <rex/input/input_system.h>
#include <rex/system/xtypes.h>
#include <rex/ui/overlay/achievement_icon_cache.h>
#include <rex/ui/overlay/achievements_overlay.h>

// X_STATUS_SUCCESS 宏内含非限定名 X_STATUS，先在文件作用域引入类型。
using rex::X_STATUS;

class DoubleDragonAchievementsDialog : public rex::ui::AchievementsOverlayDialog {
 public:
  DoubleDragonAchievementsDialog(rex::ui::ImGuiDrawer* imgui_drawer,
                              rex::ui::ImmediateDrawer* immediate_drawer,
                              rex::Runtime* runtime,
                              rex::system::AchievementManager* achievements,
                              std::function<void()> on_closed)
      : rex::ui::AchievementsOverlayDialog(imgui_drawer, immediate_drawer, runtime,
                                           achievements),
        runtime_(runtime),
        immediate_drawer_(immediate_drawer),
        achievements_(achievements),
        icon_cache_(immediate_drawer, runtime),
        on_closed_(std::move(on_closed)) {}

  ~DoubleDragonAchievementsDialog() override {
    if (on_closed_) {
      on_closed_();
      on_closed_ = nullptr;
    }
  }

  // 供 App 关闭。遵循 ImGuiDialog 的 self-deleting 惯例：
  // Close() 后由 ImGuiDrawer 在下一帧 Draw 末尾 delete this。
  void CloseFromApp() { Close(); }

 protected:
  void OnDraw(ImGuiIO& io) override {
    // 采样手柄（guest 已被 UIInputBlocker 阻塞，GetStateForUI 可正常读）。
    auto* input = static_cast<rex::input::InputSystem*>(runtime_->input_system());
    bool connected = false;
    uint16_t buttons = 0;
    int16_t ly = 0;
    if (input) {
      rex::input::X_INPUT_STATE state{};
      connected = input->GetStateForUI(0, &state) == X_STATUS_SUCCESS;
      if (connected) {
        buttons = uint16_t(state.gamepad.buttons);
        ly = state.gamepad.thumb_ly;
      }
    }
    const uint16_t pressed = uint16_t(buttons & ~prev_buttons_);
    prev_buttons_ = buttons;

    // B 关闭浮层（主机 UI 习惯）。
    if (pressed & rex::input::X_INPUT_GAMEPAD_B) {
      Close();
    }

    // 本帧滚动量（像素），在列表 child 激活期间应用。
    scroll_delta_ = ComputeScroll(buttons, ly, io.DeltaTime);

    DrawAchievementsWindow(io);
  }

 private:
  float ComputeScroll(uint16_t buttons, int16_t ly, float dt) {
    float delta = 0.0f;

    // 左摇杆：模拟量平滑滚动（Y 轴向上为正 → 向上滚为负增量）。
    constexpr int16_t kStickDeadzone = 8000;
    if (ly > kStickDeadzone || ly < -kStickDeadzone) {
      const float normalized =
          ly > 0 ? (ly - kStickDeadzone) / 32767.0f
                 : (ly + kStickDeadzone) / 32768.0f;
      constexpr float kStickSpeed = 900.0f;  // 满行程像素/秒
      delta -= normalized * kStickSpeed * dt;
    }

    // 十字键：恒速滚动（电平触发，按住即滚）。
    constexpr float kDpadSpeed = 480.0f;
    if (buttons & rex::input::X_INPUT_GAMEPAD_DPAD_UP) {
      delta -= kDpadSpeed * dt;
    }
    if (buttons & rex::input::X_INPUT_GAMEPAD_DPAD_DOWN) {
      delta += kDpadSpeed * dt;
    }

    return delta;
  }

  // 与 SDK 内置 AchievementsOverlayDialog::OnDraw 相同的布局
  // （该实现不可复用：列表行无可聚焦控件且 GetIcon 为 private），
  // 增加手柄滚动。上游更新布局时此处需同步。
  void DrawAchievementsWindow(ImGuiIO& io) {
    const rex::ui::AchievementsStyle& style =
        imgui_drawer()->style().achievements;

    ImGui::SetNextWindowPos(ImVec2(io.DisplaySize.x * 0.5f, 40.0f),
                            ImGuiCond_FirstUseEver, ImVec2(0.5f, 0.0f));
    ImGui::SetNextWindowSize(ImVec2(640.0f, 560.0f), ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowBgAlpha(0.92f);

    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, style.window_padding);
    ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, style.item_spacing);

    if (ImGui::Begin("Achievements##overlay", nullptr,
                     ImGuiWindowFlags_NoCollapse)) {
      const auto achievements = achievements_->ListAchievements();

      int unlocked_count = 0;
      int total_gs = 0;
      int earned_gs = 0;
      for (const auto& a : achievements) {
        total_gs += static_cast<int>(a.gamerscore);
        if (achievements_->IsUnlocked(a.id)) {
          ++unlocked_count;
          earned_gs += static_cast<int>(a.gamerscore);
        }
      }
      const int total_count = static_cast<int>(achievements.size());

      // ---- Header --------------------------------------------------------
      ImGui::PushStyleColor(ImGuiCol_Text, style.header_text);
      ImGui::Text("%d / %d unlocked", unlocked_count, total_count);
      ImGui::PopStyleColor();
      ImGui::SameLine();
      ImGui::TextColored(style.badge_gamerscore, "%dG / %dG", earned_gs,
                         total_gs);

      const float frac =
          total_count > 0
              ? static_cast<float>(unlocked_count) / total_count
              : 0.0f;
      ImGui::PushStyleColor(ImGuiCol_PlotHistogram, style.progress_bar);
      ImGui::ProgressBar(frac, ImVec2(-1.0f, 6.0f), "");
      ImGui::PopStyleColor();

      ImGui::TextDisabled("Unlock progress is saved automatically");
      ImGui::Separator();
      ImGui::Spacing();

      // ---- List ----------------------------------------------------------
      ImGui::BeginChild("##achlist", ImVec2(0.0f, 0.0f), false);
      if (scroll_delta_ != 0.0f) {
        // 手柄滚动：SetScrollY 内部会钳制到合法范围。
        ImGui::SetScrollY(ImGui::GetScrollY() + scroll_delta_);
      }
      ImDrawList* draw_list = ImGui::GetWindowDrawList();

      for (const auto& a : achievements) {
        const bool is_unlocked = achievements_->IsUnlocked(a.id);
        const std::string& desc =
            is_unlocked ? a.description : a.unachieved_description;

        ImGui::PushID(static_cast<int>(a.id));

        draw_list->ChannelsSplit(2);
        draw_list->ChannelsSetCurrent(1);

        const float row_start_y = ImGui::GetCursorScreenPos().y;
        const float pad = 4.0f;
        ImGui::Dummy(ImVec2(0.0f, pad * 0.5f));

        rex::ui::ImmediateTexture* icon = icon_cache_.GetIcon(a);
        if (icon) {
          ImVec4 tint = is_unlocked ? style.unlocked_icon_tint
                                    : style.locked_icon_tint;
          ImGui::ImageWithBg(reinterpret_cast<ImTextureID>(icon),
                             ImVec2(style.icon_size, style.icon_size),
                             ImVec2(0, 0), ImVec2(1, 1), ImVec4(0, 0, 0, 0),
                             tint);
        } else {
          ImGui::Dummy(ImVec2(style.icon_size, style.icon_size));
        }
        ImGui::SameLine();

        ImGui::BeginGroup();
        const char* marker = is_unlocked ? "[*]" : "[ ]";
        const ImVec4& title_color =
            is_unlocked ? style.unlocked_title : style.locked_title;
        ImGui::TextColored(title_color, "%s", marker);
        ImGui::SameLine();
        ImGui::TextColored(style.badge_gamerscore, "%dG",
                           static_cast<int>(a.gamerscore));
        ImGui::SameLine();
        ImGui::TextColored(title_color, "%s", a.label.c_str());

        ImGui::PushStyleColor(
            ImGuiCol_Text,
            is_unlocked ? style.unlocked_desc : style.locked_desc);
        ImGui::TextWrapped("%s", desc.c_str());
        ImGui::PopStyleColor();
        ImGui::EndGroup();

        ImGui::Dummy(ImVec2(0.0f, pad * 0.5f));
        const float row_end_y = ImGui::GetCursorScreenPos().y;

        if (is_unlocked) {
          draw_list->ChannelsSetCurrent(0);
          const float x0 = ImGui::GetWindowPos().x + 2.0f;
          const float x1 = x0 + ImGui::GetWindowSize().x - 4.0f;
          draw_list->AddRectFilled(ImVec2(x0, row_start_y),
                                   ImVec2(x1, row_end_y),
                                   ImGui::GetColorU32(style.row_unlocked_bg),
                                   style.row_rounding);
        }
        draw_list->ChannelsMerge();

        ImGui::Separator();
        ImGui::PopID();
      }
      ImGui::EndChild();
    }
    ImGui::End();

    ImGui::PopStyleVar(2);
  }

  rex::Runtime* runtime_ = nullptr;
  rex::ui::ImmediateDrawer* immediate_drawer_ = nullptr;
  // 基类的 achievements_ / icon_cache_ 均为 private，自备同名成员。
  rex::system::AchievementManager* achievements_ = nullptr;
  rex::ui::AchievementIconCache icon_cache_;
  std::function<void()> on_closed_;  // 析构/自关时通知 App（清指针+摘输入阻塞）
  uint16_t prev_buttons_ = 0;
  float scroll_delta_ = 0.0f;
};
