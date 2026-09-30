#include "rt.hpp"

#include <chrono>

#if defined(__x86_64__) || defined(_M_X64) || defined(__i386__)
#  include <immintrin.h>
#endif
#if defined(_WIN32)
#  include <windows.h>
#  include <avrt.h>
#elif defined(__APPLE__)
#  include <pthread.h>
#else
#  include <pthread.h>
#  include <sched.h>
#endif

namespace dz {

namespace { thread_local bool t_rt = false; }

bool in_rt_scope() { return t_rt; }
RtScope::RtScope() { t_rt = true; }
RtScope::~RtScope() { t_rt = false; }

void disable_denormals() {
#if defined(__x86_64__) || defined(_M_X64) || defined(__i386__)
    _mm_setcsr(_mm_getcsr() | 0x8040);   // FTZ (bit 15) | DAZ (bit 6)
#elif defined(__aarch64__)
    uint64_t fpcr;
    __asm__ volatile("mrs %0, fpcr" : "=r"(fpcr));
    __asm__ volatile("msr fpcr, %0" ::"r"(fpcr | (1ull << 24)));   // FZ
#endif
}

bool promote_thread() {
#if defined(_WIN32)
    DWORD task = 0;
    return AvSetMmThreadCharacteristicsW(L"Pro Audio", &task) != nullptr;
#elif defined(__APPLE__)
    return pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0) == 0;
#else
    // ponytail: direct SCHED_FIFO only; needs rtprio limits. rtkit (D-Bus) when desktop users hit EPERM.
    sched_param p{};
    p.sched_priority = 70;
    return pthread_setschedparam(pthread_self(), SCHED_FIFO, &p) == 0;
#endif
}

double now_seconds() {
    static const auto epoch = std::chrono::steady_clock::now();
    return std::chrono::duration<double>(std::chrono::steady_clock::now() - epoch).count();
}

}  // namespace dz
