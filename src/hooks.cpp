// DoubleDragon - ReXGlue Recompiled Project
//
// manifest 里 [[entrypoint.midasm_hook]] 声明的宿主钩子集中定义在这里。
// 钩子是普通函数（非 inline）：生成的 _recomp 目标独立编译，它引用的符号
// 必须有一个最终被链入 exe 的强定义。
//
// 目前没有常驻钩子（调试探针用完已移除，过程见 PORTING_NOTES.md）。
// 加钩子的步骤：manifest 追加 [[entrypoint.midasm_hook]]（address/name/registers/return），
// 这里按 void Hook(PPCRegister& rX) 定义同名函数，然后 codegen + 重新构建。

#include <rex/runtime.h>

#include "generated/default/DoubleDragon_init.h"
