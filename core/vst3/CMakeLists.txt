# STUDIO S8 (spec §20.2): the VST3 host helper and its test plugin, on the VST3 SDK (MIT since
# 3.8.0). SDK_ROOT holds the four pinned SDK repos (base, pluginterfaces, public.sdk, cmake), laid
# out as in the vst3sdk super repo. Only the SDK's libraries are built: no VSTGUI, no examples.
project(dz_vst3 VERSION 1.0.0 LANGUAGES C CXX)   # the SDK macros read PROJECT_VERSION
list(APPEND CMAKE_MODULE_PATH "${SDK_ROOT}/cmake/modules")
set(SMTG_ENABLE_VSTGUI_SUPPORT OFF)
set(SMTG_CREATE_PLUGIN_LINK OFF CACHE BOOL "" FORCE)        # never install into the user's VST3 folder
set(SMTG_RUN_VST_VALIDATOR OFF CACHE BOOL "" FORCE)
set(SMTG_CREATE_MODULE_INFO OFF CACHE BOOL "" FORCE)
include(SMTG_VST3_SDK)
smtg_setup_platform_toolset()
smtg_setup_symbol_visibility()
set(public_sdk_SOURCE_DIR ${SDK_ROOT}/public.sdk)
set(pluginterfaces_SOURCE_DIR ${SDK_ROOT}/pluginterfaces)
include_directories(${SDK_ROOT})
smtg_create_lib_base_target()
smtg_create_pluginterfaces_target()
smtg_create_public_sdk_common_target()
smtg_create_public_sdk_target()
smtg_create_public_sdk_hosting_target()
foreach(t base pluginterfaces sdk_common sdk sdk_hosting)
  target_compile_options(${t} PRIVATE -w)
endforeach()

# The SDK leaves the platform half of module loading to the host.
if(WIN32)
  set(DZ_VST3_MODULE ${SDK_ROOT}/public.sdk/source/vst/hosting/module_win32.cpp)
elseif(APPLE)
  set(DZ_VST3_MODULE ${SDK_ROOT}/public.sdk/source/vst/hosting/module_mac.mm)
else()
  set(DZ_VST3_MODULE ${SDK_ROOT}/public.sdk/source/vst/hosting/module_linux.cpp)
endif()
add_executable(dz_plugin dz_plugin.cpp ${DZ_VST3_MODULE})
target_link_libraries(dz_plugin PRIVATE sdk_hosting)
if(APPLE)
  target_link_libraries(dz_plugin PRIVATE "-framework CoreFoundation" "-framework Foundation")
endif()
set_target_properties(dz_plugin PROPERTIES RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR})   # next to dz_separate

# The test plugin lands in <build>/VST3/<config>/dz_test_gain.vst3.
smtg_add_vst3plugin(dz_test_gain dz_test_gain.cpp ${SDK_ROOT}/public.sdk/source/vst/vstsinglecomponenteffect.cpp)
target_link_libraries(dz_test_gain PRIVATE sdk)
