// Real-time helpers (spec §3): denormals, thread priority, allocation-trap scope.
#pragma once

namespace dz {

// FTZ/DAZ (x86 MXCSR) or FZ (ARM FPCR) for the calling thread.
void disable_denormals();

// MMCSS "Pro Audio" (Windows), SCHED_FIFO (Linux), QoS USER_INTERACTIVE (macOS).
// Returns false when the OS refused (e.g. no RT permission on Linux).
bool promote_thread();

// Marks the calling thread as inside a real-time section. The test binary overrides
// global operator new to abort while this is set (fallback trap, §3).
bool in_rt_scope();
struct RtScope {
    RtScope();
    ~RtScope();
    RtScope(const RtScope&) = delete;
    RtScope& operator=(const RtScope&) = delete;
};

double now_seconds();   // monotonic, process epoch

}  // namespace dz
