// Phase 3, R4.7: how a base model is held to the facts. The session JSON (dissonancia-session/1)
// becomes short Portuguese fact lines (the JSON-to-text contract, versioned below); the prompt is a
// document of worked examples, then this take's facts, then the start of the report, which the model
// continues under a grammar; the report it writes is checked: every number must be one of the facts'.
// No model here: dz_coach runs it.
#pragma once
#include <string>
#include <vector>

namespace dz::coach {

// The contract's version: the fact lines' wording, the prompt's layout and the grammar.
constexpr const char* kContract = "dissonancia-coach/1";

struct Facts {
    std::vector<std::string> lines;   // "Timing: média +12 ms atrás do metrônomo, ..."
    bool lowConfidence = false;       // a metric the report must hedge
    double bpm = 0;                   // the take's metronome (exercises may slow it down)
};

// Throws std::exception on a JSON that is not a session with metrics.
Facts facts(const std::string& sessionJson);

struct Example {
    Facts facts;
    std::string report;   // written by us, in the grammar's sections
};

// The document the model continues: a header, the examples, this take's facts, "Relatório:\n".
std::string prompt(const std::vector<Example>& examples, const Facts& take);

// The report's sections (llama.cpp GBNF), from right after "Relatório:\n" to its end.
extern const char* const kGrammar;

struct Checked {
    std::string report;                 // the items that passed, in their sections
    int items = 0, dropped = 0;
    std::vector<std::string> droppedItems;
    bool ok = false;                    // every section kept an item and at most a third was dropped
};

// Every number in an item must be a fact's (a bar, a value); an exercise may also name a slower
// tempo ("a 70 bpm") or a count ("4 vezes", "5 minutos"). Items that fail are dropped.
Checked check(const std::string& report, const Facts& f);

}  // namespace dz::coach
