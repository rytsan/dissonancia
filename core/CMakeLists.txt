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

add_library(dz_miniaudio STATIC third_party/miniaudio.c)
target_include_directories(dz_miniaudio PUBLIC third_party)
target_link_libraries(dz_miniaudio PUBLIC Threads::Threads ${CMAKE_DL_LIBS})
if(UNIX AND NOT APPLE)
  target_link_libraries(dz_miniaudio PUBLIC m)
endif()

add_library(dz_core STATIC src/engine.cpp src/rt.cpp src/voice.cpp src/cqt.cpp src/chords.cpp src/notes.cpp src/bass.cpp src/tempo.cpp src/player.cpp src/post.cpp src/decode.cpp src/beats.cpp src/timing.cpp src/intonation.cpp src/session.cpp src/dynamics.cpp src/harmony.cpp src/form.cpp src/choir.cpp src/playground.cpp)
target_include_directories(dz_core PUBLIC include src)
target_link_libraries(dz_core PUBLIC dz_miniaudio)
if(WIN32)
  target_link_libraries(dz_core PUBLIC avrt)
endif()
if(MSVC)
  target_compile_options(dz_core PRIVATE /W4)
else()
  target_compile_options(dz_core PRIVATE -Wall -Wextra)
endif()

# LIVE playground MIDI input (optional): RtMidi (MIT-style), its pinned release compiled in, on the
# platform's own MIDI API (ALSA on Linux, WinMM on Windows, CoreMIDI on macOS).
option(DZ_WITH_MIDI "MIDI input for the LIVE playground (RtMidi)" ON)
if(DZ_WITH_MIDI AND UNIX AND NOT APPLE)
  find_package(ALSA)
  if(NOT ALSA_FOUND)
    message(WARNING "ALSA development files not found: building without MIDI input")
    set(DZ_WITH_MIDI OFF)
  endif()
endif()
if(DZ_WITH_MIDI)
  include(FetchContent)
  FetchContent_Declare(rtmidi
    URL https://github.com/thestk/rtmidi/archive/refs/tags/6.0.0.tar.gz
    URL_HASH SHA256=ef7bcda27fee6936b651c29ebe9544c74959d0b1583b716ce80a1c6fea7617f0
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
  FetchContent_GetProperties(rtmidi)
  if(NOT rtmidi_POPULATED)
    FetchContent_Populate(rtmidi)
  endif()
  add_library(dz_rtmidi STATIC ${rtmidi_SOURCE_DIR}/RtMidi.cpp)
  target_include_directories(dz_rtmidi SYSTEM PUBLIC ${rtmidi_SOURCE_DIR})
  target_compile_options(dz_rtmidi PRIVATE -w)
  if(WIN32)
    target_compile_definitions(dz_rtmidi PUBLIC __WINDOWS_MM__)
    target_link_libraries(dz_rtmidi PUBLIC winmm)
  elseif(APPLE)
    target_compile_definitions(dz_rtmidi PUBLIC __MACOSX_CORE__)
    target_link_libraries(dz_rtmidi PUBLIC "-framework CoreMIDI" "-framework CoreAudio" "-framework CoreFoundation")
  else()
    target_compile_definitions(dz_rtmidi PUBLIC __LINUX_ALSA__)
    target_link_libraries(dz_rtmidi PUBLIC ALSA::ALSA Threads::Threads)
  endif()
  target_link_libraries(dz_core PUBLIC dz_rtmidi)
  target_compile_definitions(dz_core PUBLIC DZ_MIDI=1)
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
  add_executable(dz_tests tests/test_core.cpp tests/test_voice.cpp tests/test_cqt.cpp tests/test_chords.cpp tests/test_tempo.cpp tests/test_player.cpp tests/test_post.cpp tests/test_decode.cpp tests/test_mix.cpp tests/test_beats.cpp tests/test_timing.cpp tests/test_intonation.cpp tests/test_dynamics.cpp tests/test_harmony.cpp tests/test_form.cpp tests/test_choir.cpp tests/test_playground.cpp tests/rt_trap.cpp)
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
  target_link_libraries(dz_separate PRIVATE demucs_lib dz_miniaudio)
endif()

# Phase 3 AI feedback (R4.5): llama.cpp (MIT) at a pinned release, built into the dz_coach helper
# the app runs as its own process. No model is fetched: the user points to a GGUF of their choice.
# The machine decides at run time: ggml's backends are plugins beside dz_coach (GGML_BACKEND_DL), the
# CPU in every instruction-set variant (the best one for the CPU is loaded), CUDA when this machine
# has the toolkit, Vulkan (AMD, Intel, NVIDIA) when it has the SDK; a GPU that is not there is skipped.
option(DZ_WITH_COACH "Build the AI feedback helper (dz_coach)" ON)
if(DZ_WITH_COACH)
  include(CheckLanguage)
  # nvcc where the toolkit installs it, when it is not on the PATH.
  find_program(DZ_NVCC nvcc HINTS ENV CUDA_PATH PATH_SUFFIXES bin PATHS /usr/local/cuda/bin /opt/cuda/bin)
  if(DZ_NVCC AND NOT CMAKE_CUDA_COMPILER)
    set(CMAKE_CUDA_COMPILER ${DZ_NVCC})
  endif()
  check_language(CUDA)
  # Kernels for the GPUs this machine has (compiled ahead): with only the generic list, the driver
  # compiles them on the first run (~8 min here, a 2080 Ti, then cached). No GPU seen: ggml's list.
  if(CMAKE_CUDA_COMPILER AND NOT DEFINED CMAKE_CUDA_ARCHITECTURES)
    execute_process(COMMAND nvidia-smi --query-gpu=compute_cap --format=csv,noheader OUTPUT_VARIABLE _caps RESULT_VARIABLE _rc ERROR_QUIET)
    if(_rc EQUAL 0 AND _caps)
      string(REPLACE "." "" _caps "${_caps}")
      string(REGEX MATCHALL "[0-9]+" _caps "${_caps}")
      list(REMOVE_DUPLICATES _caps)
      set(CMAKE_CUDA_ARCHITECTURES ${_caps} CACHE STRING "this machine's GPUs (nvidia-smi)")
      message(STATUS "dz_coach CUDA kernels for compute capability ${_caps}")
    endif()
  endif()
  find_package(Vulkan COMPONENTS glslc QUIET)
  set(_cuda OFF)
  if(CMAKE_CUDA_COMPILER)
    set(_cuda ON)
  endif()
  option(DZ_COACH_CUDA "dz_coach's CUDA backend (on when nvcc is found)" ${_cuda})
  option(DZ_COACH_VULKAN "dz_coach's Vulkan backend (on when the Vulkan SDK is found)" ${Vulkan_FOUND})
  message(STATUS "dz_coach backends: CPU (all variants), CUDA ${DZ_COACH_CUDA}, Vulkan ${DZ_COACH_VULKAN}")
  include(FetchContent)
  FetchContent_Declare(llama_cpp
    URL https://github.com/ggml-org/llama.cpp/archive/refs/tags/b11235.tar.gz
    URL_HASH SHA256=3a9982fad75cc4c8172e6bcd23a48bd5ca7b846ce848f78837e38dba6297d07e
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
  set(LLAMA_BUILD_COMMON OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_TESTS OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_TOOLS OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_SERVER OFF CACHE BOOL "" FORCE)
  set(LLAMA_CURL OFF CACHE BOOL "" FORCE)
  set(GGML_NATIVE OFF CACHE BOOL "" FORCE)   # portable: the helper runs on the user's machine
  set(GGML_BACKEND_DL ON CACHE BOOL "" FORCE)
  set(GGML_CPU_ALL_VARIANTS ON CACHE BOOL "" FORCE)
  set(GGML_CUDA ${DZ_COACH_CUDA} CACHE BOOL "" FORCE)
  set(GGML_VULKAN ${DZ_COACH_VULKAN} CACHE BOOL "" FORCE)
  # Shared libraries for llama.cpp only; it writes them and the backend plugins to bin/, where
  # dz_coach goes too.
  set(BUILD_SHARED_LIBS ON)
  FetchContent_GetProperties(llama_cpp)
  if(NOT llama_cpp_POPULATED)
    FetchContent_Populate(llama_cpp)
    add_subdirectory(${llama_cpp_SOURCE_DIR} ${llama_cpp_BINARY_DIR} EXCLUDE_FROM_ALL)
  endif()
  unset(BUILD_SHARED_LIBS)
  # The JSON-to-facts contract, the prompt and the checker (no model needed: tested on their own).
  add_library(dz_coach_lib STATIC src/coach.cpp)
  target_include_directories(dz_coach_lib PUBLIC src SYSTEM PUBLIC ${llama_cpp_SOURCE_DIR}/vendor)
  target_compile_features(dz_coach_lib PUBLIC cxx_std_20)
  add_executable(dz_coach tools/dz_coach.cpp)
  target_link_libraries(dz_coach PRIVATE dz_coach_lib llama ggml)
  set_target_properties(dz_coach PROPERTIES BUILD_RPATH "$ORIGIN" INSTALL_RPATH "$ORIGIN" RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
  add_dependencies(dz_coach ggml)   # the backend plugins are ggml's dependencies
  target_compile_definitions(dz_coach PRIVATE DZ_COACH_DIR="${CMAKE_CURRENT_SOURCE_DIR}/coach")
  if(DZ_TESTS)
    add_executable(dz_coach_tests tests/test_coach.cpp)
    target_link_libraries(dz_coach_tests PRIVATE dz_coach_lib Catch2::Catch2WithMain)
    target_compile_definitions(dz_coach_tests PRIVATE DZ_COACH_DIR="${CMAKE_CURRENT_SOURCE_DIR}/coach")
    add_test(NAME dz_coach_tests COMMAND dz_coach_tests)
  endif()
endif()

# STUDIO S8 VST3 inserts (optional component, spec §20.2): the dz_plugin host helper the app runs
# as its own process, and a test plugin. The VST3 SDK (MIT since 3.8.0) as its four pinned repos.
option(DZ_WITH_VST3 "Build the VST3 host helper (dz_plugin) for STUDIO" ON)
if(DZ_WITH_VST3)
  include(FetchContent)
  set(DZ_VST3_SDK ${CMAKE_BINARY_DIR}/vst3sdk)
  foreach(part
      "base;vst3_base;3d2e82f8e6bff59c1d8b7a27491a29c2286b5206;8f1d5f9ac0cd1e916ca3a196f5ee080ec4301b0b10a7ee7e564013f5d58b03cd"
      "pluginterfaces;vst3_pluginterfaces;31d6eeba6daaa3e2a8bfbe3e7a90ca0b7fbfbc1c;7c9d19af0e81711edde34c3eb2e5e6d150ae1f449501bb5b352605b9590654d7"
      "public.sdk;vst3_public_sdk;a3911a4615dabbfdfd9d181ee26b05c70c289a95;4cc8a9a57a970172b1efbef62025ae4f0656e132eed994f0e15ed19dfd9b0bb3"
      "cmake;vst3_cmake;de6e54eeaaab35b7145f5c32c279b5e892146e04;ab4ce274563570fed11f2f0a31feb93280cd26543703f90c404a8720c1a486a5")
    list(GET part 0 dir)
    list(GET part 1 repo)
    list(GET part 2 commit)
    list(GET part 3 hash)
    string(REPLACE "." "_" name "vst3_${dir}")
    FetchContent_Declare(${name}
      URL https://github.com/steinbergmedia/${repo}/archive/${commit}.tar.gz
      URL_HASH SHA256=${hash}
      SOURCE_DIR ${DZ_VST3_SDK}/${dir}
      DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
    FetchContent_GetProperties(${name})
    if(NOT ${name}_POPULATED)
      FetchContent_Populate(${name})
    endif()
  endforeach()
  set(SDK_ROOT ${DZ_VST3_SDK})
  add_subdirectory(vst3)
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
