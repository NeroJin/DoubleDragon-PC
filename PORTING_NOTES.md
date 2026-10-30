# Double Dragon（XBLA）ReXGlue 静态重编译移植记录

> 记录日期：2026-10-30 ｜ SDK：ReXGlue 0.10.0.24-dev.gbd833a2 ｜ 工具链：VS 2026 自带 Clang 22 / CMake / Ninja
>
> Title ID `0x5841085F` ｜ Media ID `0x3F5C2ABE` ｜ 版本 `0.0.1.3` ｜ xex 时间戳 2007-04-20

---

## 0. 当前状态

| 项 | 状态 |
|---|---|
| codegen | 通过，0 错误（10474 个函数，113 个文件） |
| 编译 | 通过（Release，`build/Release/DoubleDragon.exe`） |
| 运行 | **未通过**：启动到 Technos 法务画面阶段后，在 `sub_82186798` 处读 guest `0x1A4` 崩溃 |

崩溃特征（每次必现、地址固定）：

```
[error] [sys] Unhandled guest access violation: read of guest 0x000001A4
in-function: sub_82186798 + 0x298      # = lwz r11,420(r31)，420 = 0x1A4
```

## 1. 工程与构建

```
D:\Projects\DoubleDragon\
  game\                       素材（不入库）：default.xex + RomData\ + UI\ ...
  src\main.cpp                REX_DEFINE_APP
  src\DoubleDragon_app.h      App 钩子（OnPostLaunchModule 装崩溃转储）
  src\fixes.cpp               CRT 缺失函数补丁（roundevenf）
  src\crashdump.cpp           guest 崩溃转储（见 §3）
  src\hooks.cpp               manifest midasm_hook 的宿主实现（当前为空）
  DoubleDragon_manifest.toml  codegen 配置
```

构建（必须在 VS 环境外显式给生成器实例，且先清代理）：

```bat
cd /d D:\Projects\DoubleDragon
set http_proxy=& set https_proxy=& set HTTP_PROXY=& set HTTPS_PROXY=
set CMAKE="D:\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
%CMAKE% -S . -B build -G "Visual Studio 18 2026" -A x64 -T ClangCL ^
        -DCMAKE_GENERATOR_INSTANCE="D:/Microsoft Visual Studio/18/Community" ^
        -DCMAKE_PREFIX_PATH=D:/ReverseTools/ReXGlueSDK
%CMAKE% --build build --config Release
```

运行（`build/Release/` 下需要 exe + 两个 dll + `game/` junction）：

```bat
DoubleDragon.exe --game_data_root=game --gpu_plugin=xenos
```

`--game_data_root` 必给，否则 `[error] [core] --game_data_root was not provided.` 直接退出；
`--gpu_plugin=xenos` 必给，否则 `VdSetGraphicsInterruptCallback: no GPU emulation loaded`。

## 2. 构建期踩到的坑

1. **`roundevenf` 未定义符号**（lld-link）。
   simde 的 `_mm_round_ps` 回退路径引用了 C23 的 `roundevenf`，UCRT 没有。
   已在 `src/fixes.cpp` 用 `nearbyintf` 补上（与 TimePilot 同款处理）。
2. **Release 也要 PDB**：不然后面没法把 host 崩溃地址符号化。CMake 里已加
   `add_compile_options(/Zi)` + `add_link_options(/DEBUG)`。

## 3. 崩溃定位工作流（本项目新增，可复用）

`src/crashdump.cpp` 在 `OnPostLaunchModule` 里装 `SetUnhandledExceptionFilter`，
崩溃时打印：host 代码/RVA、guest `lr/r1/r3..r10`、按 `PPCFuncMappings` 反查的
`sub_XXXXXXXX + 偏移`、host 调用栈 RVA、以及栈上落在代码段里的值。

> 注意 `PPCFuncMappings` 按 host 地址就近匹配只是启发式，落到函数边界附近时可能不准。

真正的符号化用 SDK 自带 LLVM（地址要传 **VA = 0x140000000 + RVA**）：

```bat
llvm-symbolizer --obj=DoubleDragon.exe --functions --demangle 0x1406BBF48
:: → sub_82186798  @ ...\DoubleDragon_recomp.17.cpp:10415
llvm-objdump -d --start-address=0x1406BBF20 --stop-address=0x1406BBF60 DoubleDragon.exe
```

反汇编 0x1406BBF48 得到 `movl (%rdx,%rcx), %eax`，其中 `eax = ctx.r31 + 420` 且
`ctx.r31 == 0` —— 与 AV 地址 0x1A4 完全吻合，确认是 `lwz r11,420(r31)` 读到空基址。

## 4. manifest 迭代记录

codegen 报错/运行期 FATAL 补进去的地址：

| 地址 | 来源 | 用途 |
|---|---|---|
| `0x82379438` | codegen Validate：`b 0x82379438 from 0x8237980C` | 静态分析漏掉的直接跳转目标 |
| `0x823705F8` | 运行期 FATAL：unregistered indirect call | 间接调用目标 |
| `0x8236BBA8` | 同上 | 同上 |
| `0x82187348` | 同上 | 同上 |
| `0x82186798` | 同上 | 同上（本次崩溃点所在函数） |

补注册后流程会往前走一段，出现下一个 FATAL —— 属于预期内的逐个收敛。

## 5. 素材侧发现

- 游戏是「官方模拟器外壳 + RomData」结构：`RomData\ddragon.{cpu0,cpu1,mcu,snd0,snd1,gfx0-2,higfx0-2}`，
  即街机 ROM 由 xex 内的模拟器核解释执行。重编译的是外壳，游戏逻辑仍在 ROM 里。
- UI 资源有两套形态：根目录 `UI\screens\screen_*.xur`（英语编译版），
  各语言目录 `UI\screens\<lang>\screen_*.xus`。游戏按 `<lang>` 再回退根目录找 `.xus`，
  所以英语会报两条 `NtCreateFile FAILED`（不致命）。
- 共享图片（`legal_bg.png` 等）只在 `UI\screens\` 根目录，各语言 `.xus` 却按自身目录相对查找，
  导致 `de-de\legal_bg.png` 之类缺失。**临时处理**：把根目录的 legal 图复制进 `de-de\`
  做验证（仅本地素材，不入库）。正式做法应确认解包是否按哈希去重漏了副本。

## 6. 未解决：`sub_82186798` 的 this 指针

事实链：

1. `sub_82186798` 的代码是 `r31 = r3` 后立即 `lwz r11,420(r31)`，崩在 `r31 == 0`。
2. 该函数是某工作对象的**虚表 +4 槽**：构造函数把 `0x82186798` 写进 `[obj+4]`，
   同时 `[obj+0]` 写 `0x821877D0`（vtable），`[obj+28]` / `[obj+8]` 写另两个槽。
3. 调用点形态（`sub_82207C30`）：`r11 = [r31+444]` → `r3 = r31` → `r11 = [r11+4]` → `bctrl`，
   即「从子对象取槽、以宿主对象为 this」的 CFront 式嵌套虚表转发。
4. 在函数入口加 midasm_hook 打印后，**入口 r3 并不是 0**，而是 `0x7018EAC0`
   （落在栈上、且在入口 `r1=0x7018EAF0` 之下的未分配区），`r4=1`。
5. 短路该函数后，崩溃消失但转为另一处读 guest `0x00000000` —— 说明只是把小问题挪走，
   对象指针本身来路不明才是根因。

待验证的假设（按优先级）：

- **A. 子调用破坏了 r31 ——已用探针证实。**
  在 `0x821867E4`（`bl sub_821866B0` / `bl sub_82186528` 返回之后、下一次读 `420(r31)` 之前）
  挂 `midasm_hook`（`return=false`）打印，得到：

  ```
  [probe] after-call r3=0x00000001 r31=0x00000000
  ```

  `r3=1` 是 `sub_821866B0` 的正常返回值（它的结尾就是 `li r3,1`），
  但调用前 `r31` 是合法对象指针、调用后变成 0 —— **非易失寄存器 r31 没有被恢复**。
  已核对：`__savegprlr_26`（0x82390b70）把 `r26..r31` 存在 `r1-56..r1-16`、
  `__restgprlr_26`（0x82390bc0）从同样的偏移取回，配对本身正确；
  `sub_821866B0` 内部也没有写自身帧顶保存区的正向偏移访问。
  因此问题更可能在「谁把保存槽写成 0」或「函数入口/出口没有走完整的 save/restore 路径」，
  下一步应在 `sub_821866B0` 内部再插两级探针，定位保存槽被清零的时刻。
- **B. 调用方对象是栈上临时物且未构造**：入口 `r3` 为 `0x7018EAC0`，落在 `r1` 之下的
  未分配区，像是构造顺序问题（某初始化返回失败被忽略）。
- **C. 数据/RomData 与 xex 版本不匹配**：外壳从 RomData 读表构造对象，版本不一致会得到垃圾指针。


## 7. 与 TimePilot 的差异（可复用结论）

- 本项目**不需要** `license_mask`（试玩解锁）也能走到同一位置，加不加都一样。
- 本项目**没有** `Xui*` / `XamXui*` 导入，UI 是游戏自己解析 `.xus`，不要去查 XUI 运行时支持。
- `--user_language=<n>` 可切语言（3=de），用来判断某个故障是否与语言资源相关。
