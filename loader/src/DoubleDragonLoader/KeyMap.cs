using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace DoubleDragon.Loader
{
    /// <summary>
    /// WinForms 按键 -> ReXGlue 键名 的映射，以及"哪些键名运行时真的认得"的校验表。
    ///
    /// 为什么需要这张表：keybind_* 存的是字符串，由运行时的键名解析函数转成 VirtualKey。
    /// 名字查不到时返回 kNone —— 不报错、不写日志，按键就是没反应，非常难查。
    /// 所以这里的两份数据都是从 rexruntime.dll 的键名表里逐个核对出来的，不要凭印象拼：
    ///   回车 = Enter（不是 Return）      反引号 = GraveAccent（不是 Backtick）
    ///   引号 = Apostrophe（不是 Quote）  方向键 = UpArrow / DownArrow / LeftArrow / RightArrow
    ///   小键盘 = Keypad0..Keypad9 / KeypadEnter ...
    /// </summary>
    internal static class KeyMap
    {
        /// <summary>运行时键名解析表（155 项，含鼠标与手柄项）。表外的一律会被解析成 kNone。</summary>
        private static readonly HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal)
        {
            "Tab", "LeftArrow", "RightArrow", "UpArrow", "DownArrow",
            "PageUp", "PageDown", "Home", "End", "Insert", "Delete", "Backspace",
            "Space", "Enter", "Escape",
            "LeftCtrl", "LeftShift", "LeftAlt", "LeftSuper",
            "RightCtrl", "RightShift", "RightAlt", "RightSuper",
            "Menu",
            "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
            "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
            "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
            "F13", "F14", "F15", "F16", "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24",
            "Apostrophe", "Comma", "Minus", "Period", "Slash", "Semicolon", "Equal",
            "LeftBracket", "Backslash", "RightBracket", "GraveAccent",
            "CapsLock", "ScrollLock", "NumLock", "PrintScreen", "Pause",
            "Keypad0", "Keypad1", "Keypad2", "Keypad3", "Keypad4",
            "Keypad5", "Keypad6", "Keypad7", "Keypad8", "Keypad9",
            "KeypadDecimal", "KeypadDivide", "KeypadMultiply", "KeypadSubtract",
            "KeypadAdd", "KeypadEnter", "KeypadEqual",
            "AppBack", "AppForward", "Oem102",
            "GamepadStart", "GamepadBack",
            "GamepadFaceLeft", "GamepadFaceRight", "GamepadFaceUp", "GamepadFaceDown",
            "GamepadDpadLeft", "GamepadDpadRight", "GamepadDpadUp", "GamepadDpadDown",
            "GamepadL1", "GamepadR1", "GamepadL2", "GamepadR2", "GamepadL3", "GamepadR3",
            "GamepadLStickLeft", "GamepadLStickRight", "GamepadLStickUp", "GamepadLStickDown",
            "GamepadRStickLeft", "GamepadRStickRight", "GamepadRStickUp", "GamepadRStickDown",
            "MouseLeft", "MouseRight", "MouseMiddle", "MouseX1", "MouseX2",
            "MouseWheelX", "MouseWheelY",
            "ModCtrl", "ModShift", "ModAlt", "ModSuper",
        };

        /// <summary>
        /// 运行时给每个 keybind_* 的出厂默认值。来源：rexruntime.dll 里 keybind 注册表
        /// （每个 cvar 依次是 名称 / 显示名 / 默认值），按显示名逐个对照得出，25 项齐全。
        ///
        /// 注意：其中有一批默认值的键名运行时根本解析不出来（见 IsKnown），也就是
        /// 这些动作出厂就等于没绑，想用必须自己绑：
        ///   B  的 Quote        → 只剩 Backspace 有用
        ///   右摇杆 的 Up / Down / Left / Right     （表里是 UpArrow / DownArrow / ...）
        ///   十字键 的 Shift+Up / +Down / +Left / +Right
        ///   START 的 Return    → 只剩 X 有用
        /// 所以界面上不能只写"（运行时默认）"，得把值摊开给用户看。
        /// </summary>
        private static readonly Dictionary<string, string> RuntimeDefaults =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "keybind_a",              "Semicolon,Space" },
            { "keybind_b",              "Quote,Backspace" },
            { "keybind_x",              "L" },
            { "keybind_y",              "P" },
            { "keybind_left_trigger",   "Q,I" },
            { "keybind_right_trigger",  "E,O" },
            { "keybind_left_shoulder",  "1" },
            { "keybind_right_shoulder", "3" },
            { "keybind_lstick_up",      "W" },
            { "keybind_lstick_down",    "S" },
            { "keybind_lstick_left",    "A" },
            { "keybind_lstick_right",   "D" },
            { "keybind_lstick_press",   "F" },
            { "keybind_rstick_up",      "Up" },
            { "keybind_rstick_down",    "Down" },
            { "keybind_rstick_left",    "Left" },
            { "keybind_rstick_right",   "Right" },
            { "keybind_rstick_press",   "K" },
            { "keybind_dpad_up",        "Shift+Up" },
            { "keybind_dpad_down",      "Shift+Down" },
            { "keybind_dpad_left",      "Shift+Left" },
            { "keybind_dpad_right",     "Shift+Right" },
            { "keybind_back",           "Z,Tab" },
            { "keybind_start",          "X,Return" },
            { "keybind_guide",          "" },
        };

        /// <summary>WinForms 键码 -> 运行时键名。捕获按键时按这张表翻译。</summary>
        private static readonly Dictionary<Keys, string> FromWinForms = BuildMap();

        /// <summary>
        /// 某个 keybind_* 的运行时默认值。空串表示出厂就没绑（GUIDE），
        /// 返回 null 表示这张表里没有这个 cvar。
        /// </summary>
        public static string DefaultFor(string cvar)
        {
            string value;
            return RuntimeDefaults.TryGetValue(cvar ?? string.Empty, out value) ? value : null;
        }

        /// <summary>
        /// 一串键名里有没有运行时认不出的（这种默认值等于没绑）。
        /// 用于把"出厂就是死的"和"用户自己写错"区分开。
        /// </summary>
        public static bool HasUnknownToken(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;

            foreach (string token in value.Split(','))
            {
                string name = token.Trim();
                if (name.Length > 0 && !IsKnown(name)) return true;
            }
            return false;
        }

        /// <summary>键名是否在运行时解析表里。false 表示这一项绑了也不会生效。</summary>
        public static bool IsKnown(string name)
        {
            return name != null && Names.Contains(name);
        }

        /// <summary>
        /// 同 <see cref="IsKnown(string)"/>，但先把两端空白去掉——toml 里常见 "A, Space" 这种写法。
        /// </summary>
        public static bool IsKnownToken(string token)
        {
            return IsKnown((token ?? string.Empty).Trim());
        }

        /// <summary>
        /// WinForms 的按键 -> 运行时键名；不支持时返回 null（比如单独的 Shift/Ctrl/Alt）。
        /// </summary>
        public static string FromKey(Keys key)
        {
            Keys code = key & Keys.KeyCode;
            string name;
            return FromWinForms.TryGetValue(code, out name) ? name : null;
        }

        /// <summary>纯修饰键（Shift / Ctrl / Alt / Win）。捕获时先跳过它们，等真按键。</summary>
        public static bool IsModifierOnly(Keys key)
        {
            Keys code = key & Keys.KeyCode;
            switch (code)
            {
                case Keys.ShiftKey:
                case Keys.ControlKey:
                case Keys.Menu:
                case Keys.LWin:
                case Keys.RWin:
                case Keys.LShiftKey:
                case Keys.RShiftKey:
                case Keys.LControlKey:
                case Keys.RControlKey:
                case Keys.LMenu:
                case Keys.RMenu:
                    return true;
                default:
                    return false;
            }
        }

        private static Dictionary<Keys, string> BuildMap()
        {
            var map = new Dictionary<Keys, string>();

            for (Keys k = Keys.A; k <= Keys.Z; k++)
            {
                map[k] = k.ToString();
            }
            for (int i = 0; i <= 9; i++)
            {
                map[(Keys)((int)Keys.D0 + i)] = i.ToString();
                map[(Keys)((int)Keys.NumPad0 + i)] = "Keypad" + i;
            }
            for (int i = 0; i < 24; i++)
            {
                map[(Keys)((int)Keys.F1 + i)] = "F" + (i + 1);
            }

            map[Keys.Up] = "UpArrow";
            map[Keys.Down] = "DownArrow";
            map[Keys.Left] = "LeftArrow";
            map[Keys.Right] = "RightArrow";

            map[Keys.Space] = "Space";
            map[Keys.Enter] = "Enter";
            map[Keys.Escape] = "Escape";
            map[Keys.Tab] = "Tab";
            map[Keys.Back] = "Backspace";
            map[Keys.Delete] = "Delete";
            map[Keys.Insert] = "Insert";
            map[Keys.Home] = "Home";
            map[Keys.End] = "End";
            map[Keys.PageUp] = "PageUp";
            map[Keys.PageDown] = "PageDown";
            map[Keys.CapsLock] = "CapsLock";
            map[Keys.Scroll] = "ScrollLock";
            map[Keys.NumLock] = "NumLock";
            map[Keys.PrintScreen] = "PrintScreen";
            map[Keys.Pause] = "Pause";

            // 主键盘上的符号键：名字取自 US 布局，和 OemKey 的物理位置一一对应。
            map[Keys.OemSemicolon] = "Semicolon";
            map[Keys.Oemplus] = "Equal";
            map[Keys.Oemcomma] = "Comma";
            map[Keys.OemMinus] = "Minus";
            map[Keys.OemPeriod] = "Period";
            map[Keys.OemQuestion] = "Slash";
            map[Keys.Oemtilde] = "GraveAccent";
            map[Keys.OemOpenBrackets] = "LeftBracket";
            map[Keys.OemPipe] = "Backslash";
            map[Keys.OemCloseBrackets] = "RightBracket";
            map[Keys.OemQuotes] = "Apostrophe";
            map[Keys.OemBackslash] = "Oem102";

            map[Keys.LShiftKey] = "LeftShift";
            map[Keys.RShiftKey] = "RightShift";
            map[Keys.LControlKey] = "LeftCtrl";
            map[Keys.RControlKey] = "RightCtrl";
            map[Keys.LMenu] = "LeftAlt";
            map[Keys.RMenu] = "RightAlt";
            map[Keys.LWin] = "LeftSuper";
            map[Keys.RWin] = "RightSuper";

            map[Keys.Add] = "KeypadAdd";
            map[Keys.Subtract] = "KeypadSubtract";
            map[Keys.Multiply] = "KeypadMultiply";
            map[Keys.Divide] = "KeypadDivide";
            map[Keys.Decimal] = "KeypadDecimal";

            return map;
        }
    }
}
