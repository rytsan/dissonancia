// STUDIO S8: the plugin's editor in a native window of the host helper (never embedded in the
// app: spec §20.2). One implementation per platform: X11 (Linux), Win32; elsewhere a stub.
#pragma once
#include "pluginterfaces/gui/iplugview.h"

#include <string>

// Opens `view` in a window titled `title` and runs its event loop until the user closes it.
// False (and why) when no window could be opened.
bool run_editor_window(Steinberg::IPlugView* view, const std::string& title, std::string& error);
