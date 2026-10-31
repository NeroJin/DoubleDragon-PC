# Double Dragon (XBLA) → PC — v0.1

> ⚠️ **Compliance / 合规须知**
> 本 Release **仅包含**移植封装层、构建脚本、文档，以及开源的 ReXGlue 运行时 DLL。
> **不包含任何受版权保护的游戏素材**（无 `*.xex`、无街机 ROM、无贴图/音频、无反编译出的游戏专有代码）。
> 使用本项目**必须自行合法持有**原版《双截龙》XBLA 包，并用 `DoubleDragonLoader` 在**本地**解包资源。
> 本项目**仅供学习与研究**，游戏资源版权归原权利人所有。详见仓库 `LICENSE`。

## 本包内容（下载 zip 解压后即为这些文件）

| 文件 | 说明 |
|---|---|
| `DoubleDragon.exe` | 游戏封装/重编译启动器（主程序） |
| `DoubleDragonLoader.exe` | 解包工具：把**你自己的** XBLA 包解开到 `game/` |
| `DoubleDragonUnpack.dll` | Loader 依赖的解包库 |
| `rexruntime.dll` | ReXGlue SDK 运行时（BSD-3，开源，可再分发） |
| `rexgpu-xenos.dll` | ReXGlue Xenon→D3D12 GPU 插件运行时（BSD-3） |
| `DoubleDragon.toml` | 推荐运行配置（简体中文、离线 XBL 提示关闭、完整许可证） |
| `RELEASE_NOTES.md` | 本说明 |

## 本包**不包含**（须你自备，绝不提供）

- `default.xex` / `dd_dec.xex` —— 游戏执行文件
- `game/RomData/*` —— 原版街机 ROM（图形 / 程序 / 音频）
- `game/UI/*` —— 游戏 UI 资源

这些均为版权素材，请从你**合法持有**的原版《双截龙》XBLA 包获取。

## 运行步骤

1. 把本 zip 解压到**非 `Program Files`** 的任意文件夹。
2. 准备好你自己的《双截龙》XBLA 包（`.xex` / STFS 容器）。
3. 运行 `DoubleDragonLoader.exe`，指向你的 XBLA 包；它会把资源解包到 exe 同级的 `game/`。
   （或手动把 `game/` 放到 exe 旁边。）
4. 运行 `DoubleDragon.exe` 开始游戏。

## 说明

- 默认分辨率 720p，可在 `DoubleDragon.toml` 调整（`resolution`、`resolution_scale`）。
- 默认**简体中文**（`ui_lang_simp = true`）；改 `false` 回原版繁体。
- 默认关闭 Xbox LIVE 联网提示（`xbl_off = true`），按离线继续。
- 想从源码构建：见仓库 `README.md`（需 ReXGlue SDK + 你自己的游戏文件）。

## 免责声明

本工程为个人学习 / 研究用途的逆向与移植示例，与版权方无关联、未获其背书。
不对使用本工程导致的任何版权、法律或使用风险负责。请在法律允许范围内使用，并支持正版。
