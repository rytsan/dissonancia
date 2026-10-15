// Platforms without an editor window yet (macOS: a Cocoa window is still to be written).
#include "editor_window.hpp"

bool run_editor_window(Steinberg::IPlugView*, const std::string&, std::string& error) {
    error = "plugin editors are not available on this platform yet";
    return false;
}
