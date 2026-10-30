# DoubleDragon - ReXGlue Recompiled Project
#
# SDK boilerplate lives in generated/rexglue.cmake.

cmake_minimum_required(VERSION 3.25)
project(DoubleDragon LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

include(generated/rexglue.cmake)

# Sources
set(DOUBLEDRAGON_SOURCES
    src/main.cpp
)

if(WIN32)
    add_executable(DoubleDragon WIN32 ${DOUBLEDRAGON_SOURCES})
else()
    add_executable(DoubleDragon ${DOUBLEDRAGON_SOURCES})
endif()

# GPU_PLUGINS xenos: 构建时把 SDK 的 rexgpu-xenos.dll 搬到 exe 旁边，
# 运行时 gpu_plugin cvar 填 "xenos" 即可按 <exe目录>/rexgpu-xenos.dll 找到。
rexglue_setup_target(DoubleDragon GPU_PLUGINS xenos)
