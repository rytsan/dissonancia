// Fallback allocation trap (spec §3): any operator new inside an RtScope aborts the test binary.
#include <cstdio>
#include <cstdlib>
#include <new>

#include "rt.hpp"

static void* checked_alloc(std::size_t n) {
    if (dz::in_rt_scope()) {
        std::fputs("FATAL: heap allocation inside a real-time scope\n", stderr);
        std::abort();
    }
    if (void* p = std::malloc(n ? n : 1)) return p;
    throw std::bad_alloc();
}

void* operator new(std::size_t n) { return checked_alloc(n); }
void* operator new[](std::size_t n) { return checked_alloc(n); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, std::size_t) noexcept { std::free(p); }
void operator delete[](void* p, std::size_t) noexcept { std::free(p); }
