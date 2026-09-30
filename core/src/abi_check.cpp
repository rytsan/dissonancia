// Compiled with -Wpadded -Werror: any implicit padding in an ABI struct fails the build (§22).
#include "dissonancia.h"

static_assert(sizeof(SessionConfig) == 28);
static_assert(sizeof(AudioDeviceConfig) == 16);
static_assert(sizeof(OnsetEvent) == 16);
static_assert(sizeof(MusicalNoteEvent) == 56);
static_assert(sizeof(ChordEvent) == 72);
static_assert(sizeof(AnalyzerEvent) == 80 && offsetof(AnalyzerEvent, data) == 8);
static_assert(sizeof(LiveSnapshot) == 16488);
static_assert(offsetof(LiveSnapshot, beatInBar) == 96 && offsetof(LiveSnapshot, waveMin) == 100);
