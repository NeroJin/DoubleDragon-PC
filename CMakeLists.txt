# DoubleDragon - ReXGlue Recompiled Project
#
# SDK boilerplate lives in generated/rexglue.cmake.

cmake_minimum_required(VERSION 3.25)
project(DoubleDragon LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# 调试信息：崩溃时才能把 host 地址符号化成 sub_XXXXXXXX（Release 也保留 pdb）。
if(MSVC)
    add_compile_options(/Zi)
    add_link_options(/DEBUG)
endif()

include(generated/rexglue.cmake)

# Sources
set(DOUBLEDRAGON_SOURCES
    src/main.cpp
    src/fixes.cpp
    src/crashdump.cpp
    src/hooks.cpp
    src/lang_redirect.cpp
    src/xbl_off.cpp
)

if(WIN32)
    add_executable(DoubleDragon WIN32 ${DOUBLEDRAGON_SOURCES})
    # 生成链接 map：崩溃时按 host 地址反查生成函数名（sub_XXXXXXXX 直接对应 guest 地址）。
    target_link_options(DoubleDragon PRIVATE "/MAP:${CMAKE_CURRENT_BINARY_DIR}/DoubleDragon.map")
else()
    add_executable(DoubleDragon ${DOUBLEDRAGON_SOURCES})
endif()

# GPU_PLUGINS xenos: 构建时把 SDK 的 rexgpu-xenos.dll 搬到 exe 旁边，
# 运行时 gpu_plugin cvar 填 "xenos" 即可按 <exe目录>/rexgpu-xenos.dll 找到。
rexglue_setup_target(DoubleDragon GPU_PLUGINS xenos)
