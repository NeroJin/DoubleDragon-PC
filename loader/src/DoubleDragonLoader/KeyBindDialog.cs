using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DoubleDragon.Loader
{
    /// <summary>
    /// 键盘映射对话框：Xbox 360 手柄全部 25 个可绑定动作，按"面键 / 肩键扳机 / 系统键 /
    /// 十字键 / 左摇杆 / 右摇杆"分 6 组、两栏排布（单列 25 行会比屏幕还高）。
    ///
    /// 改动立即写回 DoubleDragon.toml（和主界面一致），关闭对话框不需要"确定"。
    ///
    /// 注意与 overlay 热键的关系：F3 / F4 / F7 / GraveAccent（控制台）是运行时
    /// bind_* 的默认键。实测（2026-10-30）两套消费者都会响应——按一下 F4 会同时
    /// 打开设置面板并触发绑在 F4 上的手柄按键。所以捕到这些键时弹确认框，
    /// 冲突检测里也把它们预置成"占用者"，状态栏会常驻提醒。
    /// </summary>
    internal sealed class KeyBindDialog : Form
    {
        private static readonly Color DangerColor = Color.FromArgb(176, 48, 32);
        private static readonly Color WarnColor = Color.FromArgb(160, 96, 0);

        /// <summary>一组动作。cvar 名 -> 界面标签的 L10n key。</summary>
        private sealed class Section
        {
            public readonly string HeaderKey;
            public readonly string[,] Actions;

            public Section(string headerKey, string[,] actions)
            {
                HeaderKey = headerKey;
                Actions = actions;
            }
        }

        // 左列：面键、肩键/扳机、系统键（11 个）。
        private static readonly Section[] LeftSections =
        {
            new Section("SecFace", new[,]
            {
                { "keybind_a", "ActA" },
                { "keybind_b", "ActB" },
                { "keybind_x", "ActX" },
                { "keybind_y", "ActY" },
            }),
            new Section("SecShoulder", new[,]
            {
                { "keybind_left_shoulder",  "ActLB" },
                { "keybind_right_shoulder", "ActRB" },
                { "keybind_left_trigger",   "ActLT" },
                { "keybind_right_trigger",  "ActRT" },
            }),
            new Section("SecSystem", new[,]
            {
                { "keybind_start", "ActStart" },
                { "keybind_back",  "ActBack" },
                { "keybind_guide", "ActGuide" },
            }),
        };

        // 右列：十字键、左摇杆、右摇杆（14 个）。
        private static readonly Section[] RightSections =
        {
            new Section("SecDpad", new[,]
            {
                { "keybind_dpad_up",    "ActDpadUp" },
                { "keybind_dpad_down",  "ActDpadDown" },
                { "keybind_dpad_left",  "ActDpadLeft" },
                { "keybind_dpad_right", "ActDpadRight" },
            }),
            new Section("SecLStick", new[,]
            {
                { "keybind_lstick_up",    "ActLStickUp" },
                { "keybind_lstick_down",  "ActLStickDown" },
                { "keybind_lstick_left",  "ActLStickLeft" },
                { "keybind_lstick_right", "ActLStickRight" },
                { "keybind_lstick_press", "ActLStickPress" },
            }),
            new Section("SecRStick", new[,]
            {
                { "keybind_rstick_up",    "ActRStickUp" },
                { "keybind_rstick_down",  "ActRStickDown" },
                { "keybind_rstick_left",  "ActRStickLeft" },
                { "keybind_rstick_right", "ActRStickRight" },
                { "keybind_rstick_press", "ActRStickPress" },
            }),
        };

        /// <summary>overlay 热键的 cvar 与默认键。toml 里有活动行就用 toml 的值。</summary>
        private static readonly string[,] OverlayBinds =
        {
            { "bind_debug_overlay", "F3",          "OvDebug" },
            { "bind_console",       "GraveAccent", "OvConsole" },
            { "bind_settings",      "F4",          "OvSettings" },
            { "bind_achievements",  "F7",          "OvAchievements" },
        };

        private readonly TomlConfig _toml;
        private readonly List<BindRow> _rows = new List<BindRow>();
        private Label _status;

        /// <summary>overlay 热键当前占用的键 -> 占用者名字（用于冲突提示与捕获确认）。</summary>
        private readonly Dictionary<string, string> _overlayOwners =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public KeyBindDialog(TomlConfig toml)
        {
            _toml = toml;
            SeedOverlayOwners();
            BuildUi();
            RefreshRows();
        }

        /// <summary>
        /// 算出"哪些键被 overlay 占着"：默认 F3 / F4 / F7 / GraveAccent，
        /// 但 toml 里写了 bind_* 的活动行就以它为准。值解析不出来 = 这个热键
        /// 现在是死的，不占任何键（只有"没写"时才回落到默认键）。
        /// </summary>
        private void SeedOverlayOwners()
        {
            for (int i = 0; i < OverlayBinds.GetLength(0); i++)
            {
                string cvar = OverlayBinds[i, 0];
                string defaultKey = OverlayBinds[i, 1];
                string labelKey = OverlayBinds[i, 2];

                string value = _toml.GetString(cvar, null);
                string key;
                if (string.IsNullOrWhiteSpace(value))
                {
                    key = defaultKey;
                }
                else
                {
                    key = value.Split(',')[0].Trim();
                    if (!KeyMap.IsKnown(key)) continue;
                }

                if (!_overlayOwners.ContainsKey(key))
                {
                    _overlayOwners.Add(key, L10n.Get(labelKey));
                }
            }
        }

        // ================================================================== UI

        private void BuildUi()
        {
            var designFont = new Font(L10n.UiFontFamily, 9F, FontStyle.Regular, GraphicsUnit.Point);

            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = MainForm.MeasureDesignFont(designFont);
            Font = designFont;

            Text = L10n.Get("KeyMapTitle");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Padding = new Padding(12, 10, 12, 10);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            // 没有「关闭」按钮，Esc 和右上角 X 都能退出；改动已经即时写盘了。
            KeyPreview = true;
            KeyDown += OnDialogKeyDown;

            int leftRows = CountRows(LeftSections);
            int rightRows = CountRows(RightSections);
            int bodyRows = Math.Max(leftRows, rightRows);

            // 主界面把 mnk_mode 关掉时，这里改的键位一个都不会生效——必须说清楚，
            // 否则用户会以为改坏了。排在最上面（橙色），比状态行醒目。
            bool mnkOff = !_toml.GetBool("mnk_mode", true);
            int tailRows = mnkOff ? 4 : 3;   // [警告] + 说明 + 状态行

            var grid = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 8,
                RowCount = bodyRows + tailRows
            };
            for (int i = 0; i < 8; i++)
            {
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            }
            for (int i = 0; i < bodyRows + tailRows; i++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            // 两栏各一份表头（第 0 行）。
            grid.Controls.Add(MakeHeader("ColAction"), 0, 0);
            grid.Controls.Add(MakeHeader("ColKey"), 1, 0);
            grid.Controls.Add(MakeHeader("ColAction"), 4, 0);
            grid.Controls.Add(MakeHeader("ColKey"), 5, 0);

            AddSide(grid, LeftSections, 0, 1);
            AddSide(grid, RightSections, 4, 1);

            int tail = bodyRows + 1;

            if (mnkOff)
            {
                var off = new Label
                {
                    Text = L10n.Get("KeyMnkOff"),
                    AutoSize = true,
                    MaximumSize = new Size(680, 0),
                    ForeColor = WarnColor,
                    Margin = new Padding(0, 8, 0, 0)
                };
                grid.Controls.Add(off, 0, tail);
                grid.SetColumnSpan(off, 8);
                tail++;
            }

            // 说明。给个最大宽度让它折行，否则一行排到底会把窗口撑得过宽。
            var intro = new Label
            {
                Text = L10n.Get("KeyMapIntro"),
                AutoSize = true,
                MaximumSize = new Size(680, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 10, 0, 0)
            };
            grid.Controls.Add(intro, 0, tail);
            grid.SetColumnSpan(intro, 8);
            tail++;

            // 状态行（保存结果 / 冲突提示 / 无效键名提示）
            _status = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(680, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 4, 0, 0)
            };
            grid.Controls.Add(_status, 0, tail);
            grid.SetColumnSpan(_status, 8);

            Controls.Add(grid);
        }

        private static int CountRows(Section[] sections)
        {
            int rows = 0;
            foreach (Section section in sections)
            {
                rows += 1 + section.Actions.GetLength(0);   // 组头 + 动作行
            }
            return rows;
        }

        /// <summary>把一列分组画进 grid。colBase 是这列第一列的下标（左列 0、右列 4）。</summary>
        private int AddSide(TableLayoutPanel grid, Section[] sections, int colBase, int row)
        {
            foreach (Section section in sections)
            {
                var header = new Label
                {
                    Text = L10n.Get(section.HeaderKey),
                    AutoSize = true,
                    Font = new Font(L10n.UiFontFamily, 9F, FontStyle.Bold, GraphicsUnit.Point),
                    Margin = new Padding(0, 8, 12, 2)
                };
                grid.Controls.Add(header, colBase, row);
                grid.SetColumnSpan(header, 4);
                row++;

                for (int i = 0; i < section.Actions.GetLength(0); i++)
                {
                    string actionLabel = L10n.Get(section.Actions[i, 1]);
                    grid.Controls.Add(new Label
                    {
                        Text = actionLabel,
                        AutoSize = true,
                        TextAlign = ContentAlignment.MiddleLeft,
                        Margin = new Padding(0, 4, 12, 4)
                    }, colBase, row);

                    var value = new Label
                    {
                        AutoSize = true,
                        TextAlign = ContentAlignment.MiddleLeft,
                        Margin = new Padding(0, 4, 12, 4)
                    };
                    grid.Controls.Add(value, colBase + 1, row);

                    var bound = new BindRow(section.Actions[i, 0], actionLabel, value);

                    var setButton = new Button
                    {
                        Text = L10n.Get("KeySet"),
                        AutoSize = true,
                        AutoSizeMode = AutoSizeMode.GrowAndShrink,
                        MinimumSize = new Size(72, 26),
                        Margin = new Padding(0, 2, 6, 2),
                        UseVisualStyleBackColor = true,
                        Tag = bound
                    };
                    setButton.Click += OnSetClick;
                    grid.Controls.Add(setButton, colBase + 2, row);
                    bound.SetButton = setButton;

                    var clearButton = new Button
                    {
                        Text = L10n.Get("KeyClear"),
                        AutoSize = true,
                        AutoSizeMode = AutoSizeMode.GrowAndShrink,
                        MinimumSize = new Size(64, 26),
                        Margin = new Padding(0, 2, 0, 2),
                        UseVisualStyleBackColor = true,
                        Tag = bound
                    };
                    clearButton.Click += OnClearClick;
                    grid.Controls.Add(clearButton, colBase + 3, row);
                    bound.ClearButton = clearButton;

                    _rows.Add(bound);
                    row++;
                }
            }
            return row;
        }

        private static Label MakeHeader(string key)
        {
            return new Label
            {
                Text = L10n.Get(key),
                AutoSize = true,
                Font = new Font(L10n.UiFontFamily, 9F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(0, 0, 12, 4)
            };
        }

        // ============================================================ 修改与保存

        private void OnDialogKeyDown(object sender, KeyEventArgs e)
        {
            // Esc 退出本窗口；子对话框打开时这里收不到（模态）。
            if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
        }

        private void OnSetClick(object sender, EventArgs e)
        {
            var row = ((Button)sender).Tag as BindRow;
            if (row == null) return;

            string key = KeyCaptureDialog.Ask(this);
            if (key == null) return;

            // overlay 热键：实测（2026-10-30）两套消费者都会响应，按一下同时
            // 开面板 + 触发动作。不拦，但必须让用户知道自己在干什么。
            string owner;
            if (_overlayOwners.TryGetValue(key, out owner))
            {
                var answer = MessageBox.Show(
                    this,
                    L10n.Format("KeyReservedConfirm", key, owner),
                    Text,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return;
            }

            _toml.SetString(row.Cvar, key);
            SaveAndRefresh();
        }

        private void OnClearClick(object sender, EventArgs e)
        {
            var row = ((Button)sender).Tag as BindRow;
            if (row == null) return;

            // 注释掉而不是写空串：空串同样解析成 kNone，但会让人以为"绑过了"。
            // 注释掉等于"交回运行时默认"，语义清楚。
            _toml.CommentOut(row.Cvar);
            SaveAndRefresh();
        }

        private void SaveAndRefresh()
        {
            bool saved = true;
            try
            {
                _toml.Save();
            }
            catch (Exception ex)
            {
                saved = false;
                _status.ForeColor = DangerColor;
                _status.Text = L10n.Format("KeyWriteFailed", ex.Message);
            }
            RefreshRows();
            if (saved)
            {
                _status.ForeColor = SystemColors.GrayText;
                _status.Text = L10n.Get("KeySaved");
            }
        }

        private void RefreshRows()
        {
            // overlay 热键先入册：用户绑了同一个键就会自然报冲突。
            var firstOwner = new Dictionary<string, string>(_overlayOwners, StringComparer.Ordinal);
            string conflict = null;
            string unknown = null;
            int deadDefaults = 0;

            foreach (BindRow row in _rows)
            {
                string value = _toml.GetString(row.Cvar, null);
                if (string.IsNullOrEmpty(value))
                {
                    // 没绑 = 交回运行时默认。不能只写"默认"，得把出厂的键摊出来：
                    // 用户没法从别处知道默认是什么，而且其中一批默认值运行时解析不出来。
                    row.ClearButton.Enabled = false;

                    string fallback = KeyMap.DefaultFor(row.Cvar);
                    if (string.IsNullOrEmpty(fallback))
                    {
                        row.ValueLabel.Text = L10n.Get("KeyDefaultNone");
                        row.ValueLabel.ForeColor = SystemColors.GrayText;
                    }
                    else if (KeyMap.HasUnknownToken(fallback))
                    {
                        deadDefaults++;
                        row.ValueLabel.Text = L10n.Format("KeyDefaultBroken", PrettyKeys(fallback));
                        row.ValueLabel.ForeColor = WarnColor;
                    }
                    else
                    {
                        row.ValueLabel.Text = L10n.Format("KeyDefaultFmt", PrettyKeys(fallback));
                        row.ValueLabel.ForeColor = SystemColors.GrayText;
                    }
                    continue;
                }

                row.ValueLabel.Text = value;
                row.ClearButton.Enabled = true;

                bool bad = false;
                foreach (string token in value.Split(','))
                {
                    string name = token.Trim();
                    if (name.Length == 0) continue;

                    if (!KeyMap.IsKnown(name))
                    {
                        bad = true;
                        if (unknown == null)
                        {
                            unknown = L10n.Format("KeyUnknownToken", row.ActionLabel, name);
                        }
                        continue;
                    }

                    string owner;
                    if (firstOwner.TryGetValue(name, out owner))
                    {
                        if (conflict == null)
                        {
                            conflict = L10n.Format("KeyConflict", owner, row.ActionLabel);
                        }
                    }
                    else
                    {
                        firstOwner.Add(name, row.ActionLabel);
                    }
                }

                row.ValueLabel.ForeColor = bad ? DangerColor : SystemColors.ControlText;
            }

            // 无效键名优先显示（硬错），其次是冲突/占用（行为提醒），最后才提出厂默认的死键。
            if (unknown != null)
            {
                _status.ForeColor = DangerColor;
                _status.Text = unknown;
            }
            else if (conflict != null)
            {
                _status.ForeColor = WarnColor;
                _status.Text = conflict;
            }
            else if (deadDefaults > 0)
            {
                _status.ForeColor = WarnColor;
                _status.Text = L10n.Format("KeyDefaultBrokenCount", deadDefaults);
            }
            else
            {
                _status.ForeColor = SystemColors.GrayText;
                _status.Text = L10n.Get("KeyHintIdle");
            }
        }

        /// <summary>"Semicolon,Space" -> "Semicolon / Space"，只影响显示，写盘仍是原串。</summary>
        private static string PrettyKeys(string value)
        {
            var parts = new List<string>();
            foreach (string token in value.Split(','))
            {
                string name = token.Trim();
                if (name.Length > 0) parts.Add(name);
            }
            return string.Join(" / ", parts.ToArray());
        }

        /// <summary>一行绑定。ValueLabel 用于回显，ActionLabel 用于提示文案。</summary>
        private sealed class BindRow
        {
            public readonly string Cvar;
            public readonly string ActionLabel;
            public readonly Label ValueLabel;
            public Button SetButton;
            public Button ClearButton;

            public BindRow(string cvar, string actionLabel, Label valueLabel)
            {
                Cvar = cvar;
                ActionLabel = actionLabel;
                ValueLabel = valueLabel;
            }
        }

        // ============================================================ 按键捕获

        /// <summary>
        /// 弹一个"按任意键"的小窗，返回运行时键名；用户按 Esc 或关窗返回 null。
        /// 纯修饰键会被忽略（继续等），因为单绑一个 Shift 基本没用，还容易误触。
        /// </summary>
        private sealed class KeyCaptureDialog : Form
        {
            private readonly Label _hint;
            private string _result;

            private KeyCaptureDialog()
            {
                var designFont = new Font(L10n.UiFontFamily, 9F, FontStyle.Regular, GraphicsUnit.Point);

                AutoScaleMode = AutoScaleMode.Font;
                AutoScaleDimensions = MainForm.MeasureDesignFont(designFont);
                Font = designFont;

                Text = L10n.Get("KeyCaptureTitle");
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                MinimizeBox = false;
                MaximizeBox = false;
                ShowInTaskbar = false;
                ClientSize = new Size(320, 88);
                KeyPreview = true;

                _hint = new Label
                {
                    Text = L10n.Get("KeyCaptureHint"),
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Padding = new Padding(12)
                };
                Controls.Add(_hint);

                KeyDown += OnKeyDown;
            }

            public static string Ask(IWin32Window owner)
            {
                using (var dialog = new KeyCaptureDialog())
                {
                    dialog.ShowDialog(owner);
                    return dialog._result;
                }
            }

            private void OnKeyDown(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    _result = null;
                    DialogResult = DialogResult.Cancel;
                    Close();
                    return;
                }

                if (KeyMap.IsModifierOnly(e.KeyCode))
                {
                    _hint.Text = L10n.Get("KeyCaptureModifier");
                    return;
                }

                string name = KeyMap.FromKey(e.KeyCode);
                if (name == null)
                {
                    _hint.Text = L10n.Get("KeyCaptureUnsupported");
                    return;
                }

                _result = name;
                DialogResult = DialogResult.OK;
                Close();
            }
        }
    }
}
