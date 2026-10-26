#include "coach.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <map>
#include <regex>
#include <set>

#include <nlohmann/json.hpp>

namespace dz::coach {

using nlohmann::json;

namespace {

// A number as the facts write it: decimal comma, an explicit sign when asked.
std::string num(double v, int decimals = 0, bool sign = false) {
    char buf[32];
    std::snprintf(buf, sizeof buf, sign ? "%+.*f" : "%.*f", decimals, v);
    std::string s = buf;
    std::replace(s.begin(), s.end(), '.', ',');
    if (s == "+0" || s == "-0") s = "0";
    return s;
}
bool has(const json& o, const char* k) { return o.contains(k) && o[k].is_number(); }
double get(const json& o, const char* k, double d = 0) { return has(o, k) ? o[k].get<double>() : d; }
const char* level(double c) { return c >= 0.8 ? "alta" : c >= 0.5 ? "média" : "baixa"; }
std::string late(double ms) { return std::fabs(ms) < 3 ? "em cima" : ms > 0 ? "atrás" : "à frente"; }

// The core's limits (English, for any reader) in the report's language.
std::string warning(const std::string& w) {
    if (w.rfind("clipping", 0) == 0) return "o sinal clipou (amostras no máximo): o volume de entrada estava alto demais";
    if (w.rfind("very quiet", 0) == 0) return "entrada muito baixa: ataques podem ter sido perdidos";
    if (w.rfind("frames dropped", 0) == 0) return "o gravador perdeu amostras: tempos depois da falha podem estar errados";
    if (w.rfind("timing:", 0) == 0) return "poucos ataques no grid: o timing tem confiança baixa";
    if (w.rfind("intonation:", 0) == 0) return "poucas notas lidas: a afinação tem confiança baixa";
    if (w.rfind("harmony:", 0) == 0) return "poucas trocas de acorde no grid: as trocas têm confiança baixa";
    return w;
}

}  // namespace

Facts facts(const std::string& text) {
    const json root = json::parse(text);
    const json& m = root.at("metrics");
    Facts f;
    auto add = [&](std::string s) { f.lines.push_back(std::move(s)); };
    f.bpm = root.contains("session") ? get(root["session"], "bpm") : 0;

    if (m.contains("timing") && get(m["timing"], "matched") > 0) {
        const json& t = m["timing"];
        const bool metro = t.value("grid", "") == "metronome";
        const double mean = get(t, "meanOffsetMs"), conf = get(t, "confidence");
        f.lowConfidence |= conf < 0.5;
        // Against the player's own beats (the click off) the mean says nothing: only the spread.
        if (metro)
            add("Timing: média " + num(mean, 0, true) + " ms (" + late(mean) + " do metrônomo), desvio " + num(get(t, "spreadMs")) + " ms, " +
                num(get(t, "matched")) + " ataques; confiança " + level(conf) + ".");
        else
            add("Timing (metrônomo desligado, medido contra o seu próprio pulso): desvio " + num(get(t, "spreadMs")) + " ms, " + num(get(t, "matched")) +
                " ataques; confiança " + level(conf) + ".");
        // A sixteenth played apart from the rest, and the bars furthest off.
        for (const json& p : t.value("perSixteenth", json::array()))
            if (get(p, "count") >= 4 && std::fabs(get(p, "offsetMs") - mean) >= 10)
                add("Timing na posição '" + p.value("position", "") + "' do tempo: " + num(get(p, "offsetMs"), 0, true) + " ms (" + num(get(p, "count")) +
                    " ataques).");
        std::vector<std::pair<double, int>> bars;
        for (const json& b : t.value("bars", json::array()))
            if (has(b, "meanOffsetMs") && get(b, "matched") >= 2) bars.emplace_back(get(b, "meanOffsetMs"), int(get(b, "bar")));
        std::sort(bars.begin(), bars.end(), [](auto& a, auto& b) { return std::fabs(a.first) > std::fabs(b.first); });
        for (size_t i = 0; i < bars.size() && i < 2; i++)
            if (std::fabs(bars[i].first) >= 15) add("Timing no compasso " + std::to_string(bars[i].second) + ": " + num(bars[i].first, 0, true) + " ms.");
        if (has(t, "swing")) {
            const double s = get(t, "swing");
            add("Swing: " + num(s, 1) + (s < 1.2 ? " (colcheias retas)." : s > 1.8 ? " (colcheias tercinadas)." : " (colcheias suingadas)."));
        }
        const json acc = t.value("accentPerBeatDb", json::array());
        if (acc.size() >= 2) {
            int top = -1;
            double hi = -1e9, lo = 1e9;
            for (size_t i = 0; i < acc.size(); i++)
                if (acc[i].is_number()) {
                    if (acc[i].get<double>() > hi) hi = acc[i].get<double>(), top = int(i);
                    lo = std::min(lo, acc[i].get<double>());
                }
            if (top >= 0 && hi - lo >= 3) add("Acento: o tempo " + std::to_string(top + 1) + " do compasso soa " + num(hi - lo) + " dB acima do mais fraco.");
        }
    }
    if (m.contains("tempo") && has(m["tempo"], "bpm")) {
        const json& t = m["tempo"];
        std::string s = "Tempo: " + num(get(t, "bpm")) + " bpm tocados (metrônomo " + num(get(t, "metronomeBpm")) + ")";
        if (has(t, "driftBpm")) s += ", deriva de " + num(get(t, "driftBpm"), 1, true) + " bpm ao longo do take";
        add(s + ".");
    }
    if (m.contains("intonation") && has(m["intonation"], "inTuneOwnTuningPct")) {
        const json& t = m["intonation"];
        const double conf = get(t, "confidence");
        f.lowConfidence |= conf < 0.5;
        add("Afinação: " + num(get(t, "inTuneOwnTuningPct")) + "% das notas a até 20 cents da sua afinação, erro médio " + num(get(t, "meanAbsCents")) +
            " cents, " + num(get(t, "notes")) + " notas; sua afinação está " + num(get(t, "tuningCents"), 0, true) + " cents do A4 = " +
            num(get(t, "referenceA4")) + " Hz; confiança " + level(conf) + ".");
        if (get(t, "scoops") > 0) add("Afinação: " + num(get(t, "scoops")) + " notas entram deslizando (scoop de mais de 30 cents).");
        if (get(t, "vibratoNotes") > 0) add("Vibrato em " + num(get(t, "vibratoNotes")) + " notas.");
        int k = 0;
        for (const json& p : t.value("phrases", json::array())) {
            k++;
            if (std::fabs(get(p, "driftCents")) >= 30)
                add("Afinação na frase " + std::to_string(k) + " (" + num(get(p, "start"), 1) + " a " + num(get(p, "end"), 1) + " s): " +
                    (get(p, "driftCents") < 0 ? "caiu " : "subiu ") + num(std::fabs(get(p, "driftCents"))) + " cents do começo ao fim.");
        }
        std::vector<std::pair<double, int>> bars;
        for (const json& b : t.value("bars", json::array()))
            if (get(b, "notes") >= 2) bars.emplace_back(get(b, "meanAbsCents"), int(get(b, "bar")));
        std::sort(bars.rbegin(), bars.rend());
        for (size_t i = 0; i < bars.size() && i < 2; i++)
            if (bars[i].first >= 25) add("Afinação no compasso " + std::to_string(bars[i].second) + ": erro médio " + num(bars[i].first) + " cents.");
    }
    if (m.contains("harmony") && get(m["harmony"], "changes") > 0) {
        const json& h = m["harmony"];
        const double conf = get(h, "confidence");
        f.lowConfidence |= conf < 0.5;
        add("Trocas de acorde: " + num(get(h, "changes")) + ", em média " + num(get(h, "changeMeanMs"), 0, true) + " ms do tempo, desvio " +
            num(get(h, "changeSpreadMs")) + " ms; confiança " + level(conf) + ".");
        // The latest changes and the gaps, by bar; the chord a change goes to.
        const json chords = h.value("chordList", json::array());
        auto to = [&](double time) {
            for (const json& c : chords)
                if (get(c, "start") <= time + 0.15 && time < get(c, "end")) return c.value("symbol", std::string());
            return std::string();
        };
        std::vector<std::pair<double, json>> late;
        std::vector<std::string> gaps;
        for (const json& c : h.value("changeList", json::array())) {
            if (has(c, "offsetMs") && get(c, "bar") > 0) late.emplace_back(get(c, "offsetMs"), c);
            if (get(c, "gapDb") <= -10 && get(c, "bar") > 0) gaps.push_back(std::to_string(int(get(c, "bar"))));
        }
        const int gapCount = int(gaps.size());
        gaps.erase(std::unique(gaps.begin(), gaps.end()), gaps.end());   // in time order: one bar once
        std::sort(late.begin(), late.end(), [](auto& a, auto& b) { return std::fabs(a.first) > std::fabs(b.first); });
        for (size_t i = 0; i < late.size() && i < 2; i++)
            if (std::fabs(late[i].first) >= 30) {
                const std::string sym = to(get(late[i].second, "time"));
                add("Troca" + (sym.empty() ? std::string() : " para " + sym) + " no compasso " + std::to_string(int(get(late[i].second, "bar"))) + ": " +
                    num(late[i].first, 0, true) + " ms.");
            }
        if (!gaps.empty()) {
            std::string list;
            for (size_t i = 0; i < gaps.size() && i < 4; i++) list += (i ? ", " : "") + gaps[i];
            add("O som cai antes da troca (mão sai cedo) em " + num(double(gapCount)) + " trocas: compasso" + (gaps.size() > 1 ? "s " : " ") + list + ".");
        }
        if (has(h, "cleanPct"))
            add("Acordes limpos: " + num(get(h, "cleanPct")) + "% das notas ouvidas são do acorde (compare entre takes: a escuta põe parte das notas fora).");
    }
    if (m.contains("dynamics") && has(m["dynamics"], "integratedLufs")) {
        const json& d = m["dynamics"];
        std::string s = "Dinâmica: faixa de " + num(get(d, "rangeLu"), 1) + " LU";
        if (has(d, "attackSpreadDb")) s += "; as batidas variam " + num(get(d, "attackSpreadDb"), 1) + " dB na mesma posição";
        if (std::fabs(get(d, "trendLu")) >= 2) s += std::string("; ") + (get(d, "trendLu") > 0 ? "cresce " : "cai ") + num(std::fabs(get(d, "trendLu")), 1) + " LU ao longo do take";
        add(s + ".");
    }
    if (m.contains("form") && !m["form"].value("form", "").empty()) {
        const json& t = m["form"];
        std::string s = "Forma: " + t.value("form", "") + " em frases de " + num(get(t, "phraseBars")) + " compassos";
        if (get(t, "repeats") > 0) {
            s += "; as repetições batem " + num(get(t, "repeatMatchPct")) + "% com a primeira vez";
            if (has(t, "repeatTempoChangePct")) s += ", com o tempo " + num(get(t, "repeatTempoChangePct"), 1) + "% diferente";
        }
        add(s + ".");
    }
    if (root.contains("limits"))
        for (const json& w : root["limits"].value("warnings", json::array()))
            if (w.is_string()) add("Limite: " + warning(w.get<std::string>()) + ".");
    return f;
}

const char* const kGrammar = R"(root ::= "O que foi bem:\n" item item? item? "\nPara trabalhar:\n" item item? "\nExercícios:\n" item item?
item ::= "- " [^\n#] [^\n#]+ "\n"
)";

std::string prompt(const std::vector<Example>& examples, const Facts& take) {
    std::string p =
        "Dissonância — relatórios de treino\n\n"
        "Cada relatório lê só os fatos medidos no take. Cada frase cita um fato com o seu valor e nunca inventa um número. "
        "Onde a confiança é baixa, o relatório diz isso. Ele diz o que foi bem, uma ou duas coisas para trabalhar, e um exercício para cada uma.\n";
    int n = 0;
    auto block = [&](const Facts& f) {
        p += "\n### Take " + std::to_string(++n) + "\nFatos:\n";
        for (const std::string& l : f.lines) p += "- " + l + "\n";
        p += "Relatório:\n";
    };
    for (const Example& e : examples) {
        block(e.facts);
        p += e.report;
        if (!p.empty() && p.back() != '\n') p += "\n";
    }
    block(take);
    return p;
}

Checked check(const std::string& report, const Facts& f) {
    // The facts' numbers (a value, a bar, a count), normalised.
    static const std::regex number(R"([+\-−]?\d+(?:[.,]\d+)?)");
    auto value = [](std::string s) {
        for (const char* minus : {"−"})
            for (size_t p; (p = s.find(minus)) != std::string::npos;) s.replace(p, std::strlen(minus), "-");
        std::replace(s.begin(), s.end(), ',', '.');
        return std::stod(s);
    };
    std::set<double> known;
    for (const std::string& l : f.lines)
        for (auto it = std::sregex_iterator(l.begin(), l.end(), number); it != std::sregex_iterator(); ++it) {
            const double v = value(it->str());
            known.insert(v), known.insert(std::fabs(v));
        }
    auto isKnown = [&](double v) {
        for (double k : known)
            if (std::fabs(k - v) < 0.051 || std::fabs(k - std::fabs(v)) < 0.051) return true;
        return false;
    };
    // An exercise's own numbers: a slower practice tempo, how many times, how long.
    static const std::regex exercise(R"((\d+)\s*(bpm|vezes|minutos|min|repetições|compassos seguidos))");

    Checked c;
    std::string section;
    std::map<std::string, int> kept;
    size_t pos = 0;
    while (pos < report.size()) {
        size_t end = report.find('\n', pos);
        if (end == std::string::npos) end = report.size();
        const std::string line = report.substr(pos, end - pos);
        pos = end + 1;
        if (line.rfind("- ", 0) != 0) {
            if (!line.empty()) section = line, c.report += line + "\n";
            continue;
        }
        c.items++;
        std::string rest = line;
        if (section.rfind("Exercícios", 0) == 0)
            for (std::smatch m; std::regex_search(rest, m, exercise);) {
                const double v = std::stod(m[1].str());
                const bool fine = m[2] != "bpm" || (f.bpm > 0 && v <= f.bpm) || isKnown(v);
                rest = m.prefix().str() + (fine ? " " : " 999999 ") + m.suffix().str();
            }
        bool ok = true;
        for (auto it = std::sregex_iterator(rest.begin(), rest.end(), number); it != std::sregex_iterator() && ok; ++it) ok = isKnown(value(it->str()));
        if (ok) c.report += line + "\n", kept[section]++;
        else c.dropped++, c.droppedItems.push_back(line);
    }
    c.ok = kept["O que foi bem:"] > 0 && kept["Para trabalhar:"] > 0 && kept["Exercícios:"] > 0 && c.dropped * 3 <= c.items;
    return c;
}

}  // namespace dz::coach
