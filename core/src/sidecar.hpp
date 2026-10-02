// The take JSON's event list, shared by the REC sidecar (engine) and the STUDIO offline result
// (post): complete notes, chords (relabelled on their whole duration) and cadences.
#pragma once
#include <cstdio>
#include <vector>

#include "dissonancia.h"

namespace dz {

// t0: subtracted from every event time (the first downbeat of a REC take, 0 for a file).
inline void write_events_json(FILE* f, const std::vector<AnalyzerEvent>& events, double t0) {
    bool first = true;
    for (const AnalyzerEvent& e : events) {
        const char* sep = first ? "" : ",";
        if (e.type == AnalyzerEventType::ChordEnded) {
            const ChordEvent& c = e.data.chord;
            std::fprintf(f, "%s\n    {\"type\": \"chord\", \"seq\": %u, \"start\": %.4f, \"end\": %.4f, \"symbol\": \"%s\", \"roman\": \"%s\","
                            " \"diatonicStatus\": %d, \"root\": %d, \"bass\": %d, \"inversion\": %d, \"quality\": %d, \"confidence\": %.3f,"
                            " \"incomplete\": %s, \"bassSettled\": %s}",
                         sep, e.sequence, c.startTimeSeconds - t0, c.endTimeSeconds - t0, c.symbol, c.roman, int(c.diatonicStatus),
                         c.rootPitchClass, c.bassPitchClass, c.inversion, int(c.quality), c.confidence, c.incomplete ? "true" : "false",
                         c.bassSettled ? "true" : "false");
        } else if (e.type == AnalyzerEventType::Cadence) {
            const CadenceEvent& c = e.data.cadence;
            std::fprintf(f, "%s\n    {\"type\": \"cadence\", \"seq\": %u, \"time\": %.4f, \"cadence\": %d, \"from\": \"%s\", \"to\": \"%s\","
                            " \"confidence\": %.3f, \"evidence\": \"%s\"}",
                         sep, e.sequence, c.timestampSeconds - t0, int(c.type), c.fromRoman, c.toRoman, c.confidence, c.evidence);
        } else if (e.type == AnalyzerEventType::NoteEnd) {
            const MusicalNoteEvent& n = e.data.note;
            std::fprintf(f,
                         "%s\n    {\"type\": \"note\", \"seq\": %u, \"start\": %.4f, \"end\": %.4f, \"midi\": %d, \"name\": \"%s\","
                         " \"avgHz\": %.2f, \"medianHz\": %.2f, \"avgCents\": %.1f, \"confidence\": %.3f, \"chromatic\": %s, \"vibrato\": %s}",
                         sep, e.sequence, n.startTimeSeconds - t0, n.endTimeSeconds - t0, n.midi, n.writtenName, n.avgHz, n.medianHz,
                         n.avgCents, n.confidence, n.chromatic ? "true" : "false", n.vibrato ? "true" : "false");
        } else {
            continue;
        }
        first = false;
    }
}

}  // namespace dz
