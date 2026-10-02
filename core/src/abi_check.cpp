// Compiled with -Wpadded -Werror: any implicit padding in an ABI struct fails the build (§22).
#include "dissonancia.h"

static_assert(sizeof(SessionConfig) == 28);
static_assert(sizeof(AudioDeviceConfig) == 16);
static_assert(sizeof(OnsetEvent) == 16);
static_assert(sizeof(MusicalNoteEvent) == 56);
static_assert(sizeof(ChordEvent) == 88 && sizeof(CadenceEvent) == 88);
static_assert(sizeof(AnalyzerEvent) == 96 && offsetof(AnalyzerEvent, data) == 8);
static_assert(sizeof(PitchEstimate) == 32);
static_assert(sizeof(NoteEstimate) == 36);
static_assert(sizeof(TuningEstimate) == 16 && sizeof(ChromaVector) == 208);
static_assert(sizeof(ChordCandidate) == 108 && sizeof(ChordRecognitionResult) == 500);
static_assert(sizeof(BassEstimate) == 32);
static_assert(sizeof(ContextEstimate) == 16);
static_assert(sizeof(PlayerInfo) == 24 && sizeof(PostStatus) == 8 && sizeof(EditSegment) == 24);
static_assert(sizeof(LiveSnapshot) == 18016 && offsetof(LiveSnapshot, chord) == 1048 && offsetof(LiveSnapshot, bass) == 1576);
static_assert(offsetof(LiveSnapshot, beatInBar) == 96 && offsetof(LiveSnapshot, pitch) == 104);
static_assert(offsetof(LiveSnapshot, note) == 136 && offsetof(LiveSnapshot, chroma) == 200);
static_assert(offsetof(LiveSnapshot, cqtMagnitude) == 408 && offsetof(LiveSnapshot, waveMin) == 1632);
