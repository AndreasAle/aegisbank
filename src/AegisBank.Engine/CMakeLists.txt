cmake_minimum_required(VERSION 3.22)
project(AegisEngine VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

# Link-time optimisation lets the compiler inline the domain function into the
# C ABI wrapper and drop the intermediate struct copies between the two.
option(AEGIS_ENABLE_LTO "Enable link-time optimisation in Release builds" ON)
if(AEGIS_ENABLE_LTO)
  include(CheckIPOSupported)
  check_ipo_supported(RESULT AEGIS_IPO_SUPPORTED OUTPUT AEGIS_IPO_OUTPUT LANGUAGES CXX)
  if(AEGIS_IPO_SUPPORTED)
    set(CMAKE_INTERPROCEDURAL_OPTIMIZATION_RELEASE ON)
  else()
    message(STATUS "LTO not supported by this toolchain: ${AEGIS_IPO_OUTPUT}")
  endif()
endif()

option(AEGIS_BUILD_TESTS "Build GoogleTest unit tests" ON)
option(AEGIS_BUILD_BENCHMARKS "Build the native benchmark executable" ON)

set(AEGIS_SOURCES src/transfer.cpp src/engine_c_api.cpp)

# Shared library consumed by .NET through P/Invoke.
add_library(aegis_engine SHARED ${AEGIS_SOURCES})
target_include_directories(aegis_engine PUBLIC include)
target_compile_definitions(aegis_engine PRIVATE
  AEGIS_ENGINE_BUILD
  AEGIS_ENGINE_VERSION_STRING="${PROJECT_VERSION}")

# Static variant for tests and benchmarks, so they exercise the same objects
# without depending on the loader path.
add_library(aegis_engine_static STATIC ${AEGIS_SOURCES})
target_include_directories(aegis_engine_static PUBLIC include)
target_compile_definitions(aegis_engine_static PUBLIC AEGIS_ENGINE_BUILD
  PRIVATE AEGIS_ENGINE_VERSION_STRING="${PROJECT_VERSION}")

foreach(tgt aegis_engine aegis_engine_static)
  if(MSVC)
    target_compile_options(${tgt} PRIVATE /W4 /WX /permissive-)
  else()
    target_compile_options(${tgt} PRIVATE -Wall -Wextra -Wpedantic -Wconversion -Wshadow -Werror)
  endif()
  if(CMAKE_CXX_COMPILER_ID STREQUAL "GNU")
    # The engine copies small structs field by field. GCC's SLP vectoriser merges
    # some of those copies into 16-byte loads over narrower stores, which blocks
    # store-to-load forwarding. Measured with aegis_engine_bench (GCC 13.3,
    # Ryzen 7 6800H): about 31 ns -> 25 ns per call through the C ABI.
    target_compile_options(${tgt} PRIVATE -fno-tree-slp-vectorize)
  endif()
endforeach()

if(CMAKE_CXX_COMPILER_ID STREQUAL "GNU" AND NOT WIN32)
  # Keep the runtime image free of a libstdc++ version requirement.
  target_link_options(aegis_engine PRIVATE -static-libstdc++ -static-libgcc)
endif()

if(AEGIS_BUILD_TESTS)
  enable_testing()
  find_package(GTest QUIET)
  if(NOT GTest_FOUND)
    include(FetchContent)
    FetchContent_Declare(googletest
      URL https://github.com/google/googletest/archive/refs/tags/v1.15.2.tar.gz)
    set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
    FetchContent_MakeAvailable(googletest)
  endif()

  add_executable(aegis_engine_tests
    tests/transfer_tests.cpp
    tests/checked_math_tests.cpp
    tests/c_api_tests.cpp)
  target_link_libraries(aegis_engine_tests PRIVATE aegis_engine_static GTest::gtest GTest::gtest_main)

  include(GoogleTest)
  gtest_discover_tests(aegis_engine_tests)
endif()

if(AEGIS_BUILD_BENCHMARKS)
  add_executable(aegis_engine_bench bench/engine_bench.cpp)
  target_link_libraries(aegis_engine_bench PRIVATE aegis_engine_static)
endif()
