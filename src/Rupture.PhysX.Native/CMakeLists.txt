cmake_minimum_required(VERSION 3.24)

cmake_policy(SET CMP0091 NEW)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:debug>:Debug>" CACHE STRING "Static CRT for every PhysX target" FORCE)

project(rpt_physx_bridge LANGUAGES C CXX)

if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "rpt_physx_bridge supports only 64-bit Windows.")
endif()

set(RPT_PHYSX_SOURCE_DIR "" CACHE PATH "Path to the pinned NVIDIA PhysX 4.1.2 physx directory")
if(NOT EXISTS "${RPT_PHYSX_SOURCE_DIR}/include/PxPhysicsVersion.h")
    message(FATAL_ERROR "Set RPT_PHYSX_SOURCE_DIR to the PhysX repository's physx directory.")
endif()

get_filename_component(RPT_PHYSX_REPOSITORY_DIR "${RPT_PHYSX_SOURCE_DIR}" DIRECTORY)
set(PHYSX_ROOT_DIR "${RPT_PHYSX_SOURCE_DIR}" CACHE INTERNAL "PhysX source root" FORCE)
set(PXSHARED_PATH "${RPT_PHYSX_REPOSITORY_DIR}/pxshared" CACHE INTERNAL "PxShared source root" FORCE)
set(CMAKEMODULES_PATH "${RPT_PHYSX_REPOSITORY_DIR}/externals/cmakemodules" CACHE INTERNAL "PhysX CMake modules" FORCE)
set(CMAKEMODULES_NAME "CMakeModules" CACHE INTERNAL "PhysX CMake modules name" FORCE)
set(CMAKEMODULES_VERSION "1" CACHE INTERNAL "PhysX CMake modules version" FORCE)
set(TARGET_BUILD_PLATFORM "windows" CACHE STRING "PhysX target platform" FORCE)
set(PX_GENERATE_STATIC_LIBRARIES ON CACHE BOOL "Build PhysX statically" FORCE)
set(NV_USE_STATIC_WINCRT ON CACHE BOOL "Use the static Windows CRT" FORCE)
set(PX_BUILDSNIPPETS OFF CACHE BOOL "Do not build PhysX snippets" FORCE)
set(PX_BUILDPUBLICSAMPLES OFF CACHE BOOL "Do not build PhysX samples" FORCE)
set(PX_BUILDPVDRUNTIME OFF CACHE BOOL "Do not build the PVD runtime" FORCE)
set(PX_GENERATE_SOURCE_DISTRO OFF CACHE BOOL "Do not generate a source distribution" FORCE)
set(USE_RESPONSE_FILES ON CACHE BOOL "Use response files for PhysX" FORCE)
set(PX_OUTPUT_LIB_DIR "${CMAKE_BINARY_DIR}/physx-output" CACHE PATH "PhysX library output root" FORCE)
set(PX_OUTPUT_BIN_DIR "${CMAKE_BINARY_DIR}/physx-output" CACHE PATH "PhysX binary output root" FORCE)

add_subdirectory("${RPT_PHYSX_SOURCE_DIR}/source/compiler/cmake" physx-sdk EXCLUDE_FROM_ALL)
include(cmake/CasingDominance.cmake)

add_library(rpt_physx_bridge SHARED
    include/rpt_physx_bridge.h
    src/rpt_physx_bridge.cpp
    src/rpt_lzma.cpp
    vendor/lzma/LzmaDec.c)

target_compile_features(rpt_physx_bridge PRIVATE cxx_std_17)
target_compile_definitions(rpt_physx_bridge PRIVATE
    RPT_PHYSX_BRIDGE_EXPORTS
    PX_PHYSX_STATIC_LIB
    PX_FOUNDATION_STATIC_LIB
	$<$<CONFIG:debug>:_DEBUG>
	$<$<NOT:$<CONFIG:debug>>:NDEBUG>
    _CRT_SECURE_NO_WARNINGS
    NOMINMAX
    WIN32_LEAN_AND_MEAN)
target_include_directories(rpt_physx_bridge PRIVATE
    "${RPT_PHYSX_SOURCE_DIR}/include"
    "${PXSHARED_PATH}/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/vendor/lzma")
target_link_options(rpt_physx_bridge PRIVATE /DEBUG /OPT:REF /OPT:ICF)

target_link_libraries(rpt_physx_bridge PRIVATE
    PhysX
    PhysXCommon
    PhysXFoundation
    PhysXCooking
    PhysXExtensions)

set_property(TARGET rpt_physx_bridge PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:debug>:Debug>")
set_target_properties(rpt_physx_bridge PROPERTIES
    OUTPUT_NAME "rpt_physx_bridge"
    PREFIX ""
    DEBUG_POSTFIX "")

install(TARGETS rpt_physx_bridge RUNTIME DESTINATION .)

include(CTest)
if(BUILD_TESTING)
    add_executable(rpt_physx_bridge_tests tests/rpt_physx_bridge_tests.cpp)
    target_compile_features(rpt_physx_bridge_tests PRIVATE cxx_std_17)
	target_compile_definitions(rpt_physx_bridge_tests PRIVATE
		$<$<CONFIG:debug>:_DEBUG>
		$<$<NOT:$<CONFIG:debug>>:NDEBUG>)
    target_include_directories(rpt_physx_bridge_tests PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
    target_link_libraries(rpt_physx_bridge_tests PRIVATE rpt_physx_bridge)
    set_property(TARGET rpt_physx_bridge_tests PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:debug>:Debug>")
    add_test(NAME rpt_physx_bridge_tests COMMAND rpt_physx_bridge_tests)
endif()
