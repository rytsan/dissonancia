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

add_library(dz_core STATIC src/engine.cpp src/rt.cpp src/voice.cpp src/cqt.cpp src/chords.cpp src/bass.cpp src/tempo.cpp src/player.cpp src/post.cpp src/decode.cpp)
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

# Offline chord run over a WAV (same front end and tracker as the engine), for real recordings.
add_executable(dz_wav tools/dz_wav.cpp)
target_link_libraries(dz_wav PRIVATE dz_core)

if(DZ_TESTS)
  include(FetchContent)
  FetchContent_Declare(Catch2 GIT_REPOSITORY https://github.com/catchorg/Catch2.git GIT_TAG v3.7.1 GIT_SHALLOW TRUE)
  FetchContent_MakeAvailable(Catch2)
  add_executable(dz_tests tests/test_core.cpp tests/test_voice.cpp tests/test_cqt.cpp tests/test_chords.cpp tests/test_tempo.cpp tests/test_player.cpp tests/test_post.cpp tests/test_decode.cpp tests/rt_trap.cpp)
  target_link_libraries(dz_tests PRIVATE dz_core Catch2::Catch2WithMain)
  enable_testing()
  add_test(NAME dz_tests COMMAND dz_tests)
endif()

# STUDIO S3 separation (optional component, docs/studio-plan.md): demucs.cpp (MIT) + Eigen (MPL-2.0,
# header-only), pinned tarballs, built into the dz_separate helper the app runs as its own process.
# Demucs weights are not built: the app downloads them on demand (checksum-pinned).
option(DZ_WITH_DEMUCS "Build the Demucs separation helper (dz_separate) for STUDIO" ON)
if(DZ_WITH_DEMUCS)
  include(FetchContent)
  FetchContent_Declare(demucs_cpp
    URL https://github.com/sevagh/demucs.cpp/archive/f1206e9adeea103aef4a636b9e62297cf1f8e34e.tar.gz
    URL_HASH SHA256=f08bcacb49f99fdedac8affe13d671ec82cf3639c096b92c9c3da08c4b952934
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
  # Eigen at demucs.cpp's submodule commit; gitlab.com refuses scripted downloads, the GitHub mirror
  # serves the same tree (the hash pins the content).
  FetchContent_Declare(eigen_src
    URL https://github.com/eigen-mirror/eigen/archive/dd8c71e62852b2fe429edb6682ac91fd1c578a26.tar.gz
    URL_HASH SHA256=e8cbc003b1fa5e0d0d142c58155aaf341f87783d213d31a605a19b2f13d27acc
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
  FetchContent_GetProperties(demucs_cpp)
  if(NOT demucs_cpp_POPULATED)
    FetchContent_Populate(demucs_cpp)
  endif()
  FetchContent_GetProperties(eigen_src)
  if(NOT eigen_src_POPULATED)
    FetchContent_Populate(eigen_src)
  endif()
  find_package(OpenMP REQUIRED)
  file(GLOB DEMUCS_SOURCES ${demucs_cpp_SOURCE_DIR}/src/*.cpp)
  # Portable build (no -march=native: the helper runs on the user's machine). AVX2 + FMA measured
  # only 17 % faster here (50 s vs 60 s for 8.7 s of audio), not worth a second binary.
  add_library(demucs_lib STATIC ${DEMUCS_SOURCES})
  target_include_directories(demucs_lib SYSTEM PUBLIC ${eigen_src_SOURCE_DIR} ${demucs_cpp_SOURCE_DIR}/src ${demucs_cpp_SOURCE_DIR}/cli-apps)
  target_compile_features(demucs_lib PUBLIC cxx_std_17)
  target_compile_options(demucs_lib PRIVATE -O3 -fno-math-errno -w)
  target_compile_definitions(demucs_lib PUBLIC NDEBUG)
  target_link_libraries(demucs_lib PUBLIC OpenMP::OpenMP_CXX)
  add_executable(dz_separate tools/dz_separate.cpp)
  target_compile_options(dz_separate PRIVATE -O3 -w)
  target_link_libraries(dz_separate PRIVATE demucs_lib miniaudio)
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
