// X11 editor window with the run loop VST3 plugins ask their frame for on Linux (file
// descriptors and timers, Steinberg::Linux::IRunLoop).
#include "editor_window.hpp"

#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <poll.h>

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <chrono>
#include <vector>

using namespace Steinberg;
using Clock = std::chrono::steady_clock;

namespace {
// Static objects owned by the helper's single editor: reference counting is a no-op.
#define DZ_STATIC_FUNKNOWN                                     \
    uint32 PLUGIN_API addRef() override { return 1000; }      \
    uint32 PLUGIN_API release() override { return 1000; }

struct RunLoop final : Linux::IRunLoop {
    struct Fd { Linux::IEventHandler* h; int fd; };
    struct Timer { Linux::ITimerHandler* h; std::chrono::milliseconds every; Clock::time_point next; };
    std::vector<Fd> fds;
    std::vector<Timer> timers;

    tresult PLUGIN_API registerEventHandler(Linux::IEventHandler* h, Linux::FileDescriptor fd) override { fds.push_back({h, fd}); return kResultOk; }
    tresult PLUGIN_API unregisterEventHandler(Linux::IEventHandler* h) override {
        fds.erase(std::remove_if(fds.begin(), fds.end(), [&](auto& f) { return f.h == h; }), fds.end());
        return kResultOk;
    }
    tresult PLUGIN_API registerTimer(Linux::ITimerHandler* h, Linux::TimerInterval ms) override {
        const auto every = std::chrono::milliseconds(std::max<uint64>(ms, 1));
        timers.push_back({h, every, Clock::now() + every});
        return kResultOk;
    }
    tresult PLUGIN_API unregisterTimer(Linux::ITimerHandler* h) override {
        timers.erase(std::remove_if(timers.begin(), timers.end(), [&](auto& t) { return t.h == h; }), timers.end());
        return kResultOk;
    }
    tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override {
        if (FUnknownPrivate::iidEqual(iid, Linux::IRunLoop::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid)) { *obj = this; return kResultOk; }
        *obj = nullptr;
        return kNoInterface;
    }
    DZ_STATIC_FUNKNOWN
};

struct Frame final : IPlugFrame {
    Display* display = nullptr;
    Window window = 0;
    RunLoop* loop = nullptr;
    tresult PLUGIN_API resizeView(IPlugView* view, ViewRect* r) override {
        XResizeWindow(display, window, unsigned(std::max(1, r->getWidth())), unsigned(std::max(1, r->getHeight())));
        view->onSize(r);
        return kResultOk;
    }
    tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override {
        if (FUnknownPrivate::iidEqual(iid, IPlugFrame::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid)) { *obj = this; return kResultOk; }
        if (FUnknownPrivate::iidEqual(iid, Linux::IRunLoop::iid)) { *obj = loop; return kResultOk; }
        *obj = nullptr;
        return kNoInterface;
    }
    DZ_STATIC_FUNKNOWN
};
}  // namespace

// Xlib's default handler ends the process on any protocol error; a plugin's request that the
// server refuses (an atom a bare server without a window manager lacks) is logged instead.
int log_x_error(Display* d, XErrorEvent* e) {
    char text[128];
    XGetErrorText(d, e->error_code, text, sizeof text);
    std::fprintf(stderr, "X error ignored: %s (request %d)\n", text, int(e->request_code));
    return 0;
}

bool run_editor_window(IPlugView* view, const std::string& title, std::string& error) {
    if (view->isPlatformTypeSupported(kPlatformTypeX11EmbedWindowID) != kResultTrue) { error = "the editor does not support X11"; return false; }
    XSetErrorHandler(log_x_error);
    Display* d = XOpenDisplay(nullptr);
    if (!d) { error = "no X display (DISPLAY)"; return false; }
    ViewRect r{0, 0, 640, 400};
    view->getSize(&r);
    Window w = XCreateSimpleWindow(d, DefaultRootWindow(d), 0, 0, unsigned(std::max(100, r.getWidth())), unsigned(std::max(60, r.getHeight())), 0, 0, 0);
    XStoreName(d, w, title.c_str());
    Atom del = XInternAtom(d, "WM_DELETE_WINDOW", False);
    XSetWMProtocols(d, w, &del, 1);
    XSelectInput(d, w, StructureNotifyMask);
    RunLoop loop;
    Frame frame;
    frame.display = d;
    frame.window = w;
    frame.loop = &loop;
    view->setFrame(&frame);
    XMapWindow(d, w);
    XSync(d, False);
    if (view->attached(reinterpret_cast<void*>(w), kPlatformTypeX11EmbedWindowID) != kResultOk) {
        view->setFrame(nullptr);
        XDestroyWindow(d, w);
        XCloseDisplay(d);
        error = "the editor did not attach to its window";
        return false;
    }
    // Tests close the window themselves after a while (no user to do it).
    const char* autoclose = std::getenv("DZ_EDITOR_AUTOCLOSE_MS");
    const auto closeAt = Clock::now() + std::chrono::milliseconds(autoclose ? std::atoi(autoclose) : 0);
    for (bool open = true; open;) {
        if (autoclose && Clock::now() >= closeAt) break;
        while (XPending(d)) {
            XEvent e;
            XNextEvent(d, &e);
            if (e.type == ClientMessage && Atom(e.xclient.data.l[0]) == del) open = false;
        }
        if (!open) break;
        // Wait for X, a plugin's descriptor or the next timer.
        std::vector<pollfd> p{{ConnectionNumber(d), POLLIN, 0}};
        for (auto& f : loop.fds) p.push_back({f.fd, POLLIN, 0});
        int timeout = 100;
        const auto now = Clock::now();
        for (auto& t : loop.timers)
            timeout = std::min<int>(timeout, int(std::max<long long>(0, std::chrono::duration_cast<std::chrono::milliseconds>(t.next - now).count())));
        poll(p.data(), nfds_t(p.size()), timeout);
        const auto handlers = loop.fds;   // a handler may unregister itself
        for (size_t i = 1; i < p.size() && i - 1 < handlers.size(); i++)
            if (p[i].revents & POLLIN) handlers[i - 1].h->onFDIsSet(handlers[i - 1].fd);
        const auto timers = loop.timers;
        for (auto& t : timers)
            if (Clock::now() >= t.next) {
                for (auto& live : loop.timers) if (live.h == t.h) live.next = Clock::now() + live.every;
                t.h->onTimer();
            }
    }
    view->removed();
    view->setFrame(nullptr);
    XDestroyWindow(d, w);
    XCloseDisplay(d);
    return true;
}
