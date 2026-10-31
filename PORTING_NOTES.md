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

## 12. 黑屏真正根因：`gpu_plugin` 为空 = 完全没有 GPU 模拟（已修复）

第 11 节修好资源挂载后，程序能起来但**全黑**。本节是它的根因，以及修好之后暴露出的下一关。

### 12.1 用分段探针定位（这是本轮最有效的手段）

在每帧上屏函数 `sub_8212C520` 内部按里程碑插 `midasm_hook`（写 `heartbeat.log`，
因为 GUI 子系统在无头/双击场景下没有控制台，stderr 会丢）：

```
FRAME_BEGIN#0 → PRESENT#0 → SWAP_IN → SWAP_OUT → PRESENT_EXIT → FRAME_END   ✅ 完整
FRAME_BEGIN#1 → PRESENT#1 → SWAP_IN → SWAP_OUT → ✗ 之后什么都没有
```

第 2 帧的 `VdSwap` **已经返回**（`SWAP_OUT` 有），但 `sub_8212C520` 没走到出口
（`PRESENT_EXIT` 没有）——所以不是上屏死锁，而是上屏**之后**的代码卡住。

继续二分（`Q1..Q4`、`R1..R7`），最终落在：

```
Q1 (sub_82134F40 返回) ✅
Q2 (sub_8213ABF0 返回) ✅
R5 (sub_821340B8 返回) ✅
R7 (sub_82134910 返回) ✗ ← 卡在 sub_82134910 内部
```

### 12.2 根因：等 GPU 的自旋循环无人推进

`sub_82134910` 本质是一个**等 GPU 消费命令缓冲**的忙等循环：

```asm
loc_82134998:
  bl  sub_82122D98            ; 查 GPU 进度
  cmpwi r3,0
  beq loc_821349C4            ; 返回 0 → 停止等待
loc_821349A8:
  ; 比较 写指针 *(r31+10780) 与 已消费 *(*(r31+10768))
  blt → 回到 loc_82134998     ; 还没消费完 → 继续转
```

而 `sub_82122D98` 里的进度来源是：

```asm
  lwz r10,256(r13)      ; KPCR/TLS 区
  lwz r30,88(r10)       ; ← GPU 侧更新的进度计数器
  ...
  subf r11,r11,r30
  cmplwi cr6,r11,5000   ; 进度差 < 5000 → 返回 1，继续等
```

关键证据：**进程根本没加载 GPU 插件 DLL**。

```
PID=25936 Threads=24 CPU=8.4  WS=212MB
modules: rexruntime.dll, dxgi.dll, d3d11.dll      ← 没有 rexgpu-xenos.dll
```

没有 GPU 模拟 → 命令缓冲无人消费 → 进度计数器永不前进 → 第 2 帧起永久自旋
（CPU 140%，纯空转），画面当然全黑。

`rex/runtime.h` 里写得很直白：

```cpp
struct RuntimeConfig {
  // GPU emulation plugin loaded by ReXApp when `graphics` is empty
  // (e.g. "xenos"); empty means no GPU emulation.
  std::string gpu_plugin;
```

CMake 的 `rexglue_setup_target(DoubleDragon GPU_PLUGINS xenos)` **只负责把
`rexgpu-xenos.dll` 拷到 exe 旁边**，运行时不会自动去加载，必须在 `OnPreSetup`
里显式填插件名。而 `DoubleDragon_app.h` 此前从未重写 `OnPreSetup`。

### 12.3 修复

`src/DoubleDragon_app.h`：

```cpp
void OnPreSetup(rex::RuntimeConfig& config) override {
  config.gpu_plugin = "xenos";
}
```

实测效果（无头沙箱，同一份资源）：

| | 修复前 | 修复后 |
|---|---|---|
| 上屏帧数 | 2 帧后死等 | **1390 帧**持续渲染 |
| CPU | 140% 空转 | 正常渲染负载 |
| 行为 | 黑屏永久挂起 | 跑起来，随后在 JPEG 解码处崩 |

### 12.4 修完后暴露的下一关：JPEG 扫描器 host AV

跑约 1390 帧后崩溃（host 直接 0xC0000005，不是 guest FATAL）：

```
host: code=0xC0000005
guest: lr=0x82186530 r3=0x7018E800 r4=0x0000FEF0 r9=0x0000FEFE r10=0xED
in-function: sub_82186528 + 0x283
```

`sub_82186528` 是 JPEG 标记扫描器，输入流是 `*(obj+24)` 的子结构
`{base@+0, size@+4, ..., refill@+12}`。崩溃时 `base/size` 是 `0x0000FEF0`
这类被截断的垃圾值（像高 16 位丢失），扫描器随后 `lbz 0(r30)` 命中未映射页。

修复（已实现，待验证）：在 `sub_82186528` 入口做输入流体检——`{base,size}`
用 `GuestRangeReadable` 判定不可读时把 `size` 清零，扫描器第一轮就会走
`loc_82186544` 的 `size==0` 分支先调 refill 回调，不再从坏指针取字节。
（`heartbeat.log` 里会留下 `FIXSTREAM base=... size=...` 记录。）

---

## 13. 黑屏冻结 → 跑到标题（2026-10-30 晚，build69–77）

用户现场：**"只有第一幅画面，然后黑屏"**。`heartbeat.log` 给出干净的分界：

```
PRESENT #0 … #136（约 7.5s，法务/logo 屏）   ← 之后再也没有 FRAME_BEGIN
DECODE a=0x7018E800 × 5 万次（每秒数千次）    ← 帧循环再也不回来
```

即：**上屏流水线本身是好的，是 guest 主线程被 JPEG 解析器吃住了**。

### 13.1 冻结根因：负数 size 的"数据流"被我们截断成可读，于是永不结束

`sub_82186528`（标记扫描）的流结构 `{cur@+0, remain@+4, ..., refill@+12}`
里，`refill` 回调 `sub_82147958` 只是把"预装块" `{+28,+32}` 搬进 `{+0,+4}`
并**无条件 return 1**。真机上块耗尽时上层就不再装块，扫描器拿到 0 自然退出。

实测 `size = 0xFFF5F603`（= **负数**，即 `end(0x400DC907) < cur(0x4017D304)`，
说明这一路数据早就读到底了）。我们此前把负数 size "截断到可读范围"继续扫，
解析器就永远找不到结束标记。

修复：`DecodeHeartbeat` 判定 `size >= 0x80000000`（或 base 不可读）时置
`g_abort_decode`，生成的 `sub_82186528` 入口读该标志后 **直接 return 0**
（= 没有更多数据），整条解析链干净失败。另加"同一 base 连续 20 万次扫描"的
兜底死循环检测。

### 13.2 第二个雷：非 JPEG 数据进 JPEG 解码器 → `error_exit` → SEH 未模拟

冻结解除后立刻暴露：

```
[FATAL] Call to invalid or unregistered function at guest address 0x40D016D0
```

`trap_dump.txt` 抓到的现场（第一次命中，不是被踩坏之后）：

```
obj=0x7018E800  cinfo.err=0x7018E9E0   error_exit=0x821478F0   ← 正常
```

即 `0x7018E800` 是 **jpeg_decompress_struct**，`err` 是内嵌的
`jpeg_error_mgr`：`+0 error_exit / +4 emit_message / +8 output_message /
+12 format_message / +16 reset_error_mgr / +20 msg_code / +24 msg_parm`。
（`sub_821852A0` 就是 libjpeg 的 huffman 符号查表：查不到 → 写 msg_code
→ `bctrl *(err+0)`。）

而 `error_exit = sub_821478F0` 的末尾是 `b 0x82395270`（C++ throw /
unwind 包装），recomp 没有 SEH 展开 → 只能被 `DoubleDragon_FixThrow`
短路成 no-op → 解码器带着毁掉的寄存器继续跑 → 把 `err+0` 写成数据指针
`0x40D016D0` → 下一次 error_exit 就 FATAL。

真正的触发条件很朴素：**格式嗅探器 `sub_8214B318` 按 `[0,5,4,1,3,…]`
把同一块 PNG 数据也喂给了 fmt1 = JPEG 解码器**。真机上 JPEG 解码器
FFD8 校验失败就抛异常、外层 catch 掉、接着试下一种格式。

修复：在 `sub_821497F8`（JPEG 解码器入口）插入前置判断——头两字节不是
`FF D8` 就**直接返回 `0x80004005`(E_FAIL)**，不进解码器。实测日志里
`SKIP_JPEG buf=0x40D00060 hdr=0x89504E47`（PNG magic），随后 fmt3 正常解码。

### 13.3 第三个雷：字节码 VM 的跳表目标从来没被生成

下一个 FATAL：

```
[FATAL] Call to invalid or unregistered function at guest address 0x820D8018
```

陷阱抓到 `lr=0x820D7FF8`、`r3=0x820D799C`，而 `r3` 处的内存就是一张表：

```
0x820D799C: 820D7FF4 820D8018 820D8054 820D80B0 …（406 项，全部落在
            0x820D7928..0x820E0BF8 这个"函数表空洞"里）
```

`0x820D7FF4` 起的 dispatcher 是典型的字节码 VM：

```asm
lwz   r31,0(r30)     ; 取操作码
rlwinm r0,r31,2,21,29
lwzx  r9,r25,r0      ; handler = table[opcode]
mtctr r9 / bctr      ; 计算跳转
```

`bctr` 的目标静态不可见 → codegen 一个都没生成 → `bctr` 即 FATAL。

修复（**manifest 驱动，可 reproducibly 重跑**）：
`tools/` 里用脚本把跳表的 405 个不同目标（403 个 handler + dispatcher
自身 + 两个内部分支点）写进 `[entrypoint.functions]`，再 `rexglue codegen`。
codegen 会为每个目标生成独立函数，`bctr` 变成可解析的间接调用；
handler 之间的 `b`/`bne` 也变成 `{ FUNC(ctx, base); return; }` 尾调用。

顺带把 codegen 报的三个 `Unresolved branch`（0x820D7974 / 0x820D8000）
也补成函数入口，直到 `grep "Unresolved branch" generated/` 为空。

### 13.4 `tools/apply_patches.py`：codegen 会抹掉手工补丁

`generated/**` 是 codegen 产物（且不入库，见 .gitignore）。13.1/13.2 的
两处改动和调试陷阱都落在生成文件里，**每次 codegen 之后必须重打**：

```
rexglue codegen DoubleDragon_manifest.toml --ignore-stamp
python tools/apply_patches.py
cmake --build build --config Release
```

脚本幂等，负责：
1. `DoubleDragon_pch.h`：注入 `DoubleDragon_TrapDataCall` 并改造
   `REX_CALL_INDIRECT_FUNC`（未注册目标先 dump 现场再 Resolve，只记 8 次不退出）；
2. `sub_82186528`：坏流/死循环 → `return 0`；
3. `sub_821497F8`：非 JPEG → `E_FAIL`。

### 13.5 当前状态（build77，无头沙箱实测）

| | 修复前（build68） | 现在（build77） |
|---|---|---|
| 上屏 | 136 帧后永久黑屏 | **持续上屏（50s / 18000+ 帧无中断）** |
| 图像解码 | JPEG 路径死循环 | 13 张图 `fmt-OK`（PNG 走 fmt3） |
| 运行 | 7.5s 后冻结 | 50s 稳定、无 FATAL、无 trap |
| GPU | — | 8 条 pipeline 正常创建 |

仍未做的：
- 用户端确认画面内容（法务屏 → 标题屏是否正常出图）；
- `en-en` 缺 `LargeDDLogo.png` / `razorworks_logo.png` 已用根目录同名文件补齐
  （20 次重试消失），是否该由数据侧统一补全所有 `en-en` 资源待定；
- `sub_82186528` 的 `return 0` 属于"坏流直接放弃"，若某张图确实需要从
  坏流里恢复会缺图，需要时再看 `CLAMP_CHUNK` 日志。

---

## 14. 恢复垂直同步（2026-10-30 深夜，build78）

用户现场：**"可以运行了，不过要开垂直同步，要不然速度会变快。"**

原因很直接：黑屏排查期在 `OnPreLaunchModule` 里写的 `vsync=false` 一直没撤。
recomp 的 guest 侧 vblank 节拍由 presenter 的 vsync worker 驱动，
`vsync=false` 之后 Present 立即返回、vblank 中断按 CPU 速度狂发，
游戏逻辑自然跑得比实时快（实测无头环境 50s / 18000+ 帧 ≈ 360fps）。

修改（`src/DoubleDragon_app.h`）：

```cpp
void OnPreLaunchModule() override {
  // 用户从命令行显式改过就不覆盖（例如 --vsync=false 做性能测试）
  if (!rex::cvar::HasNonDefaultValue("vsync")) {
    rex::cvar::SetFlagByName("vsync", "true");
  }
  rex::cvar::SetFlagByName("host_present_from_non_ui_thread", "true");
  REXLOG_INFO("present config: vsync={} host_present_from_non_ui_thread={}", ...);
}
```

`host_present_from_non_ui_thread=true` 保留：guest 主循环直接在 guest 线程
调 VdSwap，默认走 UI 线程派发会死锁。

实测（无头沙箱，60s 连续）：

| | vsync=false | vsync=true |
|---|---|---|
| 帧率 | ~360fps（快进） | **58.4fps（60Hz 节拍）** |
| 60s 帧数 | 18000+ | 3510 |
| 稳定性 | 正常 | 60s 无 FATAL / 无 trap，进程存活 |

另：之前"运行几十秒后进程消失"是沙箱在上一个 bash 调用结束时杀掉残留进程，
不是崩溃——同一次调用内连续跑 60s 一切正常。

启动时 `rex_runtime.log` 会打印一行 `present config: vsync=true ...` 方便核对。

---

## 15. 按 A 闪退：运行时拼出来的回调地址（2026-10-30 深夜，build82）

现场：标题屏提示按 A，按下瞬间退出。

```
[NtCreateFile] FAILED: game:/ui/skins/Graphics/en-en/btn_Select.wav
XamShowDeviceSelectorUI(...)
[FATAL] Call to invalid or unregistered function at guest address 0x820EE688
```

`trap_dump.txt` 给出 `lr=0x820E4434`，调用点是

```asm
0x820E4420  lwz  r4,8(r31)      ; arg2
0x820E4424  lwz  r3,4(r31)      ; arg1
0x820E4428  lwz  r11,0(r31)     ; fp  <- 0x820EE688
0x820E442C  mtctr r11
0x820E4430  bctrl
```

### 15.1 这个地址为什么 codegen 看不到

`0x820EE688` 是 12 字节的跳板（jmp thunk）：

```asm
0x820EE688  lis  r11,0x8241
0x820EE68C  addi r3,r11,-0x288   ; r3 = &global_0x8240FD78
0x820EE690  b    0x820F6A20
```

它**不在 `.pdata`**（没有 unwind 信息），也**没有任何静态 call/branch 指向**：
调用方是在 0x820EE6D8 处现拼出来的

```asm
0x820EE6D8  lis  r11,0x820F
0x820EE6F0  addi r4,r11,-0x1978  ; r4 = 0x820EE688
0x820EE6F4  bl   0x820E41E0      ; 存进回调结构
```

这是 MSVC 生成的「成员函数指针/全局单例适配器」，游戏里到处都是。

### 15.2 试过的两条错路（别再走）

1. **把所有"前面是终结指令"的地址当函数入口**（tools/gen_entry_sweep.py）
   —— 捞出 8222 个，其中大量是函数体中间的位置。声明后 codegen 会把原函数
   从中间切断，产生上千条 `Unresolved branch`，运行时直接
   `[FATAL] Unresolved branch from 0x820F0AC0 to 0x820F0A9C`。**已放弃**。
2. **按 codegen 报的未解析分支反推并剔除**（tools/iterate_codegen.py）
   —— 能收敛一部分，但分支两端之间不一定有我们的声明，最终还是剩几百条。
   **只保留它做"补缺口"用**（`target not in any function` 那一类）。

### 15.3 现在的做法：逐个验证

`tools/prune_entries.py`：

1. 线性扫描 `.text`，跟踪 `lis` 的页基址，遇到 `addi/ori` 就拼出地址，
   落在代码区且 4 字节对齐、能解码的 → 候选（`tools/gen_callback_entries.py`）。
2. 只留"短 thunk"：从候选地址起 ≤16 条指令内遇到 `b/blr/bctr` 的。
3. **一个一个**写进 manifest 跑 codegen：干净（exit 0、无 Unresolved）就保留，
   否则撤回。因为加一个入口会改变函数划分，必须累积验证。

结果：165 个候选里保留 96 个，包含 `0x820EE688`；codegen 干净，
注册函数 10880 → 10976。

### 15.4 顺带修掉的两类生成代码问题（都在 tools/apply_patches.py）

- **跨函数 goto**：`goto loc_X` 而 X 已被拆成独立函数 → 编译报
  `use of undeclared label`。改成尾调用 `{ FUN_X(ctx, base); return; }`
  （X 的块以 blr 结束，语义等价），并在文件头补 `DECLARE_REX_FUNC(FUN_X);`
  （每个 recomp.N.cpp 只 include 按需裁剪的 funcs.N.h）。
- **pch 陷阱 / 86528 abort / 497F8 skip**：老样子，codegen 后必须重打。

### 15.5 标准流程（每次改 manifest 或 codegen 后）

```
rexglue codegen DoubleDragon_manifest.toml --ignore-stamp
python tools/iterate_codegen.py      # 有缺口/未解析分支时用来收敛
python tools/apply_patches.py        # 三处补丁 + 跨函数 goto
cmake --build build --config Release
```

再遇到 `[FATAL] ... unregistered function at 0xXXXXXXXX`：
把该地址补进 manifest 的 `[entrypoint.functions]`，
跑 `tools/prune_entries.py`（会重新验证一遍全体候选）或直接 codegen 验证是否干净，
然后按上面的流程重建。`trap_dump.txt` 里的 `lr` 就是调用点，反汇编能看出目标怎么来的。

### 15.6 状态

build82：标题屏 45s 稳定，57.8fps，无 FATAL / 无 trap。按 A 之后的路径待用户实测。

---

## 16. 界面语言（2026-10-31，build82 之后只改素材）

用户现场：**"可以游玩了，但显示的欧洲某国语言"**。

### 16.1 语言是运行时选的，不是写死的

游戏导入 `XGetLanguage`（xboxkrnl），返回值决定 `game\ui\<lang>\...` 这层目录名。
rex 的 `XGetLanguage` 由 cvar `user_language` 驱动（`XLanguage`：1 English、2 Japanese、
3 German、4 French、5 Spanish、6 Italian、7 Korean、8 TChinese、9 Portuguese、10 SChinese）。

实测（`heartbeat`/日志里的 NtCreateFile 路径即可看到）：

```
DoubleDragon.exe                    -> ui\screens\en-en\...
DoubleDragon.exe --user_language=8  -> ui\screens\zh-cht\StringTable.xus / zh-cht\...
```

路径字符串在 XEX 里：`file://game:/ui/screens/`、`file://game:/ui/` + `StringTable.xus`。

### 16.2 真正的 bug：`en-en` 的串表是德语副本

`game/ui/screens/<lang>/` 下的 `.xus` 与 png **各语言完全相同**（md5 一致，文本都来自串表），
真正区分语言的是 `game/ui/<lang>/StringTable.xus`。而本机素材里：

```
game/ui/en-en/StringTable.xus  ==  game/ui/de-de/StringTable.xus   (md5 85157587…)
```

解出来是 "ZURÜCK / ABBRECHEN / Allgemein…"，所以默认英文界面显示德语。
英文原表恰好在 `game/ui/StringTable.xus`（"BACK / CANCEL / Overall…"），覆盖过去即可。

验证方式（很省事）：把某个语言的 `StringTable.xus` 临时改名，跑一次，日志里出现
`[NtCreateFile] FAILED: path='game:/ui/zh-cht/StringTable.xus'` 就证明它读的是这一份。

### 16.3 落地

- `game/ui/en-en/StringTable.xus` ← `game/ui/StringTable.xus`（英文）。
- 部分语言目录缺 5 张通用图（`legal_bg/legal_empire/legal_text/LargeDDLogo/razorworks_logo`），
  补齐后各语言 screens 目录都是 45 个文件（原来 en-en 45 / de-de 43 / fr-fr 40 …）。
- 新增 `tools/fix_lang_assets.py`（幂等）：重新解包素材后跑一次即可，
  只修 en-en 串表 + 补齐各语言通用图，其它语言的串表原样保留。
- 新增 `play_en.bat` / `play_zh.bat` 两个启动器（中文 = `--user_language=8`）。

注意素材不入库（`.gitignore` 忽略 `game/`），所以这些修复只在本机生效，
换机器/重新解包后要再跑 `python tools/fix_lang_assets.py`。

本作只提供繁体中文（`zh-cht`），没有 `zh-cn`；`ui/arial.ttf` 是 6.6MB 的 CJK 覆盖字体
（cmap 含 U+4E00–U+9FA5 与 Hangul），中文不需要额外字体。

### 16.4 实测

| | 英文（默认） | 中文（--user_language=8） |
|---|---|---|
| 串表加载 | `ui/en-en/StringTable.xus` 成功 | `ui/zh-cht/StringTable.xus` 成功 |
| 界面图片 | 全部走 `en-en` | 全部走 `zh-cht` |
| FATAL | 0 | 0 |

剩余 `[NtCreateFile] FAILED` 都是"先探 `<lang>` 目录、再回退到 skins 根目录"的正常探测
（`btn_Focus.wav`、`ButtonNorm.png`、`advert_*.png` 等，英文模式同样有 9 条）。

---

## 17. 游玩时 BGM 有"电流声"：是原作的锅，不是移植问题

用户现场：**"音乐还有点奇怪，只有游玩的时候才会有，标题画面正常。xenia_canary 也是这样。"**

结论：**正常现象**。三条证据：

1. 本作内置了街机原版音频硬件模拟：`game/RomData/ddragon.snd0/.snd1`（Z80 声音程序，
   各 ~48KB）、`ddragon.mcu`、`cpu0/cpu1`（HD6309）等原版 ROM。游玩时的 BGM/音效是
   **实时跑 1987 年街机的 YM2151(FM) + 2× OKI MSM5205(4bit ADPCM)** 生成的。
2. 标题画面用的是 `game/ui/uimusic.xma`（XBLA 版重新编曲的曲子，干净的流媒体音频），
   所以"标题正常、游玩有电流感"完全对得上。
3. 原版音源本身就是这个听感。Kotaku 的 Morning Music 专栏写 DD1 街机版：
   *"We move on to the also-beloved first-level theme… There's an immediate buzz! It sounds
   like a damn mosquito… How much of that comes down to the Yamaha YM2151 audio hardware
   and how much is just Yamane's choices is unclear"*；同期评测也写 "Sound: accurate to the
   original arcade game"（GameSpot 用户评测）、"the standard graphics and audio are all intact"。

xenia_canary 表现一致，进一步说明是游戏自带的模拟器输出，而不是我们的 XMA/SDL 路径问题。
想听"好版本"只能听 1988 年的编曲专辑（Original Sound of Double Dragon），游戏内没有。

## 18. 简体中文（zh-cn 语言新增，不动原版繁体）（2026-10-31 凌晨）

### 结论
原版繁体 `zh-cht/` 零改动；新增独立 `zh-cn/` 简体资源，由 `ui_lang_simp` 开关在
运行时把语言重定向过去。繁/简/英三语言可切换，入口在 Double Dragon 启动器的
「界面语言」下拉框（写 toml），或命令行 `--user_language=8 --ui_lang_simp=true`。

### 原理（逆向确认）
- 语言目录名表在 guest 镜像里是 **UTF-16BE 宽字符串表**（`dd_dec.xex` 偏移 0x60B0）：
  `"en-us" "pt-pt" "zh-cht" "ko-kr" "it-it" "es-es" "fr-fr" "de-de" "ja-jp" "en-en"`，
  每个 `\0` 结尾、2 字节对齐、紧密排列。游戏按 XGetLanguage 枚举取名拼
  `game\ui\<目录>\...` 路径。XEX 文件本体搜不到这些字符串（段落压缩），
  解包镜像里才有。
- 简体实现 = `OnPostLaunchModule`（主 guest 线程已创建、尚未执行）时扫描
  guest 内存 `0x82000000..0x84000000`，把 `"zh-cht"`（12 字节宽字符）就地改写为
  `"zh-cn\0"`（等长）。命中点在只读映射页，**直接写会 guest AV**（实测
  0x820040B0），必须先 `VirtualProtect(PAGE_READWRITE)` 写完再还原。
- 字体无需新加：游戏自带 `arial.ttf`（裁剪版 TTF，无 maxp 表）cmap 覆盖约
  20902 个 CJK 汉字，简体常用字（简/龙/双/门…）全部有字形。

### 资源生成（tools/t2s_stringtable.py，幂等）
- `XUIS` 串表格式：`"XUIS"` + u32LE 版本 + **u16BE 文件大小** + **u16BE 字符串
  「对」数** + `[u16BE 长度][UTF-16BE 数据]` 流；每对 = 值、键名（键名 ASCII `TXT_*`）。
  zh-cht 串表 7240 B / 130 对 / 260 条。
- OpenCC `t2s`（opencc-python-reimplemented）逐字 1:1 映射，UTF-16 字节数不变
  （91 条含繁体字，7240B -> 7240B），头部大小字段自然不变。
- 除 `ui\zh-cn\StringTable.xus` 外还镜像三类语言资源（游戏按语言目录相对查找，
  缺一个就 NtCreateFile FAILED）：
  - `ui\skins\Graphics\zh-cn\`（按键音 + 按钮图，镜像 en-en 子目录 + 根目录兜底）
  - `ui\skins\zh-cn\skin_main.xus`（每语言皮肤布局，镜像 zh-cht）
  - `ui\screens\zh-cn\`（镜像 zh-cht 的 45 个文件 + screens 根目录共享图补齐，
    共 196 个；Bar.png/advert_*/Button* 等在根目录，各语言子目录原来就缺）

### 顺手修复（从 STFS 原包提取）
- `ui\skins\Graphics\en-en\btn_Select.wav`（94,284 B）与 `Select.wav`（129,312 B）：
  之前按键无声的根因是解包缺了这两个文件，原 LIVE 包里有，已按「条目 size 字段
  与 RIFF 长度互证」的方式提取补上。
- STFS 目录项布局（自逆向校准）：`[u32 created][u32 modified][name 0x28]
  [u32?][u32?][u32?][u32 size BE][8B 时间戳]`，条目 0x40 步进、size 在 name+0x34
  （大端 u32）；文件数据起点用内容魔数互证（arial.ttf @LBA1804、zh-cht 串表 @LBA6112）。

### 启动器（D:/Projects/DoubleDragon.Loader，从 TimePilot.Loader 复制改造）
- 全库改名 TimePilot->DoubleDragon，`TitleId = 0x5841085F`，exe/toml 名对应。
- 画质组新增「界面语言」下拉：English(1) / 繁體中文(8) / 简体中文(8+ui_lang_simp)。
- toml 模板补 `user_language` / `ui_lang_simp` 说明。
- 注意：启动器重新解包会整体替换 game/，之后需重跑
  `tools/fix_lang_assets.py` 和 `tools/t2s_stringtable.py` 重建 en-en/zh-cn 修复。

### 18.1 画面文案也在 screens/*.xus 内嵌（实测修正）（10-31 早）
- 用户实测发现「左菜单繁体、右描述简体」：菜单按钮文字**不在 StringTable.xus**，
  而是编译进 `screens/<lang>/screen_*.xus` 的字符串（该文件同样被语言重定向读取，
  之前整目录从 zh-cht 原样复制所以仍是繁体）。
- screen_*.xus 与 StringTable 同构：XUIS 头（长度不统一，实测 10/12 字节）+
  [u16BE len][UTF-16BE] 字符串流，键名（screen_main.button_xxx.Text）与值成对。
- 修复：t2s_stringtable.py 新增 `convert_xus_strings()`——先探测头部长度（要求
  解析干净走到 EOF），再对每条字符串做 OpenCC 等长替换；长度一变立即报错拒绝
  （防 1->N 映射破坏偏移）。screens 40 个文件共改动 158 条，文件大小全部不变。
- 全量校验：zh-cn 全部 42 个 .xus 再扫一遍，繁体残留 0。
- 既有非问题：`ui\skins\<lang>\ButtonDis/Focus/Norm.png` 与
  `screens\<lang>\Empire_Logo_Large.png` 请求失败在 zh-cht/en-en 下同样出现
  （前者游戏回退到 skins 根目录的同名文件，后者全库无此文件、属死引用）。
- 实机截图验证：主菜单/暂停选单全部简体（继续游戏/说明与选项/排行榜/成就/
  其他功能/离开游戏/选择/返回）。

## 19. XBL 联网提示静默化（xbl_off）（2026-10-31）

**现象**：开局/回主菜单时常弹「本设定档并未连线至 Xbox Live…」消息框（ReXGlue 运行时
的 ImGui Message Box；默认字体无 CJK，中文全显示成 `?`）。用户要求别弹，但**成就/
排行榜那类主动查询的弹窗要保留**。

**关键事实**：ReXGlue 对消息框是**立即完成** overlapped（约 100 ms deferred），那个
ImGui 窗口只是常驻装饰——所以「跳过原调用 + 手动 `CompleteOverlappedImmediate` +
广播 `XN_SYS_UI(id=9)`」是安全等价的，不会卡住调用方。

**实现**
- `src/xbl_off.cpp`：`dd_hook_XamShowMessageBoxUI` 等 7 个 hook。先读 guest 宽字符串
  （UTF-16BE）打日志，再按策略处理：命中开局提示关键字（`最高分`/`并未连线`/`並未連線`）
  就写 `*pdwResult=0`、广播 XN_SYS_UI 1→完成→0、`r3=0` 返回；否则原样调
  `__imp__XamX` 透传（成就/排行榜弹窗照旧）。
- `XamShowAchievementsUI` 特殊：运行时侧是 STUB，不完成 overlapped → 演示流程会崩。
  xbl_off 打开时直接写 `*pdwResult=0` 并完成 `r8`（overlapped）。
- cvar `xbl_off`（默认 `true`，分类 `Game`）；loader 侧「关闭 Xbox LIVE 联网提示」复选框
  + toml 模板写入。
- 第 9 个参数（XOVERLAPPED*）在 `r1+0x54`：由 `sub_821215B8` 的 `lwz r11,180(r1);
  stw r11,84(r1)`（stwu r1,-96）反推。

**踩坑**：只改 `DoubleDragon_init.cpp` 的导入映射表**完全无效**——codegen 把导入 thunk
的调用点直接内联成 `__imp__XamX(ctx, base);` 宿主调用，不查映射表。必须同时替换
`DoubleDragon_recomp*.cpp` 里的调用点（11 处）。`tools/apply_patches.py::patch_xbl_hooks()`
两处都做，且 pch 声明按「逐个缺失才补」注入（早先按整体 marker 判断导致新 hook 漏声明）。

## 20. 画册（其他功能 → 原版图档）闪退：二分查找拿到 NULL 条目（2026-10-31）

**现象**：进入「原版图档」立刻 FATAL `read of guest 0x00000000`；xenia_canary 正常。

**先排除的**：4 张海报全在（`DD_Flyer_US_Front/US_Back/Jp_Front/Jp_Back.png`）、界面
`screen_viewflyer.xur` 也在；`Skins\Bar.png`、`Empire_Logo_Large.png` 的补档也与本崩溃
无关（A/B 全同）。也不是 xbl_off 拦截导致的（`--xbl_off=false` 崩得一模一样）。

**崩溃链**（GUEST CRASH dump 逐层回溯）
```
sub_820FC588  按名字取对象，取不到就用原对象
 └ sub_82104070   读全局表指针（0x821D942C）
    └ sub_8232FB60  表有效性检查（sub_8218DD58）
       └ sub_8234E5C0  在 {count, ?, entries} 有序表里二分查 UTF-16 key
          └ sub_82394B70  UTF-16 strcmp —— 崩在 $+0x52 的 `lhz r10,0(r4)`，r4 = NULL
```
`r3`（key）= `0x7018EA10` → 内容是 `" 1/4"`（本地化后是「页 1/4」）；`r4` 是**表条目的
name 字段**（`lwz r4,0(r11)`），为 0 → 该有序表里存在 name 为空的条目。

**修复**（`tools/apply_patches.py` 注入 `sub_82394B70` 入口 guard）
- `r3`/`r4` 任一为 0 时不进比较循环，按「空串」语义返回：两者都空→0，r3 非空→1，
  r3 空→-1（即 NULL 条目视为小于任何非空 key），二分查找继续、不再解引用 guest 0。
- 诊断输出由 cvar `diag_nullwstr`（默认 `false`）控制：打开后可看到
  `[nullwstr] sub_82394B70 NULL operand: key=' 1/4' entry=0x00000000 lr=0x8234E610`。
- 不改游戏逻辑，只补运行时对「空条目」的容忍（xenia 下同样数据不崩，说明原版链路上
  这个空槽本不该被解引用到）。

**实测**：页 1/4 = `DD_Flyer_US_Front`（默认页）、2/4 = HOW TO PLAY（US 背面）、
3/4 = 日版封面、4/4 = 日版说明；A 放大 / Y 缩小 / B 返回都正常，退出再进两次仍稳定。

## 21. 成就浮层（本地显示，不依赖 XBL）（2026-10-31）

**背景**：运行时对 `XamShowAchievementsUI` 只有空桩（STUB），游戏主菜单"成就"
点了没反应。参照 TimePilot 项目（`src/achievements_dialog.h` + App 集成）移植
本地成就浮层，成就数据/图标由运行时从 xex 的 XDBF 元数据与 `game/` 根目录的
成就 png（GetTheGirl.png、Hero.png 等）加载，共 12 项 200G。

**集成**（与 TimePilot 相同）
- `src/achievements_dialog.h`：`DoubleDragonAchievementsDialog`（继承
  `rex::ui::AchievementsOverlayDialog`，自绘列表 + 手柄滚动：左摇杆模拟量平滑
  滚动 / 十字键恒速滚动 / B 关闭）。不用 SDK 内置浮层的原因：其行无聚焦控件
  且 GetIcon 为 private。
- App：`OnCreateDialogs` 把内置 F7 重绑到 `ToggleAchievementsOverlay()`（防双实例）；
  `OnDestroy` 手动删浮层；`hooks.cpp` 定义 `g_doubledragon_app` 单实例指针。
- 钩子 `dd_hook_XamShowAchievementsUI`：真实签名是
  `XamShowAchievementsUI(user, flags, XOVERLAPPED*)`——游戏包装 sub_82121590 是
  `li r4,0; b 0x823ad0bc`，**overlapped 在 r5**（之前按 r8 读是错的）。先原样调用
  STUB，再叠开浮层。

**三连坑（试错记录，别再走）**——游戏对成就调用有同步的"结果处理路径"：
  a) 完成方式一：`CompleteOverlappedImmediate(ovl)` + 同步广播 XN_SYS_UI 1/0
     → 完成回调在 guest 线程内同步重入，游戏当帧进结果处理路径，
     `sub_820F6558` 里 `lwz r11,20(r10)` 读到 vtable=NULL → `read of guest 0x14`；
  b) 完成方式二：广播"打开"，overlapped 留到浮层关闭再完成 → 完成只能在 UI
     线程发起，运行时内部要 ThreadState → host 崩 "no ThreadState bound"
     （BeginDPCImpersonation 也救不了）；
  c) 完成方式三：只 `CompleteOverlappedDeferred` 不广播 → 游戏当帧读结果
     → `read of guest 0x3F9`。
  最终方案：**完全不碰 overlapped / XN_SYS_UI**，STUB 原样返回（游戏视作
  "UI 未打开"，走既有错误分支），浮层纯叠加。F7 与游戏菜单共用同一实例。

**字体**：SDK 默认 ImGui 字体只带 `GetGlyphRangesChineseSimplifiedCommon`
（简体常用集），而成就文本是原版繁体，「兩/續/擊」等显示成 `?`。在
`OnConfigureFonts`（图集构建前）以 merge 模式补一份系统字体
（msyh.ttc → simhei → simsun 依序探测）+ `GetGlyphRangesChineseFull`，
全部汉字可显示。此修复对 ImGui 侧所有窗口（含 XBL 弹窗）生效。

**实测**：成就浮层打开（0/12、0G/200G，图标与中文名/描述齐全）→ B 关闭 →
菜单继续可用；重复打开、F7 开关均稳定；XBOX LIVE 游戏/排行榜的"必须登录"
消息框维持原样（用户确认保留）。
