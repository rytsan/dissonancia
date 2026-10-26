#include <catch2/catch_test_macros.hpp>
#include <cmath>
#include <vector>

#include "form.hpp"

using namespace dz;

namespace {
uint16_t triad(int root, bool minor) { return uint16_t((1u << root % 12) | (1u << (root + (minor ? 3 : 4)) % 12) | (1u << (root + 7) % 12)); }
}  // namespace

TEST_CASE("form: AABA in 4-bar phrases, a repeat changed and faster") {
    // A: C G Am F, one bar each; B: Dm Dm G G; the third A ends on G instead of F; 4/4.
    const std::vector<uint16_t> A = {triad(0, false), triad(7, false), triad(9, true), triad(5, false)},
                                A2 = {triad(0, false), triad(7, false), triad(9, true), triad(7, false)},
                                B = {triad(2, true), triad(2, true), triad(7, false), triad(7, false)};
    std::vector<uint16_t> masks;
    for (const auto* phrase : {&A, &A, &B, &A2})
        for (uint16_t bar : *phrase)
            for (int beat = 0; beat < 4; beat++) masks.push_back(bar);
    std::vector<double> grid, heard;
    for (size_t k = 0; k <= masks.size(); k++) grid.push_back(0.5 * double(k));
    for (double t = 0, beat = 0.5; t < grid.back(); t += beat) {   // 120 BPM, the last phrase at 126
        heard.push_back(t);
        if (t >= 24) beat = 60.0 / 126;
    }
    const FormReport r = form_report(masks, 4, grid, heard);
    INFO(r.form << " " << r.phraseBars << " match " << r.repeatMatchPct << " tempo " << r.repeatTempoPct);
    CHECK(r.form == "AABA");
    CHECK(r.phraseBars == 4);
    CHECK(r.repeats == 2);
    CHECK(r.sections[1].matchPct == 100);
    CHECK(std::abs(r.sections[3].matchPct - 75) < 1);   // F -> G: one bar of four shares no tone
    CHECK(std::abs(r.sections[3].bpm - 126) < 1);
    CHECK(std::abs(r.repeatTempoPct - 2.5f) < 0.5);   // (0 + 5) / 2
}

TEST_CASE("form: a progression played twice through 8 bars, and nothing repeated") {
    std::vector<uint16_t> masks;
    for (int pass = 0; pass < 2; pass++)
        for (int bar = 0; bar < 8; bar++)
            for (int beat = 0; beat < 3; beat++) masks.push_back(triad(bar * 5, bar % 2));   // 3/4, eight different bars
    std::vector<double> grid;
    for (size_t k = 0; k <= masks.size(); k++) grid.push_back(0.5 * double(k));
    const FormReport r = form_report(masks, 3, grid, {});
    CHECK(r.form == "AA");
    CHECK(r.phraseBars == 8);
    CHECK(std::isnan(r.repeatTempoPct));
    std::vector<uint16_t> through(masks.begin(), masks.begin() + 24);   // then the same a tritone away: no bar again
    for (int bar = 0; bar < 8; bar++)
        for (int beat = 0; beat < 3; beat++) through.push_back(triad(bar * 5 + 6, bar % 2));
    CHECK(form_report(through, 3, grid, {}).repeats == 0);
}
