// Win32 editor window: a plain top-level window sized to the view, a message loop until it is closed.
#include "editor_window.hpp"

#include <windows.h>

#include <algorithm>

using namespace Steinberg;

namespace {
LRESULT CALLBACK proc(HWND w, UINT msg, WPARAM wp, LPARAM lp) {
    if (msg == WM_CLOSE) { DestroyWindow(w); return 0; }
    if (msg == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(w, msg, wp, lp);
}

constexpr DWORD kStyle = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;

void client_size(HWND w, int width, int height) {
    RECT rc{0, 0, std::max(1, width), std::max(1, height)};
    AdjustWindowRect(&rc, kStyle, FALSE);
    SetWindowPos(w, nullptr, 0, 0, rc.right - rc.left, rc.bottom - rc.top, SWP_NOMOVE | SWP_NOZORDER);
}

struct Frame final : IPlugFrame {
    HWND window = nullptr;
    tresult PLUGIN_API resizeView(IPlugView* view, ViewRect* r) override {
        client_size(window, r->getWidth(), r->getHeight());
        view->onSize(r);
        return kResultOk;
    }
    tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override {
        if (FUnknownPrivate::iidEqual(iid, IPlugFrame::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid)) { *obj = this; return kResultOk; }
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1000; }
    uint32 PLUGIN_API release() override { return 1000; }
};
}  // namespace

bool run_editor_window(IPlugView* view, const std::string& title, std::string& error) {
    if (view->isPlatformTypeSupported(kPlatformTypeHWND) != kResultTrue) { error = "the editor does not support HWND"; return false; }
    WNDCLASSW wc{};
    wc.lpfnWndProc = proc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"DissonanciaPluginEditor";
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    RegisterClassW(&wc);
    std::wstring wtitle(title.begin(), title.end());   // plugin names: ASCII in practice
    HWND w = CreateWindowW(wc.lpszClassName, wtitle.c_str(), kStyle, CW_USEDEFAULT, CW_USEDEFAULT, 640, 400, nullptr, nullptr, wc.hInstance, nullptr);
    if (!w) { error = "cannot create the editor window"; return false; }
    ViewRect r{0, 0, 640, 400};
    view->getSize(&r);
    client_size(w, r.getWidth(), r.getHeight());
    Frame frame;
    frame.window = w;
    view->setFrame(&frame);
    if (view->attached(w, kPlatformTypeHWND) != kResultOk) { view->setFrame(nullptr); DestroyWindow(w); error = "the editor did not attach to its window"; return false; }
    ShowWindow(w, SW_SHOW);
    MSG m;
    while (GetMessageW(&m, nullptr, 0, 0) > 0) { TranslateMessage(&m); DispatchMessageW(&m); }
    view->removed();
    view->setFrame(nullptr);
    return true;
}
