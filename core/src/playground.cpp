#include "playground.hpp"

#include <cmath>
#include <cstring>

#include "miniaudio.h"

namespace dz {

namespace {
float db(float d) { return std::pow(10.f, d / 20.f); }
int write_wav(const char* path, const std::vector<float>& x, uint32_t channels, uint32_t rate) {
    ma_encoder enc;
    ma_encoder_config c = ma_encoder_config_init(ma_encoding_format_wav, ma_format_f32, channels, rate);
    if (ma_encoder_init_file(path, &c, &enc) != MA_SUCCESS) return ANA_ERR_IO;
    ma_encoder_write_pcm_frames(&enc, x.data(), x.size() / channels, nullptr);
    ma_encoder_uninit(&enc);
    return ANA_OK;
}
std::vector<float> decode(const char* path, uint32_t channels, uint32_t rate) {
    std::vector<float> x;
    ma_decoder dec;
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, channels, rate);
    if (ma_decoder_init_file(path, &cfg, &dec) != MA_SUCCESS) return x;
    float buf[8192];
    for (ma_uint64 got = 0; ma_decoder_read_pcm_frames(&dec, buf, 8192 / channels, &got) == MA_SUCCESS && got > 0;) x.insert(x.end(), buf, buf + got * channels);
    ma_decoder_uninit(&dec);
    return x;
}
}  // namespace

Playground::Playground() = default;

Playground::~Playground() {
    for (auto& s : slot_) delete s.exchange(nullptr);
    for (auto& r : retired_) delete r.first;
    delete song_.exchange(nullptr);
    for (auto& r : retiredSongs_) delete r.first;
    for (auto& [id, l] : loops_) delete l;
    for (auto& r : retiredLoops_) delete r.first;
    delete board_.exchange(nullptr);
    for (auto& r : retiredBoards_) delete r.first;
    for (auto& [id, p] : pedals_) delete p;
    for (auto& r : retiredPedals_) delete r.first;
}

void Playground::start(uint32_t rate, uint32_t outLatencyFrames, uint32_t roundTripFrames) {
    std::lock_guard lock(apiMutex_);
    if (rate != rate_) {   // decoded at another rate: empty (the app loads them again)
        for (int k = 0; k <= kSampler; k++) { delete slot_[k].exchange(nullptr); seconds_[k] = 0; }
        for (auto& [id, l] : loops_) delete l;
        loops_.clear();
        delete board_.exchange(nullptr);   // delay lines sized for the old rate: the app sends the board again
        for (auto& [id, p] : pedals_) delete p;
        pedals_.clear();
    }
    for (auto& r : retired_) delete r.first;
    retired_.clear();
    rate_ = rate;
    outLatency_ = outLatencyFrames;
    roundTrip_ = roundTripFrames;
    live_ = Deck{};
    std::fill(std::begin(live_.noteStep), std::end(live_.noteStep), -1);
    cfgSeen_ = ~0u;
    midiCount_ = 0;
    recordWant_ = false;
    std::string err;
    publish(err);
    running_ = true;
}

void Playground::stop_engine() { running_ = false; }

void Playground::retire_free() {   // apiMutex_ held
    const uint64_t cb = callbacks_.load(std::memory_order_acquire), adopted = adopted_.load(std::memory_order_acquire);
    const bool idle = !running_.load();
    std::erase_if(retired_, [&](auto& r) {
        if (!idle && cb < r.second + 2) return false;
        delete r.first;
        return true;
    });
    std::erase_if(retiredSongs_, [&](auto& r) {   // r.second: the generation that replaced it
        if (!idle && adopted < r.second) return false;
        delete r.first;
        return true;
    });
    std::erase_if(retiredLoops_, [&](auto& r) {
        if (!idle && adopted < r.second) return false;
        delete r.first;
        return true;
    });
    const uint64_t boardAt = boardAdopted_.load(std::memory_order_acquire);
    std::erase_if(retiredBoards_, [&](auto& r) {
        if (!idle && boardAt < r.second) return false;
        delete r.first;
        return true;
    });
    std::erase_if(retiredPedals_, [&](auto& r) {
        if (!idle && boardAt < r.second) return false;
        delete r.first;
        return true;
    });
}

int Playground::load(int slot, const char* path, uint32_t rate, std::string& error) {
    if (slot < 0 || slot > kSampler) { error = "no such pad"; return ANA_ERR_ARG; }
    std::unique_ptr<Sample> s;
    if (path && *path) {
        s = std::make_unique<Sample>();
        s->lr = decode(path, 2, rate);
        const size_t cap = size_t(rate) * 60 * 2;   // a minute at most: a pad, not a song
        if (s->lr.size() > cap) s->lr.resize(cap);
        s->frames = s->lr.size() / 2;
        if (s->frames == 0) { error = std::string("cannot decode ") + path; return ANA_ERR_IO; }
    }
    std::lock_guard lock(apiMutex_);
    seconds_[slot] = s ? float(double(s->frames) / rate) : 0.f;
    Sample* old = slot_[slot].exchange(s.release(), std::memory_order_acq_rel);
    // A callback holds a sample for at most the callback it reads it in, and the next one drops the
    // voices on it: two callbacks later it is free (at once while no callback runs).
    if (old) retired_.push_back({old, callbacks_.load(std::memory_order_acquire)});
    retire_free();
    return ANA_OK;
}

void Playground::set(const PlaygroundConfig& c) {
    cfg_.seq.fetch_add(1, std::memory_order_acq_rel);
    std::atomic_thread_fence(std::memory_order_release);
    cfg_.c = c;
    std::atomic_thread_fence(std::memory_order_release);
    cfg_.seq.fetch_add(1, std::memory_order_acq_rel);
}

void Playground::status(PlaygroundStatus& out) const {
    out = {};
    for (int k = 0; k <= kSampler; k++) { out.hits[k] = hits_[k].load(std::memory_order_relaxed); out.seconds[k] = seconds_[k]; }
    const float pk = const_cast<std::atomic<float>&>(peak_).exchange(0, std::memory_order_relaxed);
    out.peakDb = pk > 0 ? 20 * std::log10(pk) : -120.f;
    out.voices = voices_.load(std::memory_order_relaxed);
    out.midiMessages = midiCount_.load(std::memory_order_relaxed);
    out.entry = entry_.load(std::memory_order_relaxed);
    out.repeat = repeat_.load(std::memory_order_relaxed);
    out.bar = bar_.load(std::memory_order_relaxed);
    out.step = step_.load(std::memory_order_relaxed);
    out.queued = queued_.load(std::memory_order_relaxed);
    const uint8_t f = flags_.load(std::memory_order_relaxed);
    out.playing = f & 1; out.recording = f >> 1 & 1; out.looping = f >> 2 & 1; out.starting = f >> 3 & 1;
    auto take = [](const std::atomic<float>& a) { const float v = const_cast<std::atomic<float>&>(a).exchange(0, std::memory_order_relaxed); return v > 0 ? 20 * std::log10(v) : -120.f; };
    out.inputDb = take(inPeak_);
    out.outputDb = take(outPeak_);
    for (int k = 0; k < ANA_PEDALS; k++) out.pedalDb[k] = take(pedalPeak_[k]);
}

// ---------------------------------------------------------------- the song

int Playground::song(const PgBlock* blocks, int nb, const uint8_t* steps, int ns, const PgNote* notes, int nn, const PgEntry* order, int no, std::string& error) {
    if (nb < 0 || ns < 0 || nn < 0 || no < 0 || nb > 256 || no > 1024) { error = "song too large"; return ANA_ERR_ARG; }
    for (int b = 0; b < nb; b++) {
        const PgBlock& x = blocks[b];
        if (x.bars < 1 || x.bars > 64 || uint64_t(x.stepOffset) + uint64_t(ANA_PADS) * x.bars * ANA_PG_STEPS_PER_BAR > uint64_t(ns) ||
            uint64_t(x.noteOffset) + x.noteCount > uint64_t(nn)) { error = "block out of range"; return ANA_ERR_ARG; }
    }
    for (int e = 0; e < no; e++) if (order[e].block < 0 || order[e].block >= nb || order[e].repeats < 1) { error = "timeline entry out of range"; return ANA_ERR_ARG; }
    std::lock_guard lock(apiMutex_);
    lastBlocks_.assign(blocks, blocks + nb);
    lastSteps_.assign(steps, steps + ns);
    lastNotes_.assign(notes, notes + nn);
    lastOrder_.assign(order, order + no);
    return publish(error);
}

int Playground::publish(std::string&) {   // apiMutex_ held
    auto s = std::make_unique<Song>();
    s->gen = ++gen_;
    for (const PgBlock& x : lastBlocks_) {
        Block b;
        b.id = x.id;
        b.bars = int(x.bars);
        b.steps.assign(lastSteps_.begin() + x.stepOffset, lastSteps_.begin() + x.stepOffset + size_t(ANA_PADS) * x.bars * ANA_PG_STEPS_PER_BAR);
        b.notes.assign(lastNotes_.begin() + x.noteOffset, lastNotes_.begin() + x.noteOffset + x.noteCount);
        if (auto it = loops_.find(x.id); it != loops_.end()) b.loop = it->second;
        s->blocks.push_back(std::move(b));
    }
    s->order = lastOrder_;
    Song* old = song_.exchange(s.release(), std::memory_order_acq_rel);
    if (old) retiredSongs_.push_back({old, gen_});
    retire_free();
    return ANA_OK;
}

int Playground::record(bool on, double barFrames) {
    std::lock_guard lock(apiMutex_);
    if (on) {   // a loop for every block, its bars long at this tempo (an old one at another length is replaced, its start kept)
        bool changed = false;
        for (const PgBlock& x : lastBlocks_) {
            const size_t want = size_t(std::llround(barFrames * x.bars));
            auto it = loops_.find(x.id);
            if (it != loops_.end() && it->second->mono.size() == want) continue;
            auto* l = new Loop;
            l->mono.assign(want, 0.f);
            if (it != loops_.end()) {
                std::copy_n(it->second->mono.begin(), std::min(want, it->second->mono.size()), l->mono.begin());
                retiredLoops_.push_back({it->second, gen_ + 1});
            }
            loops_[x.id] = l;
            changed = true;
        }
        if (changed) { std::string e; publish(e); }
    }
    recordWant_ = on;
    return ANA_OK;
}

int Playground::loop_file(uint32_t id, const char* path, bool save, uint32_t rate, std::string& error) {
    std::lock_guard lock(apiMutex_);
    auto it = loops_.find(id);
    if (save) {
        if (it == loops_.end()) { error = "no loop recorded in this block"; return ANA_ERR_STATE; }
        return write_wav(path, it->second->mono, 1, rate);
    }
    Loop* l = nullptr;
    if (path && *path) {
        l = new Loop;
        l->mono = decode(path, 1, rate);
        if (l->mono.empty()) { delete l; error = std::string("cannot decode ") + path; return ANA_ERR_IO; }
    }
    if (it != loops_.end()) { retiredLoops_.push_back({it->second, gen_ + 1}); loops_.erase(it); }
    if (l) loops_[id] = l;
    std::string e;
    return publish(e);
}

// ---------------------------------------------------------------- the pedalboard

int Playground::pedalboard(const Pedalboard& in, std::string& error) {
    if (in.pedals < 0 || in.pedals > ANA_PEDALS || in.cables < 0 || in.cables > ANA_PEDAL_CABLES) { error = "pedalboard out of range"; return ANA_ERR_ARG; }
    auto jackOut = [&](int j) { return j == 0 || j == 1 || (j >= 9 && j < 8 + 2 * in.pedals && (j - 8) % 2 == 1); };
    auto jackIn = [&](int j) { return j == 2 || j == 3 || (j >= 8 && j < 8 + 2 * in.pedals && (j - 8) % 2 == 0); };
    for (int c = 0; c < in.cables; c++)
        if (!jackOut(in.cable[c].from) || !jackIn(in.cable[c].to) || !std::isfinite(in.cable[c].gain)) { error = "a cable between jacks that are not there"; return ANA_ERR_ARG; }
    for (int k = 0; k < in.pedals; k++) if (uint8_t(in.pedal[k].kind) > uint8_t(PedalKind::Filter)) { error = "no such pedal"; return ANA_ERR_ARG; }
    std::lock_guard lock(apiMutex_);
    auto b = std::make_unique<Board>();
    b->p = in;
    b->gen = ++boardGen_;
    // The pedals' DSP by id: kept (with its state) while the pedal is there and of the same kind.
    std::map<uint16_t, Pedal*> keep;
    for (int k = 0; k < in.pedals; k++) {
        const PedalParams& pp = in.pedal[k];
        auto it = pedals_.find(pp.id);
        Pedal* d = it != pedals_.end() && it->second->kind == pp.kind ? it->second : new Pedal(pp.kind, rate_);
        keep[pp.id] = d;
        b->dsp[k] = d;
    }
    for (auto& [id, d] : pedals_) {
        bool used = false;
        for (auto& [kid, kd] : keep) used |= kd == d;
        if (!used) retiredPedals_.push_back({d, b->gen});
    }
    pedals_ = keep;
    // The order: Kahn over the pedals; a pedal on a loop is left out (silent).
    auto node = [](int jack) { return jack == 2 ? ANA_PEDALS : jack == 3 ? ANA_PEDALS + 1 : (jack - 8) / 2; };
    int indeg[ANA_PEDALS]{};
    for (int c = 0; c < in.cables; c++) if (in.cable[c].from >= 9 && in.cable[c].to >= 8) indeg[node(in.cable[c].to)]++;
    int queue[ANA_PEDALS], qh = 0, qt = 0;
    for (int k = 0; k < in.pedals; k++) if (indeg[k] == 0) queue[qt++] = k;
    while (qh < qt) {
        const int k = queue[qh++];
        b->order[b->norder++] = k;
        for (int c = 0; c < in.cables; c++)
            if (in.cable[c].from == 9 + 2 * k && in.cable[c].to >= 8 && --indeg[node(in.cable[c].to)] == 0) queue[qt++] = node(in.cable[c].to);
    }
    for (int c = 0; c < in.cables; c++) {
        b->inStart[node(in.cable[c].to) + 1]++;
        b->padsPatched |= in.cable[c].from == 1;
        b->recPatched |= in.cable[c].to == 3;
    }
    for (int n = 0; n < ANA_PEDALS + 2; n++) b->inStart[n + 1] += b->inStart[n];
    int fill[ANA_PEDALS + 2];
    std::copy(b->inStart, b->inStart + ANA_PEDALS + 2, fill);
    for (int c = 0; c < in.cables; c++) b->inList[fill[node(in.cable[c].to)]++] = c;
    Board* old = board_.exchange(b.release(), std::memory_order_acq_rel);
    if (old) retiredBoards_.push_back({old, boardGen_});
    retire_free();
    return ANA_OK;
}

void Playground::board_frame(const Board& b, Pedal* const* dsp, float in, const float* pads, float* out, float& rec, double beatFrames, bool live) {
    float val[ANA_PEDALS][2]{};
    auto src = [&](int jack, float& l, float& r) {
        if (jack == 0) l = r = in;
        else if (jack == 1) { l = pads[0]; r = pads[1]; }
        else { l = val[(jack - 9) / 2][0]; r = val[(jack - 9) / 2][1]; }
    };
    auto gather = [&](int n, float& L, float& R) {
        L = R = 0;
        for (int i = b.inStart[n]; i < b.inStart[n + 1]; i++) {
            const PedalCable& c = b.p.cable[b.inList[i]];
            float l, r;
            src(c.from, l, r);
            L += c.gain * l; R += c.gain * r;
        }
    };
    for (int i = 0; i < b.norder; i++) {
        const int k = b.order[i];
        float L, R;
        gather(k, L, R);
        if (b.p.pedal[k].on) dsp[k]->run(L, R, b.p.pedal[k].knob, beatFrames);
        val[k][0] = L; val[k][1] = R;
        if (live) { const float pk = std::max(std::fabs(L), std::fabs(R)); if (pk > pedalPeak_[k].load(std::memory_order_relaxed)) pedalPeak_[k].store(pk, std::memory_order_relaxed); }
    }
    float L, R;
    gather(ANA_PEDALS, L, R);
    out[0] += L; out[1] += R;
    if (!b.padsPatched) { out[0] += pads[0]; out[1] += pads[1]; }
    if (b.recPatched) { float a, c; gather(ANA_PEDALS + 1, a, c); rec = 0.5f * (a + c); }
    else rec = in;
    if (live) {
        if (std::fabs(in) > inPeak_.load(std::memory_order_relaxed)) inPeak_.store(std::fabs(in), std::memory_order_relaxed);
        const float o = std::max(std::fabs(L), std::fabs(R));
        if (o > outPeak_.load(std::memory_order_relaxed)) outPeak_.store(o, std::memory_order_relaxed);
    }
}

// ---------------------------------------------------------------- MIDI and voices

void Playground::push(uint8_t s, uint8_t d1, uint8_t d2) { fromApi_.try_push(uint32_t(s) | uint32_t(d1) << 8 | uint32_t(d2) << 16); }

void Playground::midi_from_device(uint8_t s, uint8_t d1, uint8_t d2) {
    const uint32_t m = uint32_t(s) | uint32_t(d1) << 8 | uint32_t(d2) << 16;
    fromDevice_.try_push(m);
    raw_.try_push(m);
    midiCount_.fetch_add(1, std::memory_order_relaxed);
}

void Playground::trigger(Deck& d, int slot, int note, float vel, int stepsLeft) {
    const Sample* s = slot_[slot].load(std::memory_order_acquire);
    if (!s) return;
    Deck::Voice* v = nullptr;
    for (auto& x : d.voice) if (!x.s) { v = &x; break; }
    if (!v) {   // all busy: the one nearest its end
        v = &d.voice[0];
        for (auto& x : d.voice) if (x.pos / double(x.s->frames) > v->pos / double(v->s->frames)) v = &x;
    }
    *v = {};
    v->s = s;
    v->slot = slot;
    v->note = note;
    v->stepsLeft = stepsLeft;
    v->rate = slot == kSampler ? std::pow(2.0, (note - int(cur_.samplerRoot)) / 12.0) : 1.0;
    v->gain = vel;
    if (&d == &live_) hits_[slot].fetch_add(1, std::memory_order_relaxed);
}

// A sixteenth starts at `at`: on a bar's first one the transport moves (a song adopted, a start, a
// queued entry, the next bar / pass / entry); then the block's pads and notes on this sixteenth.
void Playground::on_step(Deck& d, uint64_t at, int spb, double barFrames, bool live) {
    const int inBar = int(d.nextStep % uint64_t(spb));
    for (auto& v : d.voice) if (v.s && v.stepsLeft > 0 && --v.stepsLeft == 0) v.release = true;
    if (inBar == 0) {
        if (live) {
            const Song* s = song_.load(std::memory_order_acquire);
            if (s != d.song) { d.song = s; adopted_.store(s ? s->gen : 0, std::memory_order_release); }
        }
        const int entries = d.song ? int(d.song->order.size()) : 0;
        if (d.starting) {
            d.starting = false;
            d.playing = entries > 0;
            d.entry = std::clamp(d.queued, 0, std::max(0, entries - 1));
            d.looping = d.queuedLoop;
            d.queued = -1;
            d.repeat = d.bar = 0;
        } else if (d.playing) {
            if (d.entry >= entries) { d.entry = 0; d.repeat = d.bar = 0; }
            const int bars = d.song->blocks[size_t(d.song->order[size_t(d.entry)].block)].bars;
            if (d.queued >= 0 && d.queued < entries) {   // launched: on this bar
                d.entry = d.queued;
                d.looping = d.queuedLoop;
                d.queued = -1;
                d.repeat = d.bar = 0;
            } else if (++d.bar >= bars) {
                d.bar = 0;
                if (!d.looping && ++d.repeat >= d.song->order[size_t(d.entry)].repeats) {
                    d.repeat = 0;
                    if (++d.entry >= entries) {
                        if (d.once) { d.playing = false; d.ended = true; }
                        d.entry = 0;
                    }
                }
            }
        }
        if (d.playing && entries == 0) d.playing = false;
        d.barStart = at;
    }
    if (!d.playing) { d.stepInBar = -1; return; }
    d.stepInBar = inBar;
    const Block& b = d.song->blocks[size_t(d.song->order[size_t(d.entry)].block)];
    const int bar = std::min(d.bar, b.bars - 1), step = bar * ANA_PG_STEPS_PER_BAR + inBar;
    for (int pad = 0; pad < ANA_PADS; pad++)
        if (const uint8_t v = b.at(pad, step)) trigger(d, pad, -1, v / 127.f);
    for (const PgNote& n : b.notes)
        if (n.step == step) trigger(d, kSampler, n.note, n.velocity / 127.f, std::max<int>(1, n.length));
    (void)barFrames;
}

void Playground::render_deck(Deck& d, float* buf, uint32_t n, const float* in, uint32_t inCh, uint64_t p0, uint64_t origin, double beatFrames, int bpb,
                             bool live) {
    const int spb = std::clamp(bpb * 4, 1, ANA_PG_STEPS_PER_BAR);
    const double sf = beatFrames / 4, barFrames = beatFrames * bpb;
    uint32_t i = 0;
    const Board* board = live ? board_.load(std::memory_order_acquire) : bounceBoard_;
    auto run = [&](uint32_t upto) {
        const float rel = 1.f / (0.03f * float(rate_));
        std::fill(d.pad + 2 * i, d.pad + 2 * upto, 0.f);
        for (auto& v : d.voice) {
            if (!v.s) continue;
            const float gl = gain_[v.slot][0] * v.gain, gr = gain_[v.slot][1] * v.gain;
            for (uint32_t j = i; j < upto; j++) {
                const uint64_t k = uint64_t(v.pos);
                if (k + 1 >= v.s->frames) { v.s = nullptr; break; }
                const float f = float(v.pos - double(k)), *x = v.s->lr.data() + 2 * k;
                if (v.release && (v.env -= rel) <= 0) { v.s = nullptr; break; }
                d.pad[2 * j] += v.env * gl * (x[0] + f * (x[2] - x[0]));
                d.pad[2 * j + 1] += v.env * gr * (x[1] + f * (x[3] - x[1]));
                v.pos += v.rate;
            }
        }
        // The pedalboard: the input and the pads in, the output and what REC records out.
        float recSig[512];
        for (uint32_t j = i; j < upto; j++) {
            float x = 0;
            if (in) { for (uint32_t c = 0; c < inCh; c++) x += in[size_t(j) * inCh + c]; x /= float(inCh); }
            if (board) board_frame(*board, live ? board->dsp : d.dsp, x, d.pad + 2 * j, buf + 2 * j, recSig[j], beatFrames, live);
            else { buf[2 * j] += d.pad[2 * j]; buf[2 * j + 1] += d.pad[2 * j + 1]; recSig[j] = x; }
        }
        // The block's audio loop: played, and overdubbed while REC (what the input brings now was
        // played to what the output gave a round trip ago).
        if (d.playing && d.song && d.entry >= 0 && size_t(d.entry) < d.song->order.size()) {
            const Block& b = d.song->blocks[size_t(d.song->order[size_t(d.entry)].block)];
            if (Loop* l = b.loop; l && !l->mono.empty()) {
                const size_t L = l->mono.size(), perBar = L / size_t(b.bars);
                const bool rec = live && d.recording && in;
                for (uint32_t j = i; j < upto; j++) {
                    const size_t idx = size_t(std::min(d.bar, b.bars - 1)) * perBar + size_t(p0 + j - d.barStart);
                    if (idx < L) { buf[2 * j] += loopGain_ * l->mono[idx]; buf[2 * j + 1] += loopGain_ * l->mono[idx]; }
                    if (rec) l->mono[(idx + L - roundTrip_ % L) % L] += recSig[j];
                }
            }
        }
        i = upto;
    };
    if (sf > 0) {
        if (sf != d.stepFrames || double(origin) != d.stepOrigin) {
            d.stepFrames = sf;
            d.stepOrigin = double(origin);
            d.nextStep = p0 > origin ? uint64_t(std::ceil(double(p0 - origin) / sf)) : 0;
            d.barStart = origin + uint64_t(std::llround(double(d.nextStep / uint64_t(spb) * uint64_t(spb)) * sf));
        }
        for (;;) {
            const uint64_t at = origin + uint64_t(std::llround(double(d.nextStep) * sf));
            if (at >= p0 + n) break;
            if (at >= p0) {
                run(uint32_t(at - p0));
                on_step(d, at, spb, barFrames, live);
            }
            d.nextStep++;
        }
    }
    run(n);
}

void Playground::render(float* out, uint32_t frames, uint32_t ch, const float* in, uint32_t inCh, uint64_t pos, uint64_t origin, double beatFrames,
                        int beatsPerBar) {
    // Settings, then the voices on a sample that was replaced (it is about to be freed).
    if (const uint32_t a = cfg_.seq.load(std::memory_order_acquire); a != cfgSeen_ && !(a & 1)) {
        PlaygroundConfig c = cfg_.c;
        std::atomic_thread_fence(std::memory_order_acquire);
        if (cfg_.seq.load(std::memory_order_acquire) == a) {
            cur_ = c;
            cfgSeen_ = a;
            for (int k = 0; k < ANA_PADS; k++) {
                const double p = (std::clamp(c.padPan[k], -1.f, 1.f) + 1) * 3.14159265358979 / 4, g = db(c.padGainDb[k]) * 1.41421356;
                gain_[k][0] = float(g * std::cos(p)); gain_[k][1] = float(g * std::sin(p));
            }
            gain_[kSampler][0] = gain_[kSampler][1] = db(c.samplerGainDb);
            loopGain_ = db(c.loopGainDb);
            master_ = db(c.masterDb);
        }
    }
    Deck& d = live_;
    if (const Board* b = board_.load(std::memory_order_acquire)) boardAdopted_.store(b->gen, std::memory_order_release);
    for (auto& v : d.voice) if (v.s && v.s != slot_[v.slot].load(std::memory_order_acquire)) v.s = nullptr;

    for (Cmd c; cmd_.pop_many(&c, 1);) {
        if (c.kind == 0) {
            d.playing = d.starting = false;
            d.queued = -1;
            for (auto& v : d.voice) if (v.s && v.stepsLeft > 0) v.release = true;
        } else if (d.playing) { d.queued = std::max(0, c.entry); d.queuedLoop = c.loop; }
        else { d.starting = true; d.queued = std::max(0, c.entry); d.queuedLoop = c.loop; }
    }
    d.recording = recordWant_.load(std::memory_order_relaxed) && d.playing;

    // Pads and notes; while REC also into the block playing, at the sixteenth nearest to when the
    // player heard the grid (the output's latency earlier).
    const double sf = beatFrames / 4;
    const int spb = std::clamp(beatsPerBar * 4, 1, ANA_PG_STEPS_PER_BAR);
    auto here = [&](int64_t& abs) {
        const double t = double(pos) - outLatency_ - double(origin);
        abs = std::llround(t / sf);
        const Block& b = d.song->blocks[size_t(d.song->order[size_t(d.entry)].block)];
        int64_t k = d.bar * spb + std::llround((double(pos) - outLatency_ - double(d.barStart)) / sf), total = int64_t(b.bars) * spb;
        k = ((k % total) + total) % total;
        return std::pair{&b, uint16_t(k / spb * ANA_PG_STEPS_PER_BAR + k % spb)};
    };
    const bool rec = d.recording && d.song && d.entry >= 0 && size_t(d.entry) < d.song->order.size() && sf > 0;
    uint32_t msg[64];
    for (auto* q : {&fromApi_, &fromDevice_})
        for (size_t n = q->pop_many(msg, 64), i = 0; i < n; i++) {
            const uint8_t s = uint8_t(msg[i]), d1 = uint8_t(msg[i] >> 8) & 0x7F, d2 = uint8_t(msg[i] >> 16) & 0x7F;
            const bool pad = d1 >= 36 && d1 < 36 + ANA_PADS;
            if ((s & 0xF0) == 0x90 && d2 > 0) {
                if (pad) trigger(d, d1 - 36, -1, d2 / 127.f); else trigger(d, kSampler, d1, d2 / 127.f);
                if (rec) {
                    int64_t abs;
                    auto [b, step] = here(abs);
                    if (pad) rec_.try_push({b->id, step, 0, 0, uint8_t(d1 - 36), d2, 0});
                    else { d.noteStep[d1] = step; d.noteAbs[d1] = abs; d.noteVel[d1] = d2; d.noteBlock[d1] = b->id; }
                }
            } else if ((s & 0xF0) == 0x80 || (s & 0xF0) == 0x90) {
                for (auto& v : d.voice) if (v.s && v.slot == kSampler && v.note == d1 && v.stepsLeft < 0) v.release = true;
                if (rec && !pad && d.noteStep[d1] >= 0) {
                    int64_t abs;
                    here(abs);
                    rec_.try_push({d.noteBlock[d1], uint16_t(d.noteStep[d1]), uint16_t(std::clamp<int64_t>(abs - d.noteAbs[d1], 1, 256)), 1, d1, d.noteVel[d1], 0});
                    d.noteStep[d1] = -1;
                }
            }
        }

    float buf[2 * 512];
    for (uint32_t done = 0; done < frames;) {
        const uint32_t n = std::min<uint32_t>(512, frames - done);
        std::memset(buf, 0, sizeof(float) * 2 * n);
        render_deck(d, buf, n, in ? in + size_t(done) * inCh : nullptr, inCh, pos + done, origin, beatFrames, beatsPerBar, true);
        float pk = 0;
        for (uint32_t j = 0; j < n; j++) {
            const float l = buf[2 * j] * master_, r = buf[2 * j + 1] * master_;
            pk = std::max(pk, std::max(std::fabs(l), std::fabs(r)));
            if (ch >= 2) { out[(done + j) * ch] += l; out[(done + j) * ch + 1] += r; }
            else if (ch == 1) out[done + j] += 0.5f * (l + r);
        }
        if (pk > peak_.load(std::memory_order_relaxed)) peak_.store(pk, std::memory_order_relaxed);
        done += n;
    }
    int active = 0;
    for (auto& v : d.voice) active += v.s != nullptr;
    voices_.store(active, std::memory_order_relaxed);
    entry_.store(d.playing ? d.entry : -1, std::memory_order_relaxed);
    repeat_.store(d.repeat, std::memory_order_relaxed);
    bar_.store(d.bar, std::memory_order_relaxed);
    step_.store(d.playing ? d.stepInBar : -1, std::memory_order_relaxed);
    queued_.store(d.starting || d.playing ? d.queued : -1, std::memory_order_relaxed);
    flags_.store(uint8_t(d.playing | d.recording << 1 | d.looping << 2 | d.starting << 3), std::memory_order_relaxed);
    callbacks_.fetch_add(1, std::memory_order_acq_rel);
}

// ---------------------------------------------------------------- bounce

int Playground::bounce(const char* path, uint32_t rate, double beatFrames, int beatsPerBar, std::string& error) {
    std::lock_guard lock(apiMutex_);
    const Song* s = song_.load(std::memory_order_acquire);
    if (!s || s->order.empty()) { error = "the timeline is empty"; return ANA_ERR_STATE; }
    if (beatFrames <= 0) { error = "no tempo"; return ANA_ERR_STATE; }
    if (cfgSeen_ == ~0u) {   // no callback has read the settings yet: as they are now
        const PlaygroundConfig c = cfg_.c;
        for (int k = 0; k < ANA_PADS; k++) {
            const double p = (std::clamp(c.padPan[k], -1.f, 1.f) + 1) * 3.14159265358979 / 4, g = db(c.padGainDb[k]) * 1.41421356;
            gain_[k][0] = float(g * std::cos(p)); gain_[k][1] = float(g * std::sin(p));
        }
        gain_[kSampler][0] = gain_[kSampler][1] = db(c.samplerGainDb);
        loopGain_ = db(c.loopGainDb);
        master_ = db(c.masterDb);
        cur_ = c;
    }
    double bars = 0;
    for (const PgEntry& e : s->order) bars += double(s->blocks[size_t(e.block)].bars) * e.repeats;
    const uint64_t total = uint64_t(bars * beatFrames * beatsPerBar) + uint64_t(rate) * 2;   // and 2 s of tails
    auto d = std::make_unique<Deck>();
    std::fill(std::begin(d->noteStep), std::end(d->noteStep), -1);
    d->song = s;
    d->once = d->starting = true;
    d->queued = 0;
    // The pads through the board as live (the input silent), on pedals of the bounce's own.
    bounceBoard_ = board_.load(std::memory_order_acquire);
    std::vector<std::unique_ptr<Pedal>> own;
    if (bounceBoard_)
        for (int k = 0; k < bounceBoard_->p.pedals; k++) { own.push_back(std::make_unique<Pedal>(bounceBoard_->p.pedal[k].kind, rate)); d->dsp[k] = own.back().get(); }
    std::vector<float> y(size_t(total) * 2, 0.f);
    for (uint64_t p = 0; p < total; p += 512) {
        const uint32_t n = uint32_t(std::min<uint64_t>(512, total - p));
        render_deck(*d, y.data() + p * 2, n, nullptr, 0, p, 0, beatFrames, beatsPerBar, false);
    }
    bounceBoard_ = nullptr;
    for (float& v : y) v *= master_;
    return write_wav(path, y, 2, rate);
}

}  // namespace dz
