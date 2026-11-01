# DoubleDragonUnpack —— STFS 解包库

给 loader / GUI 用的 C# 类库：读用户自己的 Xbox 360 STFS 包（`.LIVE` / `.PIRS` / `.CON`），
把里面的游戏资源解到数据目录。**只读解析**，不改包、不重签名、不带任何游戏数据。

- 目标框架：`net48`（Win10 1903+ / Win11 自带运行时；要兼容更老的系统把 csproj 里改成 `net472` 即可）
- 依赖：无，纯 BCL
- 规模：3 个源文件，约 700 行

> 不想建项目引用的话，把 `src/DoubleDragonUnpack/` 里的 3 个 `.cs` 直接拖进你的 WinForms 工程也能编（无第三方依赖、无 WinForms 引用）。

```
Loader/
  DoubleDragonUnpack.sln
  src/DoubleDragonUnpack/              核心库
    StfsTypes.cs                    Header / Entry / Options / Progress / Result / 异常
    StfsPackage.cs                  解析 + 解包（核心）
    DoubleDragonPack.cs                本游戏的高层封装：校验 Title ID、暂存替换安装、找包
  src/DoubleDragonUnpack.Cli/          命令行验证工具
```

---

## 一分钟上手

```csharp
using DoubleDragon.Unpack;

// 1) 在用户选的目录里自动找包（扫一层子目录，按 LIVE/PIRS/CON 魔数识别）
string package = DoubleDragonPack.FindPackage(selectedFolder);

// 2) 安装：默认开暂存 + 整体替换，失败不会留下半个资源目录
var result = DoubleDragonPack.Install(
    package,
    dataDirectory: @"D:\Games\Double Dragon\Double Dragon",
    options: null,
    progress: new Progress<ExtractProgress>(p =>
    {
        progressBar.Value = (int)p.Percent;          // 0–100
        label.Text = p.CurrentFile;                  // 当前文件相对路径
    }),
    cancellationToken: token);

Console.WriteLine(result);   // "55 个文件 / 26,970,333 B → ...（0.4 秒）"
```

WinForms 里不要卡 UI 线程：

```csharp
private async void btnInstall_Click(object sender, EventArgs e)
{
    string pkg = DoubleDragonPack.FindPackage(txtFolder.Text);
    if (pkg == null) { MessageBox.Show("这个目录里没有 STFS 包"); return; }

    var progress = new Progress<ExtractProgress>(p =>
    {
        progressBar.Value = Math.Min(100, (int)p.Percent);
        lblFile.Text = p.CurrentFile;
    });

    try
    {
        btnInstall.Enabled = false;
        var result = await Task.Run(() => DoubleDragonPack.Install(pkg, txtDataDir.Text, null, progress));
        MessageBox.Show(result.ToString());
    }
    catch (StfsException ex)         // 选错包、包损坏、格式不支持
    {
        MessageBox.Show(ex.Message);
    }
    finally { btnInstall.Enabled = true; }
}
```

`Progress<T>` 在 UI 线程构造，回调会自动切回 UI 线程，不用自己 Invoke。

---

## API

### `StfsPackage`（底层，任何 STFS 包都能用）

| 成员 | 说明 |
|---|---|
| `static Open(string path)` / `Open(Stream)` | 打开并解析头部与文件表 |
| `Header` | `Magic` `TitleId` `ContentType`(`ContentTypeName`) `ContentIdHex` `ContentSize` `HeaderSize` `DataStartOffset` … |
| `Entries` | 文件 + 目录条目（`RelativePath` 用 `/` 分隔、`Size`、`IsDirectory`、`IsConsecutive`） |
| `Find(path)` / `Contains(path)` | 按相对路径查条目 |
| `ExtractTo(dir, options, progress, ct)` | 解包，返回 `ExtractResult` |
| `ReadEntry(entry)` | 把单个文件读进内存（小文件预览用） |

### `DoubleDragonPack`（本游戏高层）

| 成员 | 说明 |
|---|---|
| `TitleId` = `0x58410815` | Double Dragon 的 Title ID |
| `Install(pkg, dir, options, progress, ct)` | 校验 Title ID + 暂存替换安装 |
| `IsDataInstalled(dir)` | `default.xex` 存在且 ≥ 4 MB |
| `FindPackage(dir)` | 在目录/一层子目录里找 STFS 包 |

### `ExtractOptions`

| 字段 | 默认 | 说明 |
|---|---|---|
| `Overwrite` | `true` | 目标已存在同名文件时覆盖 |
| `Staging` | `false`（`Install` 里为 `true`） | 先解到 `<目标>.tmp` 再整体替换 |
| `ExpectedTitleId` | `0x58410815` | 传 `null` 关闭校验 |
| `ValidateExecutable` | `true` | 解完检查 `default.xex` 魔数是不是 `XEX2` |

`ExtractProgress`：`Percent` `FilesDone` `FilesTotal` `BytesDone` `BytesTotal` `CurrentFile`。
`ExtractResult`：`FileCount` `ByteCount` `Directory` `Elapsed`。

---

## 实现要点（换包或排查时用得上）

**1. 数据区起点不是固定的 0xC000**

```
aligned  = (HeaderSize + 0xFFF) & ~0xFFF          // 0xAD0E → 0xB000
dataStart = (aligned == 0xB000) ? aligned + 0x1000 : aligned   // → 0xC000
```

常规包头部是 `0xB300`（对齐后正好 `0xC000`，块 0 就在那里）；本游戏是 `0xAD0E`，
对齐到 `0xB000` 后还空一块才是块 0。这个差异会连带影响块号映射，所以代码里用
`_hashBias` 记住它。

**2. 逻辑块号 ↔ 文件偏移**

文件表里的块号**不含哈希块**：每 170（`0xAA`）个数据块插一个哈希块。本包实测（1290 块全采样验证）：

```
physical = L + L/170 + (L >= 170 ? 1 : 0)
offset   = dataStart + physical * 4096
```

那个 `+1` 是首个边界处额外插入的一块（顶层哈希表）。常规包用 `L + L/170`。

**3. 必须按块读，不能连续读整段**

文件跨哈希边界时，包里中间夹着哈希块。一次 `Read` 连续读完会让 170 块之后的数据整体错位——
本游戏 `uiresource.xpr`（9.3 MB）就是典型受害者，前面 170 块完全正常，特别容易误判为"解包没问题"。

**4. 字段大小端是混排的**

| 字段 | 偏移 | 字节序 |
|---|---|---|
| 头部长度 / 内容类型 / 内容大小 / Title ID | `0x340` `0x344` `0x34C` `0x360` | 大端 |
| 文件表块数（VD+0x03） | `0x37C` | **小端**（`01 00` = 1） |
| 文件表块号（VD+0x05） | `0x37E` | 大端 24 位 |
| 条目块数 / 起始块 | 条目 `+0x29` `+0x2F` | **小端** 24 位 |
| 条目父目录索引 / 文件大小 | 条目 `+0x32` `+0x34` | 大端 |

块数和块号的字节序在规范里没写明，所以代码里都做了"先试一种、失败再换另一种"的兜底。

**5. 延迟写盘保护**

`Staging = true` 时：解到 `<目标>.tmp` → 成功后把旧目录改名 `.old` → `.tmp` 改名正式名 → 删 `.old`。
中途失败只删 `.tmp`，用户的旧资源不受影响。

---

## 已验证

对本游戏的 `A3E84D44…B34C58` 包：

| 项 | 结果 |
|---|---|
| 头部 | `LIVE`，Title ID `0x58410815`，内容类型 `0x000D0000`（XBLA），头部 `0xAD0E`，数据区 `0xC000` |
| 条目 | 57（55 文件 + 2 目录），全部"连续块"，无压缩块 |
| 解出大小 | 26,970,333 字节 |
| 与 LeFluffie 解包结果比对 | **55/55 文件 SHA1 完全一致，0 差异** |
| 与移植用资源目录比对 | 55/55 一致（对方多一个 `logs/` 运行时日志） |
| 逐块映射 | `default.xex` 1290 块逐块采样，公式 100% 命中 |

命令行工具可复现：

```bash
DoubleDragonUnpack list    "包路径"
DoubleDragonUnpack extract "包路径" "输出目录"
DoubleDragonUnpack verify  "包路径" "参考目录"     # 解到临时目录后逐文件比 SHA1
```

⚠️ 注意：本机安全策略禁止调用 C# 编译器，所以**这份代码没有在这里编译过**。
解析算法用等价脚本跑通并做了上表比对，但首次编译若有笔误请随手修一下。

## 已知限制

- **非连续块链**：抛 `StfsUnsupportedException`。本游戏包里所有文件都是连续块，用不到；真要支持得按哈希表条目里的 `next block` 逐链跳（free60 规范里有字段说明）。
- **压缩块**：未实现 LZX 解码。本包无压缩块。
- 只读：不提供打包/重签名（那也是 LeFluffie / XexTool 的活）。

## 法律

库本身不含任何游戏数据，只解析用户自己提供的包。发布时不要附带 `default.xex`、`media\` 等资源。

---

# DoubleDragonLoader —— 启动器窗体（net48 / WinForms）

给最终用户的壳：解包资源、调画质、点一下就进游戏。不含任何游戏数据。

```
Loader/src/DoubleDragonLoader/
  DoubleDragonLoader.csproj    WinExe / net48 / x64
  app.manifest              dpiAwareness = PerMonitorV2
  App.config                DpiAwareness = PerMonitorV2（.NET Framework 4.7+ 还要这一句才真生效）
  Program.cs                依赖检查 → 缺失弹框退出 → 打开主窗体
  MainForm.cs               界面 + 解包 + 写 toml + 拉起游戏
  KeyBindDialog.cs          键盘映射对话框（keybind_* 的录制/清除，即时写盘）
  KeyMap.cs                 WinForms 按键 <-> 运行时键名；155 个合法键名的校验表
  TomlConfig.cs             顶层 key = value 读写（保注释、解注释、缺键追加）
  L10n.cs                   简体中文 / 英文词条，跟随系统 UI 语言
```

## 发布目录

```
Double Dragon\
  DoubleDragonLoader.exe           启动器（用户在资源管理器里双击这个）
  DoubleDragonUnpack.dll           解包库（随项目引用自动复制）
  DoubleDragonLoader.exe.config    DPI 声明 + 运行时版本
  DoubleDragon.exe                 重编译出来的游戏
  DoubleDragon.toml                缺失时启动器自动生成模板
  rexruntime.dll
  rexgpu-xenos.dll
  <这里放用户自己的 LIVE/PIRS/CON 包>
  game\                         解包产物（启动器创建）
    default.xex  media\  classic\ ...
```

## 行为约定

| 动作 | 细节 |
|---|---|
| 启动检查 | `DoubleDragon.exe` / `rexruntime.dll` / `rexgpu-xenos.dll` 任一缺失 → 弹框列名字后退出（不写任何文件） |
| toml 缺失 | 按模板生成：`gpu_plugin` / `license_mask` / `resolution` / `resolution_scale` / `present_effect` / `mnk_mode` / `mnk_mouse` / `vsync` / VRR |
| 解包 | 点按钮弹文件对话框选包，loader 同目录里能找到包时会**预选好**（直接回车即可）；已解包会二次确认；`Staging` 失败不破坏旧目录 |
| 改设置 | 每次选择变更立刻写回 toml；只改自己的键（窗口尺寸/抗锯齿/全屏/vsync/VRR/mnk_*），其它键与注释原样保留 |
| 键盘映射 | 对话框内逐项改 `keybind_*`，改动即时写盘；主窗体的保存不会碰这些键 |
| 启动游戏 | 写 toml → `Process.Start`（工作目录 = loader 目录）→ 启动器关闭 |
| 解包目标 | `<loader 目录>\game`，正好对上游戏的三级路径查找 |

## 关于画质选项

- **内部渲染分辨率**：固定原生 720p（`resolution_scale = 1`）。改大内部渲染率会让游戏出现半像素偏移，所以启动器每次保存都强制写 1，界面不再提供倍率选项。
- **窗口尺寸**（仅窗口模式可用，全屏时置灰）：下拉 4 个常用尺寸 `1280×720` / `1920×1080` / `2560×1440` / `3840×2160`，写 `window_width/height`。窗口模式下启动器会注释掉 `resolution` 预设（其优先级高于 window_*，见 ReXGlue `video_mode_util::ResolveConfiguredSize`），让窗口尺寸生效。其中 1280×720 / 2560×1440 / 3840×2160 是 720p 的整数倍（整数缩放、无半像素偏移）；1920×1080 是 1.5 倍，可能引入非整数缩放，按需选用。
- **抗锯齿**：`present_effect` = `none` / `fxaa` / `fxaa_extreme`，最终呈现阶段的后处理。本作是像素画，FXAA 会把硬边糊掉，默认关闭（界面已无"推荐"提示），可按需自行开启。
- **全屏**：`fullscreen`（无边框 + 桌面分辨率，`fullscreen_exclusive` 保持默认关闭）。勾选后窗口尺寸下拉置灰（窗口尺寸无意义）。
- **VRR / vsync**：**界面不提供开关，启动器每次强制写** `d3d12_allow_variable_refresh_rate_and_tearing = false` + `vsync = true`。原因：允许撕裂会让呈现用 0 同步间隔，游戏速度直接失控（跑得飞快）。

  > 实测提醒：当前这版运行时（`rexruntime.dll` + `rexgpu-xenos.dll`）**没有注册 `vsync` 这个 cvar**——两个模块的字符串表里只有相邻的 `resolution_scale` / `present_effect` / `d3d12_allow_variable_refresh_rate_and_tearing`，`vsync` 只在 `graphics/flags.h` 里被声明、没被编译进去。D3D12 呈现路径只有"允许撕裂"打开时才用 0 同步间隔，所以**真正决定速度的是撕裂那个键，它已经被强制关掉了**。`vsync` 键照样写进 toml（运行时对不认识的键静默忽略），将来运行时补上这个 cvar 就自动生效。

## 关于键盘映射

按钮行最左边的「键盘映射…」打开对话框，管理 `keybind_*` 系列 cvar。要点：

- **原理**：`mnk_mode = true` 时运行时把键盘模拟成一个虚拟 Xbox 手柄，固定喂给 1P，和实体手柄并行（输入合并：按键取或、摇杆取幅度大的一方）。2P 必须是第二个实体手柄，键盘做不了 2P。
- **全部 25 个动作**：面键 A/B/X/Y、肩键 LB/RB、扳机 LT/RT、系统键 START/BACK/GUIDE、十字键四向、左右摇杆各四向 + 按下。Double Dragon 只消费其中一部分，其余是为下一个项目准备的；没用的绑了也无害（游戏不读）。
- **键名必须合法，否则静默失效**：运行时把键名字符串查表转成 VirtualKey，查不到就当 kNone——不报错、这个键永远没反应。`KeyMap.cs` 里的 155 项校验表是从 `rexruntime.dll` 的解析表里逐项核对出来的（脚本比对 155/155 全对）。容易写错的几个：回车是 `Enter` 不是 `Return`，反引号是 `GraveAccent`，引号是 `Apostrophe`，方向键是 `UpArrow` / `DownArrow` / `LeftArrow` / `RightArrow`。
- **出厂默认值全部可感知**：没绑的行直接显示运行时的出厂键位（`KeyMap.cs` 的 `RuntimeDefaults`，来自 DLL 里 keybind 注册表的"名称 / 显示名 / 默认值"三元组，按显示名逐个对照核对）。完整默认表：

  | 动作 | 默认键 | 动作 | 默认键 |
  |---|---|---|---|
  | A | Semicolon,Space | 左摇杆 | W/S/A/D，按下 F |
  | B | Quote,Backspace ⚠ | 右摇杆 | Up/Down/Left/Right ⚠，按下 K |
  | X / Y | L / P | 十字键 | Shift+Up/Down/Left/Right ⚠ |
  | LB / RB | 1 / 3 | BACK | Z,Tab |
  | LT / RT | Q,I / E,O | START | X,Return ⚠ |
  | GUIDE | 无 | | |

  ⚠ = 默认值里的键名运行时解析不出来（`Quote` / `Up` / `Shift+Up` / `Return` 都不在 155 项表里），**这些动作出厂就等于没绑**：B 只剩 Backspace、START 只剩 X 有用，右摇杆四向和十字键四向完全没有键。对话框里这些行用橙色显示，状态栏会给出计数；要用手动绑。这也解释了"默认键位好像不起作用"的另一半原因——除了 `mnk_mode`，出厂默认本身就有一半是死的。
- **与 overlay 热键的关系（2026-10-30 实测）**：F3（调试面板）/ F4（设置面板）/ F7（成就）/ GraveAccent（控制台）是运行时 `bind_*` 的默认键，与 `keybind_*` 是两套独立消费者，**按一下两边都响应**（实测 F4 同时打开设置面板并触发手柄按键）。所以捕获到这些键时弹确认框，用户确认才写；冲突检测里把它们预置成"占用者"，状态栏会常驻橙色提醒。不硬拦——真想把 F4 当 A 用的人确认即可。
- **交互**：每行「设置…」弹一个"按任意键"小窗录制（Esc 取消，纯修饰键会被要求重按）；「清除」把该行注释掉 = 交回运行时默认。改动立即写盘。一个动作可绑多个键（toml 里逗号分隔，如 `Semicolon,Space`），对话框里重新设置会替换整组。
- **安全网**：值里有表外键名 → 该行标红并提示"不会生效"；两个动作（或动作与 overlay 热键）绑了同一个键 → 橙色提醒"按下会同时触发"。
- **启用/停用**：主界面「输入」组有「启用键盘控制」开关（就是 `mnk_mode`），默认开——和以前强制打开的行为一致。只用实体手柄时建议关掉：出厂 A 键默认绑在分号/空格上，误碰键盘会触发游戏动作。关掉后「键盘映射…」仍可打开（键位改动照常存盘），但对话框顶部会用橙色提示"当前不会生效"。
- **主窗体每次保存都会写** `mnk_mode = 勾选状态`、`mnk_mouse = false`（鼠标驱动右摇杆，本作用不到右摇杆）。
- **已知运行时 bug**：`bind_console` 的默认值 `Backtick` 不在键名解析表里（正确写法是 `GraveAccent`），所以控制台热键当前是失效的。想用控制台就在 toml 里写 `bind_console = "GraveAccent"`（toml 里写 `bind_*` 是否生效未实测——配置加载发生在 bind 注册之前，取决于 cvar 注册时是否回查配置）。

## 构建

```bash
dotnet build src/DoubleDragonLoader/DoubleDragonLoader.csproj -c Release
# 或者用 VS 打开 DoubleDragonUnpack.sln 直接编 DoubleDragonLoader
```

单文件分发不需要额外运行时（Win10 1903+ / Win11 自带 .NET Framework 4.8）。

> **源码编码：全部 `.cs` 必须存成 UTF-8 with BOM + CRLF。** 少 BOM 时中文 Windows 上的 csc / VS 会按 CP936 解码源文件，中文注释变乱码；更糟的是多字节序列会吃掉紧随其后的 ASCII 字节。
> **写字符串字面量时别在中文里用半角双引号。** `@"..."` 里出现一个 `"` 就会提前结束字面量，从那一行烂到文件尾，VS 会显示"整段每一行都报错"——`DefaultTemplate` 就踩过这个坑，注释里的引号统一用「」或全角。模板现在是一行一个字符串的数组，即使真漏了转义也只坏一行。
