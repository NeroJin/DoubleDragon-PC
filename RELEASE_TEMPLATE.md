# Double Dragon XBLA → PC（ReXGlue 移植）v0.1

> ⚠️ **合规须知 / Compliance Notice**
> 本 Release 提供的 `DoubleDragon.exe` 仅是由你自备的原版游戏**静态重编译出的封装/游戏逻辑**，
> **不含任何受版权保护的游戏素材**（无 xex、无街机 ROM、无贴图/音频）。
> 运行前**必须**把你自己合法持有的《双截龙》XBLA 包（`.LIVE/.PIRS/.CON`）用
> `DoubleDragon.Loader` 解包到 `game/` 目录。本项目**仅供学习与研究**，游戏资源版权归原权利人所有。
> **我们不提供、也不提供任何指向版权游戏文件的链接。**

## 这是什么
把 Xbox 360 XBLA 版《双截龙》经 ReXGlue 重编译为原生 Windows 程序的移植工程。游戏内置"街机呈现"
模式，运行时加载原版街机 ROM 并套用高清图形层（`game/RomData/ddragon.higfx0/1/2`）。

## 如何运行（简易路径）
1. 自备**合法持有**的原版《双截龙》XBLA 包（`.LIVE/.PIRS/.CON`）。
2. 用 `DoubleDragon.Loader`（仓库内 `DoubleDragon.Loader/` 工程）把包解包到 `game/`。
3. 把本 Release 的 `DoubleDragon.exe` 放到与 `game/` 同级目录。
4. 首次运行会自动生成 `DoubleDragon.toml`；启动 `DoubleDragon.exe` 即可。

> 若提示缺少 `rexruntime.dll` / `rexgpu-xenos.dll`：这两个是 ReXGlue SDK 运行时，请从你本地的
> ReXGlueSDK 目录取（再分发许可需向 ReXGlue 确认），放到 exe 同级。

## 想从源码构建？（替代路径）
克隆仓库后见 `README.md` 的「构建」节：`rexglue init` → codegen → CMake 编译。
仓库**不含**任何游戏数据；`game/` 与 `generated/` 均需本地生成。

## 本 Release 包含 / 不包含
**包含：** `DoubleDragon.exe`（重编译封装层，MIT）、`README.md`、`LICENSE`、`PORTING_NOTES.md`。
**不包含（需你自备）：** 任何 xex / ROM / 素材；ReXGlue SDK 运行时 DLL（见上）。

## 免责声明
本工程为个人学习 / 研究用途的逆向与移植示例。不对使用本工程导致的任何版权、法律或使用风险负责。
请在法律允许范围内使用，并支持正版。
