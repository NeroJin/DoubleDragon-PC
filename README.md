# DoubleDragon (XBLA) — 静态重编译

用 ReXGlue SDK 把 Xbox 360 上的 **Double Dragon**（XBLA）从 PowerPC 机器码静态重编译成 x64 原生程序。

> 本仓库只含**工程脚手架与移植补丁**，不含任何游戏本体数据。
> 运行需要你自己合法持有的游戏文件（`default.xex` + 素材），这里不提供下载。

## 目录结构

| 路径 | 说明 |
|---|---|
| `src/` | 手写代码：入口 `main.cpp`、`DoubleDragon_app.h`（App 定制钩子） |
| `loader/` | `DoubleDragonLoader` 启动器/解包工具源码（C#/.NET WinForms，需 Visual Studio 构建） |
| `game/` | **游戏素材目录**（不入库）。把解包后的 `default.xex` 及素材直接拷进来 |
| `DoubleDragon_manifest.toml` | codegen 配置：`game_root = "game"`、入口 `game/default.xex` |
| `generated/` | codegen 产出（`rexglue.cmake` + `default/*.cpp`）。**不入库**：从机器码反编译的游戏专有代码有版权，且必须自备原始 xex 本地生成 |

## 素材放置

把解包结果直接平铺进 `game/`，即：

```
game/
  default.xex        ← 必须，manifest 的 entrypoint
  media/  ...        ← 其余素材按解包原样
```

`manifest` 里只写相对路径 `game`，换机器不用改配置。

## 构建

需要 ReXGlue SDK 0.10.0.24-dev 与 Visual Studio 2026（Community，带 ClangCL）。

```bat
cd /d D:\Projects\DoubleDragon
rem 1) 清掉代理变量：大小写并存会让 MSBuild 报 MSB6001
set http_proxy=& set https_proxy=& set HTTP_PROXY=& set HTTPS_PROXY=

rem 2) configure（-T ClangCL 必须：SDK 头文件用了 __builtin_bswap*，MSVC 编不过）
cmake -S . -B build -G "Visual Studio 18 2026" -A x64 -T ClangCL ^
      -DCMAKE_GENERATOR_INSTANCE="D:/Microsoft Visual Studio/18/Community" ^
      -DCMAKE_PREFIX_PATH=D:/ReverseTools/ReXGlueSDK

rem 3) build（codegen 自动作为依赖跑，产物落 generated/default/）
cmake --build build --config Release
```

- 必须显式给 `CMAKE_GENERATOR_INSTANCE`：机器上装了 BuildTools 时，CMake 会挑到没装 ClangCL 的那个，报 `MSB8020`。
- 单独跑 codegen：`cmake --build build --config Release --target DoubleDragon_codegen`
- `generated/` 不入库，**克隆后先执行一次 `rexglue init` 重建 `generated/rexglue.cmake`**（configure 依赖它）：

```bat
D:\ReverseTools\ReXGlueSDK\bin\rexglue.exe init --project-name DoubleDragon --xex-path game/default.xex
```

## 运行

`build/Release/` 下需要：`DoubleDragon.exe`、`rexruntime.dll`、`rexgpu-xenos.dll`、`game/`、`DoubleDragon.toml`（首次运行自动生成）。
两个 rexglue 运行时 dll 由 SDK 提供（`rexglue_setup_target(... GPU_PLUGINS xenos)` 会把 xenos 插件拷到 exe 旁），**未随仓库分发**——再分发许可需向 ReXGlueX 确认。

> **简体中文**：启动器（`loader/`）在选「简体中文」启动时，会自动在 `game/ui/zh-cn/` 重建简体资源——42 个 `.xus` 以**字节补丁**形式随启动器分发（`loader/.../lang_zhcn/patch_v1.bin`，约 8.7 KB），启动时以游戏自带的同名 `zh-cht` 为基底、校验后按 `(偏移, 新字节)` 就地还原出 `zh-cn`；其余中性图片/音频从用户解开的游戏包就地拷贝，**无需手动准备 `zh-cn` 资源**。游戏侧 `lang_redirect` 再把内存里的 `zh-cht` 改写成 `zh-cn` 读取。

## 获取与发布（GoldenEye 范式）

本工程遵循 GoldenEye 007 / Banjo-Tooie XBLA Recomp 的发布范式：

- **预编译 exe（可选）**：Releases 提供 `DoubleDragon.exe`，它只是重编译出的封装/游戏逻辑，
  **不含任何游戏素材**。下载后需你自行提供 XBLA 包并解包到 `game/` 才能运行。
- **我们不提供、也不提供任何指向版权游戏文件的链接。** 请使用你自己合法持有的
  《双截龙》XBLA 包（`.LIVE/.PIRS/.CON`），用 `DoubleDragon.Loader` 解包到 `game/`。
- **两条路径**：
  1. 简易：下载 Releases 里的预编译 exe + 你自己的 XBLA 包 → 用 `DoubleDragon.Loader` 解包到 `game/`
     → 把 exe 放到与 `game/` 同级目录并运行。
  2. 从源码构建：见上方「构建」节（`rexglue init` → codegen → CMake 编译）。

> 注：预编译 exe 由 `generated/` 反编译代码编译而来，属静态重编译的游戏逻辑，分发前请确认你已
> 合法持有原版。仓库不含任何游戏数据，详见 `LICENSE` 与 `PORTING_NOTES.md` 第 23 节。

## 说明

仅供学习与研究。游戏资源版权归原权利人所有，请使用自己合法持有的游戏文件。
