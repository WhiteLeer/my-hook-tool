cmake_minimum_required(VERSION 3.20)
project(MyHookRdcDrawProbe LANGUAGES CXX)

set(RENDERDOC_ROOT "" CACHE PATH "Custom RenderDoc checkout")
set(RENDERDOC_BIN "" CACHE PATH "Matching RenderDoc build directory")
set(JSON_INCLUDE_DIR "" CACHE PATH "Directory containing nlohmann/json.hpp")
if(NOT RENDERDOC_ROOT OR NOT RENDERDOC_BIN OR NOT JSON_INCLUDE_DIR)
  message(FATAL_ERROR "Set RENDERDOC_ROOT, RENDERDOC_BIN and JSON_INCLUDE_DIR")
endif()

add_executable(my-hook-rdc-draw-probe main.cpp)
target_compile_features(my-hook-rdc-draw-probe PRIVATE cxx_std_17)
target_compile_definitions(my-hook-rdc-draw-probe PRIVATE RENDERDOC_PLATFORM_WIN32)
target_include_directories(my-hook-rdc-draw-probe PRIVATE
  "${RENDERDOC_ROOT}/renderdoc/api/replay" "${JSON_INCLUDE_DIR}")
target_link_libraries(my-hook-rdc-draw-probe PRIVATE "${RENDERDOC_BIN}/renderdoc.lib")
set_target_properties(my-hook-rdc-draw-probe PROPERTIES
  MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_custom_command(TARGET my-hook-rdc-draw-probe POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different "${RENDERDOC_BIN}/renderdoc.dll"
    "$<TARGET_FILE_DIR:my-hook-rdc-draw-probe>/renderdoc.dll")
