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

## 8. r31 崩溃根因破案（2026-10-30 下午，build19~29）

之前第 6 节的三个假设全部被推翻，真实根因是 **X360 SEH 结构化异常未被 recomp 模拟**。

### 8.1 排障过程中纠正的两个测量错误

- `__savegprlr_26` 的保存槽是 64 位写（std r31,-16(r1)），早期用 32 位读看到"槽=0"
  是假象（r31 高 32 位本来就是 0）。
- **探针字段读错位 bug**：`ReadGuestU64`+`bswap64` 之后低 32 位是 addr+4 处的字段。
  v3 的 DumpObject 全部错位 4 字节，一度得出"对象半初始化/+444=0/+24=0"的错误结论。
  v4 起全部改用 ReadGuestU32 逐字段读。**教训：U64 读的低位是下一个字段。**

### 8.2 对象构造链（实测正确，无 bug）

```
sub_821497F8 (帧 r1=0x7018EA60)
  └─ sub_821843A8(obj=0x7018EAC0, type=62, size=472)   工厂
      ├─ 清 +4、校验 type/size、memset 472 字节、保留 +0/+12、置 +16=1
      ├─ sub_82208E68(obj)  类构造：8220E7A8(obj,84) 申请接口块 → 填静态表 → stw r31,4(r29)
      ├─ 零 +8/+24/+168..+308
      ├─ sub_82187078(obj)：经 +4 接口块虚调用 (this,0,172) → 组件 0x400D4160 → stw +444
      ├─ sub_82207DF8(obj)
      └─ state(+420 区域) 等初始化
```

最终对象：+0=0x7018ECA0(栈上函数表) +4=0x40080990 +12=0x4000D7F4 +16=1
+24=0x400D4230(解析子结构) +440=0x400D4210 +444=0x400D4160。**全部有效。**

### 8.3 崩溃链条（每一环都实测验证）

1. `sub_821846B8`(状态机) → bctrl `*(this+440)+0` → `sub_82207C30`
2. `sub_82207C30` → bctrl `*(this+444)+4` → `sub_82186798`（正常）
3. `86798` 循环：`420(r31)==0` 且 `444(r31)` 链为空 → `bl sub_821866B0`
4. `866B0(obj)`：`r31 = *(obj+24) = 0x400D4230`（解析子结构），
   chunk={base,size} 经 refill 回调 `0x82147958`（+28/+32 → +0/+4，恒返回 1）
   装入 **0x40D00060 / ~223KB**
5. 扫描循环（loc_821866FC）实际是 **JPEG SOI(FF D8) 校验**：首字节非 0xFF 即走
   错误路径 loc_8218674C
6. 缓冲内容实测 = **PNG 文件**（`0D 0A 1A 0A ... IHDR 640x360`，基址偏 3 字节
   = PNG+3，即 `89 50 4E` 之后）→ 校验失败 → throw
7. throw 助手 `sub_821478F0`：保存 r31/LR → stwu -96 → bctrl → `bl sub_82395270`
   （内部 `__imp__RtlUnwind`）→ **生成代码在此截断（真机上永不正常返回，无 epilogue
   是对的）**。recomp 未模拟 SEH 展开 → "返回"时 r1/r29/r31 全是垃圾
8. 866B0 带假 r1=0x7018EA60 执行 epilogue：`addi r1,r1,144` → r1=0x7018EAF0 →
   `__restgprlr_26` 把对象本体 0x7018EAC0+0x20..+0x38 当保存区恢复 → r31=0
9. 返回 86798 → `lwz r11,420(r31)` 访问 guest 0x1A4 → GUEST CRASH

**根因：游戏用 throw/catch（X360 SEH/RtlUnwind）做正常控制流（格式嗅探/数据未就绪），
recomp 未实现 guest SEH 展开。** 真机上这类 throw 每次都会发生并被 catch。

### 8.4 当前缓解方案（src/hooks.cpp，临时，SEH 模拟实现后移除）

- `[fix-entry]` @866B0 入口：当前/待装 chunk 头两字节非 FF D8（确认非 JPEG）→
  清空 chunk 字段
- `[fix-scan]` @866FC 扫描循环头：chunk base==0 → 计数器 r10 置 1 强制走耗尽路径
- `[fix-refill]` @82147970（回调 blr 前）：重载后 base==0 → 强制 r3=0
- 效果：866B0 走自带 `li r3,0` 优雅返回 → 86798 走 loc_82186C1C 返回 0 →
  **原崩溃消失**（[rest]/[E4] 正常触发，r31 正确恢复）

### 8.5 下一层问题（build29 暴露）

优雅返回 0 后流程继续，新崩溃在 `sub_82184A18 + 0x14F`（从 `sub_82184BD0` 的
`bl lr=0x82184D34` 进入），崩溃时 r1=0x7018E9F0（又高于本帧）、r3=0x7018F050、
r4=0x24000448/r5=0x8214984C（与旧崩溃相同的寄存器特征）。疑似同类问题
（另一处 throw/截断函数），待查。另注意 `sub_82395270` 内的 `__imp__RtlUnwind`
是 rexbuilt 导入 shim，其真实行为（空操作还是有模拟）未确认——若 rexglue 未来
加入 guest SEH 支持，本节缓解方案应整体移除。

---

## 9. 第二~四层：throw 吞掉、格式分发循环、refill 校验（build30–37）

### 9.1 崩溃 #2 真凶：state 201 的“再抛”

build30 探针抓到完整链（run30.log）：

```
[497F8 #1] r3=0x7018F050 r4=0x40D00060 r5=0x368C9   ← 内存里的 PNG（223KB）
[BD0 #1]  obj=0x7018EAC0  state=0xC9(201)            ← JPEG 工厂走完后状态 201
[C28]     state=201 → BD0 默认分支 → bctrl *(obj+0)[0]
[THROW]   lr=0x82184D2C → bctrl 目标就是 throw 助手 0x821478F0 本身
```

即：对象的 `vtable[0]` **直接就是 throw 助手**（“不支持的操作”占位）。真机上它会
RtlUnwind 展开到上层 catch；recomp 里函数从末尾掉出去 → r1/r31 全毁 → BD0 带着
垃圾 r31 继续 → `A18` 读 `+424`(=0) → 空指针崩溃。

### 9.2 修复：throw 助手无害化（关键）

```toml
[[entrypoint.midasm_hook]]
address = 0x821478F0          # throw 助手入口
name = "DoubleDragon_FixThrow"
return = true                 # 生成: Hook(r3); return;  ← 不动 r1/r31
```

`return = true` 在函数入口 = 整段函数被短路，寄存器原样返回给调用者。效果：所有
对 0x821478F0 的 throw 都变成 no-op（“异常被吞”），调用者（BD0/A18 循环等）拿到
完好寄存器继续。**这一条把“每次 throw 必崩”变成了可继续执行**。

### 9.3 A18 的 +424 补洞（注意：不能写对象自身字段！）

- `+424 == 0` 时 A18 的 `lwz r11,0(r11)` 会崩 → 需要给它一个合法接口块
- 试验 1（build32）：指向 `+440` 真实接口块 → 其 `[8]` 恒非零 → A18 的
  `loc_82184A70` 循环**死锁**（日志 1300 万行，PrintCap 计数器还溢出了）
- 试验 2（build33）：在对象尾部 +420/+428 自建接口 → 能跑，但**污染共享对象**
- 最终（build36）：`LookupHeapByType(false,0x1000)->Alloc(16,…)` 分配独立 guest
  临时块，写入 `{0x823AC730(纯 blr 空函数), 0, 0}`，`+424 := 该块`
- 另加 `[guard-a18]` @0x82184A70：同一对象循环 >2000 次就把 `iface[8]` 清零强制退出

### 9.4 refill 结果必须做可读性校验

run38：扫描器拿到 `base=0x40510000`（未映射）仍 `lbz` → 未处理 AV。修法：

```cpp
// xmemory: LookupHeap(addr) + QueryRangeAccess(low,high) != kNoAccess
bool GuestRangeReadable(mem, addr, size);
```

- `[fix-refill]` @0x82147970：重载后 `base==0 || size==0 || !可读` → `r3=0`（EOF）
- `[fix-86528]` @0x82186528 入口：入口块坏 → 清 size/pending（同 866B0 规则）
- 注意：`TranslateVirtual()` 不做映射校验，直接加偏移，读未映射页就是 AV；
  低地址（<0x1000）读还会绕过 `__try/__except` 直接崩进程（run31 实测）

### 9.5 关键认知：`sub_8214B318` 是“9 种格式逐个尝试”循环

```
数组 [0,5,4,1,3,8,7,2,6]（栈上 r1+112 起 9 个字）
loop: switch(fmt) → 各解码器(this, buf, size)；返回 <0 = 失败(清理后试下一个)，
      >=0 = 成功(跳出)。解码器入口：
  0→8214B298 1→821497F8(JPEG) 2→82149B78 3→8214A588 4→8214ADF8
  5→8214A2A0 6→82148C38 7→821480F8 8→82147CF0
```

**这就是游戏用异常做格式嗅探的地方。** run40 实测每张图的真实序列：

```
fmt 0 → E_FAIL, fmt 5 → E_FAIL, fmt 4 → E_FAIL, fmt 1(JPEG) → E_FAIL(我们的缓解),
fmt 3 → 0  ← 成功，跳出循环（8/7/2/6 从未尝试）
```

因此 PNG 最终是被 **格式 3（sub_8214A588）** 接下的，图像解码并没有真的失败。
XEX 里确实内嵌了 libpng（`dd_dec.xex` 中 0x82019db0 处 libpng 错误串
“Application transferred too many scanlines”，0x82040d48 处 chunk 名表
`IHDR/IDAT/IEND/PLTE`，另有内嵌 PNG 资源）。

### 9.6 当前剩余问题（build37 / run40）

跑满 10s、无 guest 崩溃、10 张图全部“成功”，最后：

```
[critical] [FATAL] Call to invalid or unregistered function at guest address 0x40D016D0
```

`0x40D016D0 = 0x40D00060 + 0x1670`，即**在 PNG 文件缓冲内部**——某个 bctrl 把数据
当函数指针调了。方向：
1. 可能是我们 `[fix-entry]/[fix-86528]` 的中和让某结构留下半初始化状态，
   后续阶段按“解码成功”继续走，读到伪造的指针；
2. 也可能格式 3 返回 0 并不是真正解码成功（`>=0` 判定太宽松），需要看
   `sub_8214A588` 的语义；
3. `en-en` 本地化目录在游戏数据中缺失（只有 de/es/fr/it/ja/ko/pt/zh-cht），
   所有 `game:/ui/**/en-en/*` 打开都 FAILED，随后走根目录回退。若要更贴近
   真机行为，可考虑给 `en-en` 做一份根目录内容的副本（纯数据层面，待验证）。

### 9.7 工具

- `tools/find_png_codec.py`：验证 XEX 解压后 `file offset → guest 地址` 映射
  （header size 0x2000、image base 0x82000000），并用 0x8232E038 的
  `mr r3,r5` 字节自校验；可搜 PNG 字符串/被引用位置
- `tools/find_png_refs.py`：扫 lis/addi 对，列出指向指定 .rdata 页的代码引用
- 解压 XEX：`xextool.exe -e u -o dd_dec.xex game/default.xex`

## 10. 0x40D016D0 根因深挖（build38–45）：虚表被污染 / 未重定位

### 10.1 run46（noop 陷阱）暴露的关键事实

把 `REX_CALL_INDIRECT_FUNC` 里对 `0x40D016D0` 的命中从“FATAL”改成“打印并 noop 继续”，
run46 抓到**同一坏值被从很多不同调用点调用**，且 `r3/r31` 恒为 `0x7018E800`：

```
[TRAP-UNREG] target=0x40D016D0 lr=0x821854E0 r3=0x7018E800 ...   (sub_821852A0 fallback: 虚表槽 0)
[TRAP-UNREG] target=0x40D016D0 lr=0x82185530 r3=0x7018E800 ...   (sub_821852A0 matched: 虚表槽 +4)
[TRAP-UNREG] target=0x40D016D0 lr=0x82207700 r3=0x7018E800 r30=0x40DCD10 ...
[TRAP-UNREG] target=0x40D016D0 lr=0x82207744 r3=0x7018E800 r30=0x40DCD10 ...
[TRAP-UNREG] target=0x40D016D0 lr=0x822077B4 r3=0x7018E800 r30=0x40D0008C / 0x40D000E0 ...
[TRAP-UNREG] target=0x40D016D0 lr=0x82209658 r3=0x7018E800 r30=0x40D01870 ...
[TRAP-UNREG] target=0x40D016D0 lr=0x822081C4 r3=0x7018E800 r30=0x40DC200 (r31=0)
[TRAP-UNREG] target=0x40D016D0 lr=0x82208130 r3=0x7018E800 r30=0x7018E800 (r31=0)
```

结论：

- **核心对象就是 `0x7018E800`**——几乎所有现场的 `r3/r31` 都是它（仅末尾两处 `r31=0`
  但 `r3` 仍是 `0x7018E800`，即 `r3` 是对象、`r31` 在那些调用点不是）。
- 它是一个**被很多调用点通过虚表调用的对象**，而这些调用点取的虚表槽位值**恒为 `0x40D016D0`**
  （fallback 取槽 0、matched 取槽 +4，外加 0x82207xxx 等取其它偏移，全都是 `0x40D016D0`）。
- `0x40D016D0` 落在 **`0x40D00000` 这个 heap/data 区**（之前已确认是 PNG 缓冲
  `0x40D00060 + 0x1670`，高熵随机字节，不是代码也不是虚表）。
- `sub_821852A0` 的派发表 one-past-end（`*(obj+220)` + `*(obj+36)*84`）也等于 `0x40D016D0`；
  其它调用点的 `r30` 是 `0x40DCD10 / 0x40D0008C / 0x40D000E0 / 0x40D01870 / 0x40DC200`
  ——**全是 `0x40D00000` 数据区地址**。即对象 `0x7018E800` 的多个指针字段都指向这个数据区。

### 10.2 根因假设（待 trap_dump.txt 确认）

- **假设 A（最可能）：虚表未重定位 / 被污染。** 对象 `0x7018E800` 的虚表指针
  `*(obj+0)` 所指向的虚表里，多个槽位被写成了 `0x40D016D0`——一个 guest 数据/堆地址。
  要么 recomp 漏重定位了某张虚表里的函数指针（原始 XEX 里的绝对地址没被改写），
  要么某个 hook（`[fix-entry]`/`[fix-a18]` 等）或运行时把数据指针误写进了虚表槽。
- **假设 B：** `0x40D016D0` 本身就是一张“占位/未实现”虚表的地址，槽位全是它，
  recomp 没为它生成代码 → 调用即 FATAL。（与 A 同类，都是“该地址没有对应 recomp 函数”。）

无论哪种，修复方向都是：**让 `0x40D016D0` 这个调用目标变成合法 code 地址**——
要么在 manifest `[entrypoint.functions]` 里为它生成 recomp 函数，要么校正重定位，
要么撤回污染它的写入。

### 10.3 诊断陷阱（已落入代码，build45 二进制已含）

`generated/default/DoubleDragon_pch.h` 的 `REX_CALL_INDIRECT_FUNC`：

- 条件从“`target == 0x40D016D0`”放宽为“**任何未解析且 `target < 0x82000000`（代码基址）**的
  间接调用”——这是“数据被当代码调用”的通用特征，能罩住同类 bug。
- `RexTrapUnregistered` 命中后 dump：
  - 现场 `r1/r3/r4/r5/r29/r30/r31`、`r25`、`r27`；
  - `obj=0x7018E800` 的 `vptr=*(+0)`、`count=*(+36)`、`dtbl=*(+220)`；
  - **虚表 `slots[0..15]`**（确认哪个槽位 = 0x40D016D0、虚表指针本身是否指向数据区）；
  - 目标地址 `0x40D016D0` 处 16 字节（确认是 PNG/数据还是代码）；
  - `obj` 前 32 字布局；`r25` 处 16 字节命令串。
  - 然后 `::_exit(2)` 干净退出。
- **写入文件 `D:/Projects/DoubleDragon/trap_dump.txt`（不是 stderr）**：
  原因——本 agent 沙箱里游戏是 WIN32 GUI 子系统、没有关联控制台，`fprintf(stderr)`
  输出会丢（run46 能抓到只是因为那次运行有控制台）。写文件不受控制台影响。

### 10.4 当前阻塞：本沙箱跑不到该路径

在 agent 无头沙箱里：游戏进程能起来（`tasklist` 可见 PID、Console 会话、~20MB 内存），
但跑到 240s 也不触发陷阱、无输出——它停在标题/菜单，或 **headless 的 xenos GPU 路径
把触发 `0x40D016D0` 的那条 UI 屏幕加载推迟/跳过了**（run46 日志里陷阱出现在很靠后的位置，
说明它在后期交互/attract 路径上）。所以这里无法复现。

**下一步（需用户在有显示的环境跑，那里 run40–46 能复现）：**
1. 用 build45 的 `build/Release/DoubleDragon.exe` 运行；
2. 游戏跑到触发点会自动写出 `trap_dump.txt`；
3. 把 `trap_dump.txt` 贴回来，据此确认虚表指针与坏槽位，决定：
   - 是漏重定位 → 在 manifest 给 `0x40D016D0`（及同虚表相邻槽）补 `FUN_*` 生成，或
   - 是 hook 污染 → 撤回对应写入。

（陷阱代码是临时调试用，确认根因后应从 `DoubleDragon_pch.h` 移除。）

---

## 11. `game_dir not mounted` 启动致命错误（build46 新增）

### 11.1 现象
用户在 GUI 下运行 build45 的 exe，启动即弹 **`Fatal Error: game_dir not mounted`**
（红色叉号对话框，只有 OK 按钮）。这是比 `0x40D016D0` 更靠前的**启动期**阻塞——
资源目录根本没挂上，游戏读不到 XEX/UI/PNG。

### 11.2 根因
- recomp 框架（`rexruntime.lib`，CVar `FLAGS_game_data_root_storage_`）用
  `game_data_root` 把宿主 `game/` 目录挂载成 guest 的内容目录（按 XEX title_id 定位）。
- `src/DoubleDragon_app.h` 此前**没有重写 `OnConfigurePaths`**，完全依赖框架默认。
  框架默认按**当前工作目录（CWD）**解析 `game_root = "game"`。
- 双击 exe 启动 → CWD = exe 所在目录（`build/Release/`），那里没有 `game/` →
  挂载失败 → 游戏（`OnPostSetup` 之后、guest 启动前）检测到内容目录未挂载 → 弹
  `game_dir not mounted`。
- 之前 run40–46 能复现 `0x40D016D0`，是因为那几次都是**从项目根目录**用命令行启动
  （CWD=项目根，`game/` 找得到），挂载成功才走到后面的解码/崩溃。

### 11.3 修复（已编译进 build46）
`src/DoubleDragon_app.h` 新增 `OnConfigurePaths` 覆盖：从 **exe 模块路径**出发向上回溯
找到含 `game/` 子目录的项目根，把 `game_data_root` 钉死为 `<root>/game`，与启动目录无关。
这样无论从项目根、从 `build/Release/` 双击、还是从任意子目录启动都能挂载。

替代方案（不改代码）：命令行传 `--game_data_root=<绝对路径>/game`，或始终从项目根启动。

### 11.4 与 `0x40D016D0` 的关系
**两者是独立的两道关卡**。`game_dir not mounted` 是启动期资源挂载失败；`0x40D016D0` 是
挂载成功、游戏正常加载资源后，在图像解码路径里发生的**堆/数据损坏**（见第 10 节：解码器
块描述符 `base/size` 被涂成垃圾 `0xFFF5F603`，虚表槽位被涂成 PNG 缓冲内地址 `0x40D016D0`，
根因是 recomp 未模拟 SEH unwinding、解码异常被 `fix-throw` 吞掉后对象状态不一致）。
- 因此：挂好资源目录后，游戏大概率**仍会**走到 `0x40D016D0` 那一关。
- 届时 build46 自带的陷阱会在命中 `<0x82000000` 的非法间接调用时写出
  `D:/Projects/DoubleDragon/trap_dump.txt` 并 `_exit(2)`——把该文件贴回即可继续定位。
