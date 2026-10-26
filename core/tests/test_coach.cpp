#include <catch2/catch_test_macros.hpp>
#include <fstream>
#include <sstream>
#include <string>

#include "coach.hpp"

using namespace dz::coach;

namespace {
std::string slurp(const std::string& p) {
    std::ifstream f(p);
    std::stringstream s;
    s << f.rdbuf();
    return s.str();
}
bool any(const Facts& f, const std::string& start) {
    for (const std::string& l : f.lines)
        if (l.rfind(start, 0) == 0) return true;
    return false;
}
}  // namespace

TEST_CASE("coach: a guitar take's facts") {
    const Facts f = facts(slurp(DZ_COACH_DIR "/examples/1-guitar.json"));
    std::string all;
    for (const std::string& l : f.lines) all += l + "\n";
    INFO(all);
    CHECK(any(f, "[neutro] Timing: média +13 ms (atrás do metrônomo), desvio 20 ms, 128 ataques; confiança alta."));
    CHECK(any(f, "[neutro] Tempo: 109 bpm tocados (metrônomo 108), deriva de +2,2 bpm"));
    CHECK(any(f, "[bom] Trocas de acorde: 20, em média +5 ms do tempo"));
    CHECK(any(f, "[neutro] Forma: ABAC em frases de 4 compassos"));
    CHECK_FALSE(f.lowConfidence);
    CHECK(f.bpm == 108);
}

TEST_CASE("coach: the check drops an item with a number the facts do not hold") {
    Facts f;
    f.lines = {"[neutro] Timing: média +13 ms (atrás do metrônomo), desvio 20 ms, 128 ataques; confiança alta.", "[trabalhar] Timing no compasso 5: +35 ms."};
    f.bpm = 108;
    const Checked c = check("O que foi bem:\n- Desvio de 20 ms, regular.\nPara trabalhar:\n- Compasso 5 atrasa 35 ms.\n- Compasso 7 atrasa 40 ms.\n"
                            "Exercícios:\n- Toque o compasso 5 em loop a 80 bpm, 4 vezes.\n- Toque tudo a 120 bpm.\n",
                            f);
    INFO(c.report);
    CHECK(c.items == 5);
    CHECK(c.dropped == 2);   // bar 7 and 40 ms are invented; 120 bpm is faster than the take
    CHECK(c.report.find("Compasso 7") == std::string::npos);
    CHECK(c.report.find("80 bpm") != std::string::npos);
    CHECK_FALSE(c.ok);       // two of five dropped: more than a third
    CHECK(check("O que foi bem:\n- Média de +13 ms.\nPara trabalhar:\n- O compasso 5: +35 ms.\nExercícios:\n- Loop do compasso 5 a 90 bpm.\n", f).ok);
    // No comparison with an earlier take; low confidence must be said.
    const Checked earlier = check("O que foi bem:\n- O timing melhorou: média de +13 ms.\n- Média de +13 ms.\nPara trabalhar:\n- O compasso 5: +35 ms.\n"
                                  "Exercícios:\n- Loop do compasso 5.\n", f);
    CHECK(earlier.dropped == 1);
    f.lowConfidence = true;
    CHECK_FALSE(check("O que foi bem:\n- Média de +13 ms.\nPara trabalhar:\n- O compasso 5: +35 ms.\nExercícios:\n- Loop do compasso 5.\n", f).ok);
    CHECK(check("O que foi bem:\n- Média de +13 ms.\nPara trabalhar:\n- O compasso 5: +35 ms.\nExercícios:\n- Loop do compasso 5.\n"
                "Confiança:\n- O timing tem confiança baixa.\n", f).ok);
    CHECK(grammar(f).find("Confiança:") != std::string::npos);
    // A4 or Cadd9 is a name: its digit does not make "4 feedbacks" a known number.
    f.lines.push_back("[bom] Afinação de referência: a sua está +9 cents do A4 = 440 Hz.");
    CHECK(check("O que foi bem:\n- +9 cents do A4 = 4 feedbacks.\n", f).dropped == 1);
    // A fact to work on is not something that went well; an item is not said twice.
    f.lines.push_back("[trabalhar] Timing no compasso 8: -38 ms.");
    const Checked tags = check("O que foi bem:\n- O compasso 8 corre: -38 ms.\n- +9 cents do A4.\n- A sua afinação: +9 cents.\n", f);
    CHECK(tags.dropped == 2);
}

TEST_CASE("coach: the prompt is the examples, then this take's facts, then the report's start") {
    Facts a, b;
    a.lines = {"Timing: média +5 ms."};
    b.lines = {"Timing: média -8 ms."};
    const std::string p = prompt({{a, "O que foi bem:\n- x\n"}}, b);
    CHECK(p.find("### Take 1\nFatos:\n- Timing: média +5 ms.\nRelatório:\nO que foi bem:\n- x\n") != std::string::npos);
    const std::string tail = "### Take 2\nFatos:\n- Timing: média -8 ms.\nRelatório:\n";
    CHECK(p.ends_with(tail));
}

TEST_CASE("coach: the worked examples pass the check against their own facts (the contract holds)") {
    for (const char* name : {"1-guitar", "2-voice", "3-short-clipped"}) {
        const std::string base = std::string(DZ_COACH_DIR "/examples/") + name;
        const Checked c = check(slurp(base + ".txt"), facts(slurp(base + ".json")));
        INFO(name << ": " << c.dropped << " dropped" << (c.droppedItems.empty() ? "" : ": " + c.droppedItems[0]));
        CHECK(c.dropped == 0);
        CHECK(c.ok);
    }
    CHECK(facts(slurp(DZ_COACH_DIR "/examples/3-short-clipped.json")).lowConfidence);
}
