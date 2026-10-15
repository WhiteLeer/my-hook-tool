cmake_minimum_required(VERSION 3.20)
project(MyHookRenderDocTrigger LANGUAGES CXX)

set(RENDERDOC_ROOT "" CACHE PATH "Custom RenderDoc source checkout")
if(NOT RENDERDOC_ROOT)
  message(FATAL_ERROR "Set RENDERDOC_ROOT to the custom RenderDoc checkout")
endif()

add_executable(MyHookRenderDocTrigger main.cpp)
target_compile_features(MyHookRenderDocTrigger PRIVATE cxx_std_17)
target_compile_definitions(MyHookRenderDocTrigger PRIVATE
  UNICODE _UNICODE WIN32_LEAN_AND_MEAN RENDERDOC_PLATFORM_WIN32)
target_include_directories(MyHookRenderDocTrigger PRIVATE
  "${RENDERDOC_ROOT}"
  "${RENDERDOC_ROOT}/renderdoc/api/replay")
target_link_libraries(MyHookRenderDocTrigger PRIVATE
  "${RENDERDOC_ROOT}/x64/Development/renderdoc.lib")
set_target_properties(MyHookRenderDocTrigger PROPERTIES
  OUTPUT_NAME "my-hook-renderdoc-trigger"
  MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_custom_command(TARGET MyHookRenderDocTrigger POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "${RENDERDOC_ROOT}/x64/Development/renderdoc.dll"
    "$<TARGET_FILE_DIR:MyHookRenderDocTrigger>/renderdoc.dll")
