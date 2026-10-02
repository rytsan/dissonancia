// STUDIO offline analysis (studio-plan S1): a whole file through the mode's pipeline at the
// session's quality on a worker thread, with progress and cancel; the result is written as a take
// JSON (same schema as the REC sidecar) so SCORE reads it like any take. POST layer: allocates.
#pragma once
#include <atomic>
#include <string>
#include <thread>
#include <vector>

#include "dissonancia.h"

namespace dz {

class PostJob {
public:
    ~PostJob() { cancel(); join(); }
    // compensationSeconds: subtracted from event times (a REC take's round-trip latency, else 0).
    // metronomeGrid: the file starts on a downbeat of the session's tempo and meter (a REC take).
    // beats: track the beats (beats.hpp) instead of transcribing.
    int start(const SessionConfig& s, double compensationSeconds, const char* inPath, const char* outJsonPath, bool metronomeGrid, bool beats = false);
    void cancel() { cancel_.store(true); }
    void status(PostStatus& out) const;
    std::string error() const;

    // Synchronous body (tests, tools): returns ANA_OK, or an error / ANA_ERR_STATE when cancelled.
    int run(const SessionConfig& s, double compensationSeconds, const std::string& in, const std::string& out, bool metronomeGrid = false, bool beats = false);

private:
    void join() { if (thread_.joinable()) thread_.join(); }

    std::thread thread_;
    std::atomic<bool> cancel_{false};
    std::atomic<float> progress_{0};
    std::atomic<uint8_t> state_{uint8_t(PostState::Idle)};
    mutable std::atomic_flag errorLock_ = ATOMIC_FLAG_INIT;
    std::string error_;
    void set_error(const std::string& e);
};

}  // namespace dz
