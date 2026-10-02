cmake_minimum_required(VERSION 3.24)
project(dissonancia_core C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
# Only ANA_API functions leave the shared library.
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

option(DZ_TESTS "Build Catch2 tests" ON)
find_package(Threads REQUIRED)

add_library(miniaudio STATIC third_party/miniaudio.c)
target_include_directories(miniaudio PUBLIC third_party)
target_link_libraries(miniaudio PUBLIC Threads::Threads ${CMAKE_DL_LIBS})
if(UNIX AND NOT APPLE)
  target_link_libraries(miniaudio PUBLIC m)
endif()

add_library(dz_core STATIC src/engine.cpp src/rt.cpp src/voice.cpp src/cqt.cpp src/chords.cpp src/bass.cpp src/context.cpp)
target_include_directories(dz_core PUBLIC include src)
target_link_libraries(dz_core PUBLIC miniaudio)
if(WIN32)
  target_link_libraries(dz_core PUBLIC avrt)
endif()
if(MSVC)
  target_compile_options(dz_core PRIVATE /W4)
else()
  target_compile_options(dz_core PRIVATE -Wall -Wextra)
endif()

add_library(dissonancia SHARED src/api.cpp)
target_link_libraries(dissonancia PRIVATE dz_core)

# ABI structs must have no implicit padding (spec §22).
add_library(dz_abi_check OBJECT src/abi_check.cpp)
target_include_directories(dz_abi_check PRIVATE include)
if(MSVC)
  target_compile_options(dz_abi_check PRIVATE /we4820)
else()
  target_compile_options(dz_abi_check PRIVATE -Wpadded -Werror)
endif()
add_dependencies(dissonancia dz_abi_check)

if(DZ_TESTS)
  include(FetchContent)
  FetchContent_Declare(Catch2 GIT_REPOSITORY https://github.com/catchorg/Catch2.git GIT_TAG v3.7.1 GIT_SHALLOW TRUE)
  FetchContent_MakeAvailable(Catch2)
  add_executable(dz_tests tests/test_core.cpp tests/test_voice.cpp tests/test_cqt.cpp tests/test_chords.cpp tests/test_context.cpp tests/rt_trap.cpp)
  target_link_libraries(dz_tests PRIVATE dz_core Catch2::Catch2WithMain)
  enable_testing()
  add_test(NAME dz_tests COMMAND dz_tests)
endif()

# SCORE view (spec §20.4): Verovio, LGPL-3.0, stays a separate shared library loaded at run
# time by the app; it is never linked into the core. Pinned source tarball, built once.
option(DZ_WITH_VEROVIO "Build Verovio (LGPL-3.0) as a separate shared library for the SCORE view" ON)
if(DZ_WITH_VEROVIO)
  include(ExternalProject)
  ExternalProject_Add(verovio
    URL https://github.com/rism-digital/verovio/archive/3120890a5adcee6f8ac7e5ce03974867315adc2a.tar.gz
    URL_HASH SHA256=783c66a167b43668fd6182cbe558a537c71f4e2a0a617786ff0031ae121ae572
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    SOURCE_SUBDIR cmake
    # Its version script runs `git describe` and would find this repository: hide git from it.
    CMAKE_COMMAND ${CMAKE_COMMAND} -E env GIT_DIR=${CMAKE_BINARY_DIR}/no-git ${CMAKE_COMMAND}
    CMAKE_ARGS -DCMAKE_BUILD_TYPE=Release -DBUILD_AS_LIBRARY=ON -DNO_HUMDRUM_SUPPORT=ON
               -DCMAKE_INSTALL_PREFIX=${CMAKE_BINARY_DIR}/verovio -DCMAKE_INSTALL_LIBDIR=lib
    BUILD_COMMAND ${CMAKE_COMMAND} --build <BINARY_DIR> --config Release --target verovio
    # Leipzig as TTF too: the SVG embeds it as WOFF2 for text glyphs (tempo, chord accidentals),
    # which Skia cannot load, so the app registers the TTF with its SVG renderer. SIL OFL 1.1.
    INSTALL_COMMAND ${CMAKE_COMMAND} --install <BINARY_DIR> --config Release
            COMMAND ${CMAKE_COMMAND} -E copy <SOURCE_DIR>/fonts/Leipzig/Leipzig.ttf ${CMAKE_BINARY_DIR}/verovio/share/verovio/Leipzig.ttf)
endif()
