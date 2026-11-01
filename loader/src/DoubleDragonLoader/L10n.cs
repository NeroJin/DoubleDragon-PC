using System;
using System.Collections.Generic;
using System.Globalization;

namespace DoubleDragon.Loader
{
    /// <summary>
    /// 极简本地化：只支持简体中文 / 英文两种，跟随系统 UI 语言自动选择。
    /// 词条放在表里而不是 resx，是为了让"同一个 key 的中英两份文本"始终相邻，
    /// 改文案时不会漏掉另一种语言。缺 key 时直接返回 key 本身，便于发现遗漏。
    /// </summary>
    internal static class L10n
    {
        private static readonly Dictionary<string, string[]> Table =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                // key                      zh-CN, en-US
                { "AppTitle",        new[] { "Double Dragon 启动器", "Double Dragon Launcher" } },
                { "GroupGraphics",   new[] { "画质", "Graphics" } },
                { "GroupInput",      new[] { "输入", "Input" } },
                { "KeyboardOn",      new[] { "启用键盘控制（键盘当作 1P 手柄）", "Enable keyboard (acts as a player-1 pad)" } },
                { "HintKeyboard",    new[] { "开启后键盘与实体手柄同时控制 1P（输入合并）。只用实体手柄时建议关掉：本作 A 键默认绑在分号/空格上，误碰会触发动作。键位在下面「键盘映射…」里改。", "When on, the keyboard controls player 1 alongside a real pad (inputs merge). If you only use a pad, turn it off: the default A binding is Semicolon/Space, so stray key presses trigger actions. Bindings live under Key bindings… below." } },
                { "GroupData",       new[] { "游戏数据", "Game data" } },
                { "LabelAa",         new[] { "抗锯齿", "Anti-aliasing" } },
                { "LabelLang",       new[] { "界面语言", "UI language" } },
                { "LabelWindowSize",  new[] { "窗口尺寸", "Window size" } },
                { "DisableLive",     new[] { "关闭 Xbox LIVE 联网提示", "Suppress Xbox LIVE prompts" } },
                { "Fullscreen",      new[] { "全屏（无边框）", "Fullscreen (borderless)" } },
                { "AaNone",          new[] { "关闭（none）", "Off (none)" } },
                { "AaFxaa",          new[] { "FXAA", "FXAA" } },
                { "AaFxaaExtreme",   new[] { "FXAA Extreme", "FXAA Extreme" } },

                { "DataReady",       new[] { "状态：已解包（default.xex {0:N0} 字节）", "Status: extracted (default.xex {0:N0} bytes)" } },
                { "DataMissing",     new[] { "状态：未解包", "Status: not extracted" } },
                { "HintIdle",        new[] { "设置改动会立即写入 DoubleDragon.toml；窗口尺寸仍可在游戏内按 F4 调整。", "Changes are written to DoubleDragon.toml immediately; window size is still adjustable in-game with F4." } },
                { "ProgressItem",    new[] { "正在解包 {0}/{1}：{2}", "Extracting {0}/{1}: {2}" } },
                { "ExtractDone",     new[] { "解包完成：{0} 个文件 / {1:N0} 字节，用时 {2:F1} 秒", "Extraction finished: {0} files / {1:N0} bytes in {2:F1} s" } },
                { "Cancelled",       new[] { "已取消，目标目录未被修改。", "Cancelled. Target folder was left untouched." } },

                { "Extract",         new[] { "解包资源(&E)", "&Extract data" } },
                { "Cancel",          new[] { "取消", "Cancel" } },
                { "Launch",          new[] { "启动游戏(&L)", "&Launch game" } },

                { "PickPackageTitle", new[] { "选择 Double Dragon 的 Xbox 360 内容包", "Select the Xbox 360 package for Double Dragon" } },
                { "PickPackageFilter", new[] { "Xbox 360 内容包 (*.live;*.pirs;*.con)|*.live;*.pirs;*.con|所有文件 (*.*)|*.*", "Xbox 360 package (*.live;*.pirs;*.con)|*.live;*.pirs;*.con|All files (*.*)|*.*" } },
                { "ConfirmOverwrite",new[] { "已经解包过一次，重新解包会替换现有资源（存档不受影响）。继续吗？", "Game data is already extracted. Re-extracting will replace it (your saves are not touched). Continue?" } },
                { "ExtractFailed",   new[] { "解包失败：\n{0}", "Extraction failed:\n{0}" } },
                { "LaunchNeedsData", new[] { "游戏数据还没解包，现在解包吗？", "Game data has not been extracted yet. Extract it now?" } },
                { "LaunchFailed",    new[] { "无法启动游戏：\n{0}", "Could not start the game:\n{0}" } },
                { "SaveFailed",      new[] { "无法写入 DoubleDragon.toml：\n{0}\n\n本次设置不会生效，游戏将使用文件里的旧值。", "Could not write DoubleDragon.toml:\n{0}\n\nThese settings will not apply; the game will use the old values." } },

                { "MissingTitle",    new[] { "缺少运行文件", "Missing files" } },
                { "MissingBody",     new[] { "启动器必须和游戏放在同一个文件夹里。以下文件缺失：\n\n{0}", "The launcher must sit in the same folder as the game. These files are missing:\n\n{0}" } },

                // ---- 键盘映射 ----
                { "KeyMap",          new[] { "键盘映射…(&K)", "Key bindings…" } },
                { "KeyMapTitle",     new[] { "键盘映射（1P）", "Key bindings (player 1)" } },
                { "KeyMapIntro",     new[] { "键盘会被模拟成一个 1P 手柄，和实体手柄同时使用（输入合并）。这里列出全部 25 个手柄动作；Double Dragon 只用到其中一部分，其余留给下一个项目。键名由运行时解析，写法固定，请用「设置…」录入。灰字是运行时的出厂键位，橙字表示出厂键名运行时认不出——那个动作其实没有键，要自己绑。", "The keyboard is presented as a player-1 pad and works alongside a real pad (inputs merge). All 25 pad actions are listed; Double Dragon only consumes some of them, the rest are here for the next project. Key names are parsed strictly, so use Set… to record them. Grey 「(default: …)」 is the shipped runtime binding; orange means the shipped key name cannot be parsed, so that action has no key at all and must be bound manually." } },
                { "KeyReservedConfirm", new[] { "{0} 同时也是 {1} 的热键。实测两个都会响应：按下会同时打开 overlay 和触发游戏动作。仍然绑定吗？", "{0} is also the hotkey of {1}. Both fire: the overlay opens and the pad button presses at the same time. Bind anyway?" } },
                { "KeyMnkOff",       new[] { "键盘控制当前是关着的（主界面「启用键盘控制」没勾选），下面改的键位不会生效。", "Keyboard control is currently off (Enable keyboard is unchecked in the main window), so the bindings below will do nothing." } },
                { "ColAction",       new[] { "动作", "Action" } },
                { "ColKey",          new[] { "当前按键", "Current key" } },
                { "KeySet",          new[] { "设置…", "Set…" } },
                { "KeyClear",        new[] { "清除", "Clear" } },
                { "KeyDefaultFmt",   new[] { "默认 {0}", "default {0}" } },
                { "KeyDefaultNone",  new[] { "默认无", "default: none" } },
                { "KeyDefaultBroken", new[] { "默认 {0}（无效）", "default {0} (invalid)" } },
                { "KeyDefaultBrokenCount", new[] { "有 {0} 个动作的出厂键名运行时认不出（橙色），这些动作要用得自己绑。", "{0} actions ship with key names the runtime cannot parse (shown in orange); bind them yourself to use them." } },
                { "KeyCaptureTitle", new[] { "按键", "Press a key" } },
                { "KeyCaptureHint",  new[] { "请按下要绑定的键；Esc 取消。", "Press the key to bind. Esc cancels." } },
                { "KeyCaptureModifier", new[] { "按 Shift / Ctrl / Alt 本身不算，请再按一个键。", "Shift / Ctrl / Alt alone does not count. Press another key." } },
                { "KeyCaptureUnsupported", new[] { "运行时认不出这个键，换一个试试。", "The runtime does not know this key. Try another one." } },
                { "KeyUnknownToken", new[] { "「{0}」的键名 {1} 运行时认不出来，这一项不会生效。", "Key name {1} for {0} is not recognized by the runtime; that binding will do nothing." } },
                { "KeyConflict",     new[] { "「{0}」和「{1}」绑了同一个键，按下会同时触发。", "{0} and {1} share the same key; pressing it triggers both." } },
                { "KeySaved",        new[] { "已写入 DoubleDragon.toml，重启游戏后生效。", "Written to DoubleDragon.toml; takes effect after restarting the game." } },
                { "KeyHintIdle",     new[] { "可以用逗号绑多个键，例如 Semicolon,Space；改动立即写入 DoubleDragon.toml。", "Multiple keys can be comma-separated, e.g. Semicolon,Space. Changes are written to DoubleDragon.toml immediately." } },
                { "KeyWriteFailed",  new[] { "无法写入 DoubleDragon.toml：\n{0}", "Could not write DoubleDragon.toml:\n{0}" } },

                { "SecFace",         new[] { "面键", "Face buttons" } },
                { "SecShoulder",     new[] { "肩键 / 扳机", "Shoulders & triggers" } },
                { "SecSystem",       new[] { "系统键", "System" } },
                { "SecDpad",         new[] { "十字键", "D-pad" } },
                { "SecLStick",       new[] { "左摇杆", "Left stick" } },
                { "SecRStick",       new[] { "右摇杆", "Right stick" } },

                { "ActA",            new[] { "A 键", "A button" } },
                { "ActB",            new[] { "B 键", "B button" } },
                { "ActX",            new[] { "X 键", "X button" } },
                { "ActY",            new[] { "Y 键", "Y button" } },
                { "ActLB",           new[] { "左肩键 LB", "Left bumper (LB)" } },
                { "ActRB",           new[] { "右肩键 RB", "Right bumper (RB)" } },
                { "ActLT",           new[] { "左扳机 LT", "Left trigger (LT)" } },
                { "ActRT",           new[] { "右扳机 RT", "Right trigger (RT)" } },
                { "ActStart",        new[] { "START（暂停 / 菜单）", "START (pause / menu)" } },
                { "ActBack",         new[] { "BACK", "BACK" } },
                { "ActGuide",        new[] { "GUIDE（主页键）", "GUIDE (home)" } },
                { "ActLStickUp",     new[] { "左摇杆 上", "Left stick up" } },
                { "ActLStickDown",   new[] { "左摇杆 下", "Left stick down" } },
                { "ActLStickLeft",   new[] { "左摇杆 左", "Left stick left" } },
                { "ActLStickRight",  new[] { "左摇杆 右", "Left stick right" } },
                { "ActLStickPress",  new[] { "左摇杆 按下", "Left stick click" } },
                { "ActDpadUp",       new[] { "十字键 上", "D-pad up" } },
                { "ActDpadDown",     new[] { "十字键 下", "D-pad down" } },
                { "ActDpadLeft",     new[] { "十字键 左", "D-pad left" } },
                { "ActDpadRight",    new[] { "十字键 右", "D-pad right" } },
                { "ActRStickUp",     new[] { "右摇杆 上", "Right stick up" } },
                { "ActRStickDown",   new[] { "右摇杆 下", "Right stick down" } },
                { "ActRStickLeft",   new[] { "右摇杆 左", "Right stick left" } },
                { "ActRStickRight",  new[] { "右摇杆 右", "Right stick right" } },
                { "ActRStickPress",  new[] { "右摇杆 按下", "Right stick click" } },

                { "OvDebug",         new[] { "调试面板", "debug overlay" } },
                { "OvConsole",       new[] { "控制台", "console" } },
                { "OvSettings",      new[] { "设置面板", "settings overlay" } },
                { "OvAchievements",  new[] { "成就面板", "achievements overlay" } },
            };

        static L10n()
        {
            // 简繁都算中文；其它语言一律英文。
            string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            IsChinese = string.Equals(lang, "zh", StringComparison.OrdinalIgnoreCase);
            UiFontFamily = IsChinese ? "Microsoft YaHei UI" : "Segoe UI";
        }

        /// <summary>当前是否使用中文界面。</summary>
        public static bool IsChinese { get; private set; }

        /// <summary>界面字体族：中文用雅黑 UI，英文用 Segoe UI（两者都是系统自带）。</summary>
        public static string UiFontFamily { get; private set; }

        public static string Get(string key)
        {
            string[] pair;
            if (!Table.TryGetValue(key, out pair)) return key;
            return IsChinese ? pair[0] : pair[1];
        }

        public static string Format(string key, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, Get(key), args);
        }
    }
}
