// DoubleDragon - ReXGlue Recompiled Project
//
// 项目专用补丁。ReXGlue 运行时通过内部 guest 函数表派发，不查 exe 原生导出符号，
// 因此这里不需要针对 guest 地址的 dllexport 劫持桩，只放宿主环境真正缺的东西。

#include <cmath>

// MSVC/UCRT 没有 C23 的 roundevenf（四舍六入五成双）。
// simde 的 _mm_round_ps 回退路径会引用它，clang 把 __builtin_roundevenf 降级成 libcall，
// lld-link 于是报 undefined symbol: roundevenf。nearbyintf 走当前舍入模式（默认就近取偶），
// 语义一致且不抛 inexact 异常。
extern "C" float roundevenf(float x) {
    return std::nearbyintf(x);
}
