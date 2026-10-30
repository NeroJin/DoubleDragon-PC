# DoubleDragon (XBLA) — 静态重编译

用 ReXGlue SDK 把 Xbox 360 上的 **Double Dragon**（XBLA）从 PowerPC 机器码静态重编译成 x64 原生程序。

> 本仓库只含**工程脚手架与移植补丁**，不含任何游戏本体数据。
> 运行需要你自己合法持有的游戏文件（`default.xex` + 素材），这里不提供下载。

## 目录结构

| 路径 | 说明 |
|---|---|
| `src/` | 手写代码：入口 `main.cpp`、`DoubleDragon_app.h`（App 定制钩子） |
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

## 说明

仅供学习与研究。游戏资源版权归原权利人所有，请使用自己合法持有的游戏文件。
