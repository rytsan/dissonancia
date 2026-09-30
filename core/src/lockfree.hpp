// Lock-free triple buffer (lossy state) and SPSC queue (lossless events), spec §3.
// Both preallocate at construction; the steady-state path never allocates.
#pragma once
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>

namespace dz {

// One writer, one reader. The writer fills write_slot() COMPLETELY, then publish().
template <class T>
class TripleBuffer {
public:
    TripleBuffer() : slots_(std::make_unique<T[]>(3)) {}

    T& write_slot() { return slots_[back_]; }
    void publish() { back_ = middle_.exchange(back_ | kDirty, std::memory_order_acq_rel) & kIndex; }

    // Returns the newest published slot (or the previous one when nothing new).
    const T& read() {
        if (middle_.load(std::memory_order_relaxed) & kDirty)
            front_ = middle_.exchange(front_, std::memory_order_acq_rel) & kIndex;
        return slots_[front_];
    }

private:
    static constexpr uint32_t kDirty = 4, kIndex = 3;
    std::unique_ptr<T[]> slots_;
    std::atomic<uint32_t> middle_{1};
    uint32_t back_ = 0, front_ = 2;
};

// Capacity N must be a power of two. try_push never blocks and never allocates.
template <class T, size_t N>
class SpscQueue {
    static_assert((N & (N - 1)) == 0, "capacity must be a power of two");

public:
    SpscQueue() : buf_(std::make_unique<T[]>(N)) {}

    bool try_push(const T& v) {
        size_t h = head_.load(std::memory_order_relaxed);
        if (h - tail_.load(std::memory_order_acquire) == N) return false;
        buf_[h & (N - 1)] = v;
        head_.store(h + 1, std::memory_order_release);
        return true;
    }

    size_t pop_many(T* out, size_t cap) {
        size_t t = tail_.load(std::memory_order_relaxed);
        size_t n = head_.load(std::memory_order_acquire) - t;
        if (n > cap) n = cap;
        for (size_t i = 0; i < n; i++) out[i] = buf_[(t + i) & (N - 1)];
        tail_.store(t + n, std::memory_order_release);
        return n;
    }

private:
    std::unique_ptr<T[]> buf_;
    alignas(64) std::atomic<size_t> head_{0};
    alignas(64) std::atomic<size_t> tail_{0};
};

}  // namespace dz
