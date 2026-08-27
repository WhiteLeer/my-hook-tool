cmake_minimum_required(VERSION 3.20)
project(RuntimeBridgeProbe LANGUAGES CXX)

add_library(RuntimeBridgeProbe SHARED RuntimeBridgeProbe.cpp)
target_compile_features(RuntimeBridgeProbe PRIVATE cxx_std_17)
target_compile_definitions(RuntimeBridgeProbe PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
set_target_properties(RuntimeBridgeProbe PROPERTIES OUTPUT_NAME "my-hook-runtime-probe")
