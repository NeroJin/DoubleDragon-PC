using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoubleDragon.Unpack;

namespace DoubleDragon.Loader
{
    /// <summary>
    /// 启动器主窗体：解包资源 / 改画质设置 / 拉起游戏。
    ///
    /// 布局全部用 TableLayoutPanel + AutoSize 构建（不用像素坐标），
    /// 配合 AutoScaleMode.Font，在 125% / 150% / 200% 缩放下按比例放大而不错位。
    /// </summary>
    internal sealed class MainForm : Form
    {
        // ---- 固定约定：loader 与游戏同目录 ----
        private const string GameExeName = "DoubleDragon.exe";
        private const string TomlName = "DoubleDragon.toml";
        private const string DataFolderName = "game";
        private const string ManagedAaIgnored = "none";
        /// <summary>简体中文资源包目录（随 Loader 分发，位于 exe 同目录）。</summary>
        private const string LangPayloadDir = "lang_zhcn";
        /// <summary>简体 .xus 字节补丁：以游戏自带 zh-cht 为基底复原 zh-cn，替代整包分发。</summary>
        private const string PatchFileName = "patch_v1.bin";

        /// <summary>
        /// 窗口尺寸预设（仅窗口模式生效，写入 window_width/height）。
        /// 内部渲染分辨率固定为原生 720p（resolution_scale = 1），所以这里只是
        /// 决定窗口的客户区像素大小：选得越大，720p 画面被 GPU 放大得越多。
        /// 其中 1280×720 / 2560×1440 / 3840×2160 是 720p 的整数倍（整数缩放、无半
        /// 像素偏移）；1920×1080 是 1.5 倍，可能引入非整数缩放，按需选用。
        /// </summary>
        private static readonly WindowSizeOption[] WindowSizeOptions =
        {
            new WindowSizeOption(1280,  720),
            new WindowSizeOption(1920, 1080),
            new WindowSizeOption(2560, 1440),
            new WindowSizeOption(3840, 2160),
        };

        /// <summary>抗锯齿 cvar present_effect 的取值。</summary>
        private static readonly string[] AaValues = { "none", "fxaa", "fxaa_extreme" };
        private static readonly string[] AaKeys = { "AaNone", "AaFxaa", "AaFxaaExtreme" };

        private readonly string _baseDir;
        private readonly string _exePath;
        private readonly string _dataDir;
        private readonly TomlConfig _toml;

        /// <summary>布局根容器。窗体高度要按它的实际需求校准，否则加了新分组就会挤掉按钮行。</summary>
        private TableLayoutPanel _root;

        private ComboBox _aaCombo;
        /// <summary>界面语言：英文 / 繁体（原版）/ 简体（zh-cn 重定向）。</summary>
        private ComboBox _langCombo;
        /// <summary>关闭 Xbox LIVE 联网提示（游戏侧 xbl_off）。</summary>
        private CheckBox _xblOffCheck;
        private CheckBox _fullscreenCheck;
        /// <summary>窗口尺寸预设下拉：仅窗口模式可用，写入 window_width/height。</summary>
        private ComboBox _windowSizeCombo;
        /// <summary>mnk_mode：键盘是否模拟成 1P 手柄。关掉后键盘完全不参与游戏。</summary>
        private CheckBox _keyboardCheck;
        private Label _dataStatus;
        private Label _hint;
        private ProgressBar _progress;
        private Button _extractButton;
        private Button _keyMapButton;
        private Button _cancelButton;
        private Button _launchButton;

        private CancellationTokenSource _cts;
        private bool _busy;
        private bool _suspendSave;
        private bool _saveErrorShown;

        public MainForm(string baseDir)
        {
            _baseDir = baseDir;
            _exePath = Path.Combine(baseDir, GameExeName);
            _dataDir = Path.Combine(baseDir, DataFolderName);

            _toml = TomlConfig.LoadOrCreate(Path.Combine(baseDir, TomlName), TomlConfig.DefaultTemplate);

            BuildUi();
            LoadSettings();
            RefreshDataStatus();
        }

        // ================================================================== UI

        private void BuildUi()
        {
            var designFont = new Font(L10n.UiFontFamily, 9F, FontStyle.Regular, GraphicsUnit.Point);

            // Font 模式下 AutoScaleDimensions 必须是"设计时 96 DPI"下该字体的度量，
            // 否则 150% 屏上会二次放大。这里现算一遍，换字体也不用改常数。
            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = MeasureDesignFont(designFont);
            Font = designFont;

            Text = L10n.Get("AppTitle");
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ShowIcon = false;
            // 96 DPI 设计基准。三组内容实需约 388，留一点余量；其它 DPI 由 AutoScale
            // 按字体比例整体放大，OnLoad 里还会再按真实内容兜一次底。
            ClientSize = new Size(560, 400);
            Padding = new Padding(0);

            _root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(12, 10, 12, 10)
            };
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 画质
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 输入
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 游戏数据
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // 留白
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 按钮行

            _root.Controls.Add(BuildGraphicsGroup(), 0, 0);
            _root.Controls.Add(BuildInputGroup(), 0, 1);
            _root.Controls.Add(BuildDataGroup(), 0, 2);
            _root.Controls.Add(BuildButtonRow(), 0, 4);

            Controls.Add(_root);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FitToContent();
        }

        /// <summary>
        /// 按内容真实需要的高度校准窗体。必须在 AutoScale 完成之后调用（OnLoad 满足），
        /// 这样量到的是当前 DPI 下的实际像素，直接补差值即可，不会再被缩放一次。
        /// 字体比例缩放取整后常常比设计值高几像素，靠这一步兜住，避免按钮行被挤出窗口。
        /// </summary>
        private void FitToContent()
        {
            if (_root == null)
            {
                return;
            }

            int needed = _root.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
            if (needed > ClientSize.Height)
            {
                ClientSize = new Size(ClientSize.Width, needed);
            }
        }

        private GroupBox BuildGraphicsGroup()
        {
            var group = new GroupBox
            {
                Text = L10n.Get("GroupGraphics"),
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 8),
                Margin = new Padding(0, 0, 0, 8)
            };

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 5
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
            for (int i = 0; i < 5; i++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            // 抗锯齿
            grid.Controls.Add(MakeLabel("LabelAa"), 0, 0);
            _aaCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 3, 0, 3)
            };
            for (int i = 0; i < AaValues.Length; i++)
            {
                _aaCombo.Items.Add(new AaOption(AaValues[i], L10n.Get(AaKeys[i])));
            }
            _aaCombo.SelectedIndexChanged += OnSettingChanged;
            grid.Controls.Add(_aaCombo, 1, 0);

            // 界面语言：写 user_language + ui_lang_simp 两个 cvar。
            // 简体 = 游戏在载入后把内存里的语言目录名 zh-cht 改写成 zh-cn，
            // 读本启动器解包目录里的简体串表（原版繁体资源零改动）。
            grid.Controls.Add(MakeLabel("LabelLang"), 0, 1);
            _langCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 3, 0, 3)
            };
            _langCombo.Items.Add("English");
            _langCombo.Items.Add("繁體中文（原版）");
            _langCombo.Items.Add("简体中文");
            _langCombo.SelectedIndex = 0;
            _langCombo.SelectedIndexChanged += OnSettingChanged;
            grid.Controls.Add(_langCombo, 1, 1);

            // 全屏（无边框 + 桌面分辨率；fullscreen_exclusive 保持默认关闭）
            _fullscreenCheck = new CheckBox
            {
                Text = L10n.Get("Fullscreen"),
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0)
            };
            _fullscreenCheck.CheckedChanged += OnSettingChanged;
            grid.Controls.Add(_fullscreenCheck, 0, 2);
            grid.SetColumnSpan(_fullscreenCheck, 2);

            // 窗口尺寸预设（仅窗口模式可用，全屏时禁用）。写入 window_width/height；
            // SaveSettings 会注释掉 resolution 预设，使 window_* 优先生效。
            grid.Controls.Add(MakeLabel("LabelWindowSize"), 0, 3);
            _windowSizeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 3, 0, 3)
            };
            foreach (WindowSizeOption option in WindowSizeOptions)
            {
                _windowSizeCombo.Items.Add(option);
            }
            _windowSizeCombo.SelectedIndexChanged += OnSettingChanged;
            grid.Controls.Add(_windowSizeCombo, 1, 3);

            // 关闭联网提示（xbl_off）：勾上后游戏不再弹「未连线至 Xbox Live」等提示框。
            _xblOffCheck = new CheckBox
            {
                Text = L10n.Get("DisableLive"),
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0)
            };
            _xblOffCheck.CheckedChanged += OnSettingChanged;
            grid.Controls.Add(_xblOffCheck, 0, 4);
            grid.SetColumnSpan(_xblOffCheck, 2);

            group.Controls.Add(grid);
            return group;
        }

        /// <summary>
        /// 输入组：只放一个开关（mnk_mode）。默认开着——和以前"强制打开"的行为一致；
        /// 只用实体手柄的人可以关掉，免得误碰键盘（尤其空格/分号）触发游戏动作。
        /// 键位本身在按钮行的「键盘映射…」里改。
        /// </summary>
        private GroupBox BuildInputGroup()
        {
            var group = new GroupBox
            {
                Text = L10n.Get("GroupInput"),
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 8),
                Margin = new Padding(0, 0, 0, 8)
            };

            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _keyboardCheck = new CheckBox
            {
                Text = L10n.Get("KeyboardOn"),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2)
            };
            _keyboardCheck.CheckedChanged += OnSettingChanged;
            stack.Controls.Add(_keyboardCheck, 0, 0);

            var hint = new Label
            {
                Text = L10n.Get("HintKeyboard"),
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(18, 0, 0, 0)
            };
            stack.Controls.Add(hint, 0, 1);

            group.Controls.Add(stack);
            return group;
        }

        private GroupBox BuildDataGroup()
        {
            var group = new GroupBox
            {
                Text = L10n.Get("GroupData"),
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 8),
                Margin = new Padding(0, 0, 0, 8)
            };

            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 3
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _dataStatus = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 4)
            };
            stack.Controls.Add(_dataStatus, 0, 0);

            _progress = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 1000,
                Style = ProgressBarStyle.Continuous,
                Margin = new Padding(0, 2, 0, 4)
            };
            stack.Controls.Add(_progress, 0, 1);

            _hint = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                ForeColor = SystemColors.GrayText,
                Text = L10n.Get("HintIdle"),
                Margin = new Padding(0)
            };
            stack.Controls.Add(_hint, 0, 2);

            group.Controls.Add(stack);
            return group;
        }

        private Control BuildButtonRow()
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0)
            };

            _launchButton = MakeButton("Launch", 132);
            _launchButton.Font = new Font(Font, FontStyle.Bold);
            _launchButton.Click += OnLaunchClick;

            _cancelButton = MakeButton("Cancel", 88);
            _cancelButton.Enabled = false;
            _cancelButton.Click += OnCancelClick;

            _extractButton = MakeButton("Extract", 132);
            _extractButton.Click += OnExtractClick;

            // 键盘映射：写 keybind_* 那一段。放在按钮行而不是新开一组，
            // 是因为它和"解包"一样是低频操作，不值得占主面板一行的高度。
            _keyMapButton = MakeButton("KeyMap", 116);
            _keyMapButton.Click += OnKeyMapClick;

            // RightToLeft 流式布局：先加的在最右，所以顺序是"启动 | 取消 | 解包 | 键盘映射"。
            flow.Controls.Add(_launchButton);
            flow.Controls.Add(_cancelButton);
            flow.Controls.Add(_extractButton);
            flow.Controls.Add(_keyMapButton);
            return flow;
        }

        private static Label MakeLabel(string key)
        {
            return new Label
            {
                Text = L10n.Get(key),
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 3, 6, 3)
            };
        }

        private static Button MakeButton(string key, int minWidth)
        {
            return new Button
            {
                Text = L10n.Get(key),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(minWidth, 30),
                Margin = new Padding(6, 0, 0, 0),
                UseVisualStyleBackColor = true
            };
        }

        /// <summary>按 96 DPI 计算字体的自动缩放基准尺寸（与 WinForms 内部度量方式一致）。</summary>
        internal static SizeF MeasureDesignFont(Font font)
        {
            using (var bitmap = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                Size size = TextRenderer.MeasureText(
                    graphics,
                    "The quick brown fox jumped over the lazy dog",
                    font,
                    new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding);
                return new SizeF(size.Width, size.Height);
            }
        }

        // ============================================================ 设置读写

        private void LoadSettings()
        {
            _suspendSave = true;
            try
            {
                _aaCombo.SelectedIndex = AaIndexOf(_toml.GetString("present_effect", ManagedAaIgnored));
                bool fullscreen = _toml.GetBool("fullscreen", false);
                _fullscreenCheck.Checked = fullscreen;
                // 缺这个键时按"开"处理：老 toml 里没有 mnk_mode，而以前启动器
                // 每次保存都会强写 true，所以缺省视为开才不会把人的键盘"弄没了"。
                _keyboardCheck.Checked = _toml.GetBool("mnk_mode", true);
                // 窗口尺寸：非全屏且 toml 里有活动的 window_width/height 时，按值匹配
                // 下拉项；否则（含全屏）用默认首项 1280×720。
                int w = _toml.GetInt("window_width", 0);
                int h = _toml.GetInt("window_height", 0);
                _windowSizeCombo.SelectedIndex = WindowSizeIndexFor(w, h);
                // 界面语言：user_language 8=繁中；简体 = 8 + ui_lang_simp。
                int lang = _toml.GetInt("user_language", 1);
                bool simp = _toml.GetBool("ui_lang_simp", false);
                _langCombo.SelectedIndex = simp && lang == 8 ? 2 : (lang == 8 ? 1 : 0);
                // 联网提示开关：默认勾选（游戏侧 cvar 默认也是 true）。
                _xblOffCheck.Checked = _toml.GetBool("xbl_off", true);
            }
            finally
            {
                _suspendSave = false;
            }
            UpdateWindowSizeEnabled();
        }

        private void OnSettingChanged(object sender, EventArgs e)
        {
            if (_suspendSave || _busy) return;
            UpdateWindowSizeEnabled();
            SaveSettings(false);
        }

        /// <summary>
        /// 窗口尺寸下拉的可用性：仅在窗口模式可用；全屏时禁用（窗口尺寸无意义）。
        /// </summary>
        private void UpdateWindowSizeEnabled()
        {
            _windowSizeCombo.Enabled = !_busy && !_fullscreenCheck.Checked;
        }

        /// <summary>把当前界面值写进 toml。silent=true 时不弹错误框（启动流程里自己处理）。</summary>
        private bool SaveSettings(bool silent)
        {
            // 用户手改过 gpu_plugin 就尊重；缺失/为空时补上，否则游戏会黑屏。
            if (_toml.GetString("gpu_plugin", string.Empty).Trim().Length == 0)
            {
                _toml.SetString("gpu_plugin", "xenos");
            }

            // 内部渲染分辨率固定为原生 720p（resolution_scale = 1）：游戏改了内部
            // 渲染率会出现半像素偏移，所以这里不给用户改，直接写死。
            _toml.SetInt("resolution_scale", 1);
            _toml.SetString("present_effect", SelectedAa);
            _toml.SetBool("fullscreen", _fullscreenCheck.Checked);

            // 界面语言：英文(1) / 繁中(8) / 简中(8 + ui_lang_simp 重定向 zh-cn)。
            int langIndex = _langCombo.SelectedIndex >= 0 ? _langCombo.SelectedIndex : 0;
            _toml.SetInt("user_language", langIndex == 0 ? 1 : 8);
            _toml.SetBool("ui_lang_simp", langIndex == 2);
            _toml.SetBool("xbl_off", _xblOffCheck.Checked);

            // 窗口策略：resolution 预设优先级高于 window_width/height（见 ReXGlue
            // video_mode_util::ResolveConfiguredSize），所以两边互斥靠注释切换。
            if (_fullscreenCheck.Checked)
            {
                // 全屏：恢复 resolution 预设（窗口阶段可能已把它注释掉），沿用原全屏
                // 行为按预设分辨率渲染；窗口尺寸键只在窗口模式有意义，这里注释掉。
                // 用 Uncomment 而不是 Set，是为保留用户若在 toml 里手改过的分辨率值。
                _toml.Uncomment("resolution");
                _toml.CommentOut("window_width");
                _toml.CommentOut("window_height");
            }
            else
            {
                // 窗口：注释掉 resolution 预设，使 window_width/height 优先生效。
                _toml.CommentOut("resolution");
                WindowSizeOption size = SelectedWindowSize;
                _toml.SetInt("window_width", size.Width);
                _toml.SetInt("window_height", size.Height);
            }

            // 强制项，不给界面开关：
            // 撕裂/可变刷新率会让呈现用 0 同步间隔，游戏速度会失控；
            // 垂直同步必须开着（当前运行时还没注册这个 cvar，写上是为了将来生效）。
            _toml.SetBool("d3d12_allow_variable_refresh_rate_and_tearing", false);
            _toml.SetBool("vsync", true);

            // 键盘：mnk_mode 开 = 键盘模拟成一个虚拟手柄喂 1P（键位见「键盘映射…」）。
            // 关掉后键盘完全不参与游戏，适合只用实体手柄、怕误碰键盘触发动作的人。
            // 鼠标驱动右摇杆，本作不用右摇杆，固定关掉免得误操作。
            _toml.SetBool("mnk_mode", _keyboardCheck.Checked);
            _toml.SetBool("mnk_mouse", false);

            try
            {
                _toml.Save();
                return true;
            }
            catch (Exception ex)
            {
                if (!silent && !_saveErrorShown)
                {
                    _saveErrorShown = true;
                    MessageBox.Show(this, L10n.Format("SaveFailed", ex.Message), Text,
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return false;
            }
        }

        private WindowSizeOption SelectedWindowSize
        {
            get
            {
                var option = _windowSizeCombo.SelectedItem as WindowSizeOption;
                return option ?? WindowSizeOptions[0];
            }
        }

        private string SelectedAa
        {
            get
            {
                var option = _aaCombo.SelectedItem as AaOption;
                return option != null ? option.Value : ManagedAaIgnored;
            }
        }

        /// <summary>把 toml 里的 window 尺寸匹配到下拉项；不匹配时用默认首项 1280×720。</summary>
        private static int WindowSizeIndexFor(int w, int h)
        {
            for (int i = 0; i < WindowSizeOptions.Length; i++)
            {
                if (WindowSizeOptions[i].Width == w && WindowSizeOptions[i].Height == h) return i;
            }
            return 0;
        }

        private static int AaIndexOf(string value)
        {
            for (int i = 0; i < AaValues.Length; i++)
            {
                if (string.Equals(AaValues[i], value, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return 0;
        }

        // ================================================================ 解包

        private async void OnExtractClick(object sender, EventArgs e)
        {
            await RunExtractAsync();
        }

        /// <summary>
        /// 让用户挑包。启动器目录里如果能自动找到包，就把它预选好——
        /// 常见情况下直接回车即可；找不到也无所谓，用户可以在别处挑。
        /// 默认筛选"所有文件"，因为 STFS 包名字通常是一串十六进制、没有扩展名。
        /// </summary>
        private string PickPackage()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = L10n.Get("PickPackageTitle"),
                Filter = L10n.Get("PickPackageFilter"),
                FilterIndex = 2,
                CheckFileExists = true,
                RestoreDirectory = true,
                Multiselect = false
            })
            {
                string found = DoubleDragonPack.FindPackage(_baseDir);
                if (found != null)
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(found);
                    dialog.FileName = Path.GetFileName(found);
                }
                else
                {
                    dialog.InitialDirectory = _baseDir;
                }

                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            }
        }

        private async Task<bool> RunExtractAsync()
        {
            if (_busy) return false;

            string package = PickPackage();
            if (package == null) return false;

            if (DoubleDragonPack.IsDataInstalled(_dataDir))
            {
                if (MessageBox.Show(this, L10n.Get("ConfirmOverwrite"), Text,
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return false;
                }
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            SetBusy(true);
            _progress.Value = 0;

            // Progress<T> 会回到创建它的 UI 线程，所以回调里可以安全碰控件。
            var progress = new Progress<ExtractProgress>(OnExtractProgress);

            try
            {
                ExtractResult result = await Task.Run(
                    () => DoubleDragonPack.Install(package, _dataDir, null, progress, cts.Token));

                _progress.Value = _progress.Maximum;
                _hint.Text = L10n.Format("ExtractDone", result.FileCount, result.ByteCount,
                                         result.Elapsed.TotalSeconds);
                RefreshDataStatus();
                return true;
            }
            catch (OperationCanceledException)
            {
                _hint.Text = L10n.Get("Cancelled");
                return false;
            }
            catch (Exception ex)
            {
                _hint.Text = L10n.Get("HintIdle");
                MessageBox.Show(this, L10n.Format("ExtractFailed", ex.Message), Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally
            {
                _cts = null;
                cts.Dispose();
                SetBusy(false);
            }
        }

        private void OnExtractProgress(ExtractProgress progress)
        {
            int value = (int)Math.Round(progress.Percent * 10.0);
            _progress.Value = Math.Max(0, Math.Min(_progress.Maximum, value));
            _hint.Text = L10n.Format("ProgressItem", progress.FilesDone, progress.FilesTotal,
                                     progress.CurrentFile);
        }

        private void OnCancelClick(object sender, EventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cancelButton.Enabled = false;
            }
        }

        /// <summary>
        /// 键盘映射。改动由对话框自己写盘，这里不重复保存——
        /// 否则用一个旧界面的状态去 Save，反而可能把刚绑的键冲掉。
        /// </summary>
        private void OnKeyMapClick(object sender, EventArgs e)
        {
            if (_busy) return;
            using (var dialog = new KeyBindDialog(_toml))
            {
                dialog.ShowDialog(this);
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _aaCombo.Enabled = !busy;
            _fullscreenCheck.Enabled = !busy;
            _keyboardCheck.Enabled = !busy;
            UpdateWindowSizeEnabled();   // 窗口尺寸下拉还要看全屏状态，单独算
            _extractButton.Enabled = !busy;
            _keyMapButton.Enabled = !busy;
            _launchButton.Enabled = !busy;
            _cancelButton.Enabled = busy;
            UseWaitCursor = busy;
            if (!busy && _hint.Text.Length == 0)
            {
                _hint.Text = L10n.Get("HintIdle");
            }
        }

        private void RefreshDataStatus()
        {
            if (DoubleDragonPack.IsDataInstalled(_dataDir))
            {
                long size = 0;
                try
                {
                    size = new FileInfo(Path.Combine(_dataDir, DoubleDragonPack.ExecutableName)).Length;
                }
                catch (IOException)
                {
                    // 只用来显示，读不到就当 0。
                }
                catch (UnauthorizedAccessException)
                {
                }
                _dataStatus.Text = L10n.Format("DataReady", size);
                _dataStatus.ForeColor = SystemColors.ControlText;
            }
            else
            {
                _dataStatus.Text = L10n.Get("DataMissing");
                _dataStatus.ForeColor = Color.FromArgb(176, 48, 32);
            }
        }

        // ================================================================ 启动

        private async void OnLaunchClick(object sender, EventArgs e)
        {
            if (_busy) return;

            if (!DoubleDragonPack.IsDataInstalled(_dataDir))
            {
                DialogResult answer = MessageBox.Show(this, L10n.Get("LaunchNeedsData"), Text,
                                                       MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
                if (!await RunExtractAsync()) return;
            }

            if (!SaveSettings(false)) return;
            LaunchGame();
        }

        private void LaunchGame()
        {
            try
            {
                // 选了简体中文则在启动前把 game/UI/zh-cn 补齐（详见 EnsureSimplifiedChinese）。
                EnsureSimplifiedChinese();

                Process.Start(new ProcessStartInfo(_exePath)
                {
                    // 工作目录必须是 exe 所在目录：日志和 toml 都按它定位。
                    WorkingDirectory = _baseDir,
                    UseShellExecute = true
                });
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L10n.Format("LaunchFailed", ex.Message), Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ================================================================ 简体中文注入

        /// <summary>
        /// 选了简体中文（cvar ui_lang_simp=true）时，在拉起游戏前把 game/UI/zh-cn 树补齐。
        /// <para>
        /// 机制：游戏侧 <c>lang_redirect</c> 会把内存里的语言目录名 zh-cht 改写成 zh-cn，于是改读
        /// game/ui/zh-cn/ 下的资源。但 XBLA 原包没有 zh-cn，解包后也不会有，所以这里在本地重建它。
        /// </para>
        /// <para>
        /// 重建方式（manifest.txt 逐条描述 236 个目标文件）：
        /// 类型 <c>S</c> —— 42 个有差异的 .xus，改为"补丁式"：读游戏自带的同名 zh-cht 作基底，
        ///   校验基底 MD5 后按 patch_v1.bin 的 (offset, bytes) 就地改写并写到 zh-cn，不再随包携带整包 .xus；
        /// 类型 <c>N</c> —— 其余 194 个中性图片/音频，运行时从用户自己解开的游戏包（中性路径）拷贝，
        ///   不随 Loader 分发任何原始游戏素材。
        /// </para>
        /// <para>幂等：已存在则覆盖；注入失败不阻断启动（游戏回退繁体，并写 lang_inject.log）。</para>
        /// </summary>
        private void EnsureSimplifiedChinese()
        {
            if (!_toml.GetBool("ui_lang_simp", false)) return;

            string payloadDir = Path.Combine(_baseDir, LangPayloadDir);
            string manifestPath = Path.Combine(payloadDir, "manifest.txt");
            if (!File.Exists(manifestPath)) return; // 资源包缺失：静默回退繁体

            // 补丁缺失（旧包）时 S 类会被跳过，游戏对该屏回退繁体，不影响启动。
            string patchPath = Path.Combine(payloadDir, PatchFileName);
            Dictionary<string, PatchEntry> patches = File.Exists(patchPath)
                ? LoadPatch(patchPath)
                : new Dictionary<string, PatchEntry>(StringComparer.OrdinalIgnoreCase);

            string uiDir = Path.Combine(_dataDir, "UI");
            try
            {
                foreach (string raw in File.ReadAllLines(manifestPath))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string[] parts = raw.Split('|');
                    if (parts.Length < 2) continue;

                    string zhcnRel = parts[0].Replace('/', Path.DirectorySeparatorChar);
                    string target = Path.Combine(uiDir, zhcnRel);

                    if (parts[1] == "S")
                    {
                        // 简体 .xus：以游戏自带 zh-cht 为基底套字节补丁生成。
                        MaterializePatchedXus(zhcnRel, target, patches, uiDir);
                        continue;
                    }

                    // "N"：从用户解包的中性文件拷贝
                    if (parts.Length < 3) continue;
                    string neutralRel = parts[2].Replace('/', Path.DirectorySeparatorChar);
                    if (string.IsNullOrEmpty(neutralRel)) continue;
                    string source = Path.Combine(uiDir, neutralRel);
                    if (!File.Exists(source)) continue; // 解包不完整则跳过该项

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(source, target, true);
                }
            }
            catch (Exception ex)
            {
                // 注入失败不阻断启动：游戏仍能跑（回退繁体）。记录到日志便于排查。
                try
                {
                    File.WriteAllText(Path.Combine(_baseDir, "lang_inject.log"),
                        "[lang] 简体资源注入未完成: " + ex.GetType().Name + ": " + ex.Message);
                }
                catch { }
            }
        }

        /// <summary>
        /// 以 game/UI 下同名 zh-cht 为基底，套用补丁还原 zh-cn 并写出。
        /// <para>
        /// 基底长度/MD5 必须与造补丁时一致，否则偏移会错位。不符时退化为直接复制繁体到 zh-cn
        /// （该屏显示繁体，但至少有文件，避免整屏加载失败）；基底缺失则跳过。
        /// </para>
        /// </summary>
        private static void MaterializePatchedXus(
            string zhcnRel, string target, IDictionary<string, PatchEntry> patches, string uiDir)
        {
            if (patches == null || patches.Count == 0) return;

            PatchEntry entry;
            if (!patches.TryGetValue(zhcnRel, out entry)) return;

            string basePath = Path.Combine(uiDir, zhcnRel.Replace("zh-cn", "zh-cht"));
            if (!File.Exists(basePath)) return;

            byte[] data = File.ReadAllBytes(basePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            bool ok = data.Length == entry.BaseSize && Md5Equals(data, entry.BaseMd5);
            if (ok)
            {
                foreach (ByteRun run in entry.Runs)
                {
                    if (run.Offset < 0 || run.Offset + run.Data.Length > data.Length) { ok = false; break; }
                    Buffer.BlockCopy(run.Data, 0, data, run.Offset, run.Data.Length);
                }
            }

            if (ok)
                File.WriteAllBytes(target, data);
            else
                File.Copy(basePath, target, true); // 基底版本不符：退化为繁体，保证文件存在
        }

        private static bool Md5Equals(byte[] data, byte[] expected)
        {
            if (expected == null || expected.Length != 16) return false;
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(data);
                for (int i = 0; i < 16; i++)
                    if (hash[i] != expected[i]) return false;
                return true;
            }
        }

        /// <summary>patch_v1.bin 中的一段"偏移 + 新字节"。</summary>
        private sealed class ByteRun
        {
            public int Offset;
            public byte[] Data;
        }

        /// <summary>patch_v1.bin 中单个目标文件的补丁条目。</summary>
        private sealed class PatchEntry
        {
            public int BaseSize;
            public byte[] BaseMd5;
            public ByteRun[] Runs;
        }

        /// <summary>
        /// 解析 DDLP v1 补丁：magic{4} ver{u16} count{u16}；
        /// 逐文件 pathLen{u16} path{utf8} baseSize{u32} baseMd5{16} runCount{u16}；
        /// 逐段 offset{u32} len{u16} data。
        /// </summary>
        private static Dictionary<string, PatchEntry> LoadPatch(string path)
        {
            var map = new Dictionary<string, PatchEntry>(StringComparer.OrdinalIgnoreCase);
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 8 || b[0] != (byte)'D' || b[1] != (byte)'D' || b[2] != (byte)'L' || b[3] != (byte)'P')
                return map;

            int pos = 4;
            ushort ver = BitConverter.ToUInt16(b, pos); pos += 2;
            ushort count = BitConverter.ToUInt16(b, pos); pos += 2;
            if (ver != 1) return map;

            for (int i = 0; i < count; i++)
            {
                ushort plen = BitConverter.ToUInt16(b, pos); pos += 2;
                string rel = Encoding.UTF8.GetString(b, pos, plen).Replace('/', Path.DirectorySeparatorChar);
                pos += plen;

                var entry = new PatchEntry();
                entry.BaseSize = (int)BitConverter.ToUInt32(b, pos); pos += 4;
                entry.BaseMd5 = new byte[16];
                Buffer.BlockCopy(b, pos, entry.BaseMd5, 0, 16); pos += 16;

                ushort runCount = BitConverter.ToUInt16(b, pos); pos += 2;
                entry.Runs = new ByteRun[runCount];
                for (int r = 0; r < runCount; r++)
                {
                    var run = new ByteRun();
                    run.Offset = (int)BitConverter.ToUInt32(b, pos); pos += 4;
                    ushort dlen = BitConverter.ToUInt16(b, pos); pos += 2;
                    run.Data = new byte[dlen];
                    Buffer.BlockCopy(b, pos, run.Data, 0, dlen); pos += dlen;
                    entry.Runs[r] = run;
                }
                map[rel] = entry;
            }
            return map;
        }

        // ================================================================ 选项类型

        /// <summary>窗口尺寸预设项，显示成 "1280×720" 这样的客户区像素尺寸。</summary>
        private sealed class WindowSizeOption
        {
            public readonly int Width;
            public readonly int Height;

            public WindowSizeOption(int width, int height)
            {
                Width = width;
                Height = height;
            }

            public override string ToString()
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}×{1}", Width, Height);
            }
        }

        private sealed class AaOption
        {
            public readonly string Value;
            private readonly string _label;

            public AaOption(string value, string label)
            {
                Value = value;
                _label = label;
            }

            public override string ToString()
            {
                return _label;
            }
        }
    }
}
