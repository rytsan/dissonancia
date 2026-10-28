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
    // Each line carries our judgement, so the model words it rather than weighs it: [bom] goes to
    // what went well, [trabalhar] to what to work on, [neutro] either or neither, [limite] to the limits.
    auto add = [&](const char* kind, const char* tag, const std::string& s) {
        f.lines.push_back(std::string("[") + tag + "] " + s);
        f.kinds.push_back(kind);
    };
    f.bpm = root.contains("session") ? get(root["session"], "bpm") : 0;

    // A voice's attacks spread more than a pick's (a consonant before the vowel): looser limits.
    const bool voice = m.contains("intonation");
    const double good = voice ? 30 : 20, bad = voice ? 40 : 25;
    if (m.contains("timing") && get(m["timing"], "matched") > 0) {
        const json& t = m["timing"];
        const bool metro = t.value("grid", "") == "metronome";
        const double mean = get(t, "meanOffsetMs"), conf = get(t, "confidence");
        f.lowConfidence |= conf < 0.5;
        // Against the player's own beats (the click off) the mean says nothing: only the spread.
        if (metro)
            add("timing", conf < 0.5 ? "neutro" : std::fabs(mean) <= 10 && get(t, "spreadMs") <= good ? "bom" : std::fabs(mean) > 15 || get(t, "spreadMs") > bad ? "trabalhar" : "neutro",
                "Timing: média " + num(mean, 0, true) + " ms (" + late(mean) + " do metrônomo), desvio " + num(get(t, "spreadMs")) + " ms, " +
                num(get(t, "matched")) + " ataques; confiança " + level(conf) + ".");
        else
            add("timing", get(t, "spreadMs") <= good ? "bom" : get(t, "spreadMs") > bad ? "trabalhar" : "neutro", "Timing (metrônomo desligado, medido contra o seu próprio pulso): desvio " + num(get(t, "spreadMs")) + " ms, " + num(get(t, "matched")) +
                " ataques; confiança " + level(conf) + ".");
        // A sixteenth played apart from the rest, and the bars furthest off.
        for (const json& p : t.value("perSixteenth", json::array()))
            if (get(p, "count") >= 4 && std::fabs(get(p, "offsetMs") - mean) >= 10 && std::fabs(get(p, "offsetMs")) >= 15)
                add("timing-position", "trabalhar", "Timing na posição '" + p.value("position", "") + "' do tempo: " + num(get(p, "offsetMs"), 0, true) + " ms (" + num(get(p, "count")) +
                    " ataques).");
        std::vector<std::pair<double, int>> bars;
        for (const json& b : t.value("bars", json::array()))
            if (has(b, "meanOffsetMs") && get(b, "matched") >= 2) bars.emplace_back(get(b, "meanOffsetMs"), int(get(b, "bar")));
        std::sort(bars.begin(), bars.end(), [](auto& a, auto& b) { return std::fabs(a.first) > std::fabs(b.first); });
        for (size_t i = 0; i < bars.size() && i < 2; i++)
            if (std::fabs(bars[i].first) >= (voice ? 30 : 15)) add("timing-bar", "trabalhar", "Timing no compasso " + std::to_string(bars[i].second) + ": " + num(bars[i].first, 0, true) + " ms.");
        if (has(t, "swing")) {
            const double s = get(t, "swing");
            add("swing", "neutro", "Swing: " + num(s, 1) + (s < 1.2 ? " (colcheias retas)." : s > 1.8 ? " (colcheias tercinadas)." : " (colcheias suingadas)."));
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
            if (top >= 0 && hi - lo >= 3) add("accent", "neutro", "Acento: o tempo " + std::to_string(top + 1) + " do compasso soa " + num(hi - lo) + " dB acima do mais fraco.");
        }
    }
    // The tempo heard is left out when it is far from the click (a beat tracker's octave or 3:2 error
    // is likelier than a player 15 % off a click they heard in the count-in).
    if (m.contains("tempo") && has(m["tempo"], "bpm") &&
        (get(m["tempo"], "metronomeBpm") <= 0 || std::fabs(get(m["tempo"], "bpm") / get(m["tempo"], "metronomeBpm") - 1) <= 0.15)) {
        const json& t = m["tempo"];
        std::string s = "Tempo: " + num(get(t, "bpm")) + " bpm tocados (metrônomo " + num(get(t, "metronomeBpm")) + ")";
        if (has(t, "driftBpm")) s += ", deriva de " + num(get(t, "driftBpm"), 1, true) + " bpm ao longo do take";
        const double drift = std::fabs(get(t, "driftBpm")), off = get(t, "metronomeBpm") > 0 ? std::fabs(get(t, "bpm") / get(t, "metronomeBpm") - 1) : 0;
        add("tempo", drift <= 2 && off <= 0.02 ? "bom" : drift > 3 || off > 0.05 ? "trabalhar" : "neutro", s + ".");
    }
    if (m.contains("intonation") && has(m["intonation"], "inTuneOwnTuningPct")) {
        const json& t = m["intonation"];
        const double conf = get(t, "confidence");
        f.lowConfidence |= conf < 0.5;
        add("intonation", get(t, "inTuneOwnTuningPct") >= 75 ? "bom" : get(t, "inTuneOwnTuningPct") < 60 ? "trabalhar" : "neutro",
            "Afinação: " + num(get(t, "inTuneOwnTuningPct")) + "% das notas a até 20 cents da sua afinação, erro médio " + num(get(t, "meanAbsCents")) +
            " cents, " + num(get(t, "notes")) + " notas; confiança " + level(conf) + ".");
        add("tuning", std::fabs(get(t, "tuningCents")) <= 10 ? "bom" : "neutro",
            "Afinação de referência: a sua está " + num(get(t, "tuningCents"), 0, true) + " cents do A4 = " + num(get(t, "referenceA4")) + " Hz.");
        if (get(t, "scoops") > 0) add("scoop", get(t, "scoops") > 0.25 * get(t, "notes") ? "trabalhar" : "neutro", "Afinação: " + num(get(t, "scoops")) + " notas entram deslizando (scoop de mais de 30 cents).");
        if (get(t, "vibratoNotes") > 0) add("vibrato", "neutro", "Vibrato em " + num(get(t, "vibratoNotes")) + (get(t, "vibratoNotes") == 1 ? " nota." : " notas."));
        int k = 0;
        for (const json& p : t.value("phrases", json::array())) {
            k++;
            if (std::fabs(get(p, "driftCents")) >= 30)
                add("phrase-drift", "trabalhar", "Afinação na frase " + std::to_string(k) + " (" + num(get(p, "start"), 1) + " a " + num(get(p, "end"), 1) + " s): " +
                    (get(p, "driftCents") < 0 ? "caiu " : "subiu ") + num(std::fabs(get(p, "driftCents"))) + " cents do começo ao fim.");
        }
        std::vector<std::pair<double, int>> bars;
        for (const json& b : t.value("bars", json::array()))
            if (get(b, "notes") >= 2) bars.emplace_back(get(b, "meanAbsCents"), int(get(b, "bar")));
        std::sort(bars.rbegin(), bars.rend());
        for (size_t i = 0; i < bars.size() && i < 2; i++)
            if (bars[i].first >= 25) add("intonation-bar", "trabalhar", "Afinação no compasso " + std::to_string(bars[i].second) + ": erro médio " + num(bars[i].first) + " cents.");
    }
    if (m.contains("harmony") && get(m["harmony"], "changes") > 0) {
        const json& h = m["harmony"];
        const double conf = get(h, "confidence");
        f.lowConfidence |= conf < 0.5;
        add("changes", conf < 0.5 ? "neutro" : std::fabs(get(h, "changeMeanMs")) <= 15 && get(h, "changeSpreadMs") <= 25 ? "bom" : "trabalhar", "Trocas de acorde: " + num(get(h, "changes")) + ", em média " + num(get(h, "changeMeanMs"), 0, true) + " ms do tempo, desvio " +
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
                add("late-change", "trabalhar", "Troca" + (sym.empty() ? std::string() : " para " + sym) + " no compasso " + std::to_string(int(get(late[i].second, "bar"))) + ": " +
                    num(late[i].first, 0, true) + " ms.");
            }
        if (!gaps.empty()) {
            std::string list;
            for (size_t i = 0; i < gaps.size() && i < 4; i++) list += (i ? ", " : "") + gaps[i];
            add("gap", "trabalhar", "O som cai antes da troca (mão sai cedo) em " + num(double(gapCount)) + " trocas: compasso" + (gaps.size() > 1 ? "s " : " ") + list + ".");
        }
        if (has(h, "cleanPct"))
            add("clean", "neutro", "Acordes limpos: " + num(get(h, "cleanPct")) + "% das notas ouvidas são do acorde (compare entre takes: a escuta põe parte das notas fora).");
    }
    if (m.contains("dynamics") && has(m["dynamics"], "integratedLufs")) {
        const json& d = m["dynamics"];
        std::string s = "Dinâmica: faixa de " + num(get(d, "rangeLu"), 1) + " LU";
        if (has(d, "attackSpreadDb")) s += "; as batidas variam " + num(get(d, "attackSpreadDb"), 1) + " dB na mesma posição";
        if (std::fabs(get(d, "trendLu")) >= 2) s += std::string("; ") + (get(d, "trendLu") > 0 ? "cresce " : "cai ") + num(std::fabs(get(d, "trendLu")), 1) + " LU ao longo do take";
        const double spread = get(d, "attackSpreadDb", 3);
        add("dynamics", spread <= 2.5 ? "bom" : spread > 4 ? "trabalhar" : "neutro", s + ".");
    }
    if (m.contains("form") && !m["form"].value("form", "").empty()) {
        const json& t = m["form"];
        std::string s = "Forma: " + t.value("form", "") + " em frases de " + num(get(t, "phraseBars")) + " compassos";
        if (get(t, "repeats") > 0) {
            s += "; as repetições batem " + num(get(t, "repeatMatchPct")) + "% com a primeira vez";
            if (has(t, "repeatTempoChangePct")) s += ", com o tempo " + num(get(t, "repeatTempoChangePct"), 1) + "% diferente";
        }
        add("form", get(t, "repeats") > 0 && get(t, "repeatMatchPct") >= 85 ? "bom" : "neutro", s + ".");
    }
    if (root.contains("limits"))
        for (const json& w : root["limits"].value("warnings", json::array()))
            if (w.is_string()) {
                const std::string text = w.get<std::string>();
                add(text.rfind("clipping", 0) == 0 ? "clipping" : text.rfind("very quiet", 0) == 0 ? "quiet" : text.rfind("frames dropped", 0) == 0 ? "dropped" : "low-confidence",
                    "limite", warning(text) + ".");
            }
    return f;
}

std::string grammar(const Facts& f) {
    // An item: one Portuguese sentence (Latin letters, digits, punctuation; a full stop only at its end),
    // at most ~240 characters: a model cannot run on into anything else.
    std::string g = R"(root ::= "O que foi bem:\n" item item? item? "\nPara trabalhar:\n" item item? "\nExercícios:\n" item item?)";
    if (f.lowConfidence) g += R"( "\nConfiança:\n" item)";
    g += R"(
item ::= "- " [A-Za-zÀ-ÿ0-9] ch{6,240} "." "\n"
ch ::= [A-Za-zÀ-ÿ0-9 ,;:!?()'"%+/=–—…-]
)";
    return g;
}

Exercises parse_exercises(const std::string& text) {
    Exercises e;
    size_t pos = 0;
    while (pos < text.size()) {
        size_t end = text.find('\n', pos);
        if (end == std::string::npos) end = text.size();
        std::string line = text.substr(pos, end - pos);
        pos = end + 1;
        if (line.empty() || line[0] == '#') continue;
        const size_t colon = line.find(':');
        if (colon == std::string::npos) continue;
        size_t t = colon + 1;
        while (t < line.size() && line[t] == ' ') t++;
        e[line.substr(0, colon)].push_back(line.substr(t));
    }
    return e;
}

namespace {
// The kinds of fact a take has to work on: the limits first, then [trabalhar] in the facts' order.
std::vector<std::string> to_work_on(const Facts& f) {
    std::vector<std::string> k;
    for (const char* tag : {"[limite]", "[trabalhar]"})
        for (size_t i = 0; i < f.lines.size() && i < f.kinds.size(); i++)
            if (f.lines[i].rfind(tag, 0) == 0 && std::find(k.begin(), k.end(), f.kinds[i]) == k.end()) k.push_back(f.kinds[i]);
    return k;
}
bool sung(const Facts& f) { return std::find(f.kinds.begin(), f.kinds.end(), "intonation") != f.kinds.end(); }
}  // namespace

std::vector<Example> select(const std::vector<Example>& bank, const Facts& take, size_t n) {
    const std::vector<std::string> list = to_work_on(take);
    const std::set<std::string> want(list.begin(), list.end());
    std::vector<std::pair<int, size_t>> score;
    for (size_t i = 0; i < bank.size(); i++) {
        int s = sung(bank[i].facts) == sung(take) ? 3 : 0;
        for (const std::string& k : to_work_on(bank[i].facts)) s += 2 * int(want.count(k));
        if (take.lowConfidence && bank[i].facts.lowConfidence) s += 3;
        score.emplace_back(-s, i);
    }
    std::sort(score.begin(), score.end());
    std::vector<size_t> pick;
    for (size_t i = 0; i < score.size() && i < n; i++) pick.push_back(score[i].second);
    std::sort(pick.begin(), pick.end());   // the bank's order: the same examples make the same prompt prefix
    std::vector<Example> out;
    for (size_t i : pick) out.push_back(bank[i]);
    return out;
}

std::string prompt(const std::vector<Example>& examples, const Facts& take, const Exercises& exercises) {
    std::string p =
        "Dissonância — relatórios de treino\n\n"
        "Cada relatório lê só os fatos medidos no take. Cada frase cita um fato com o seu valor e nunca inventa um número. "
        "Onde a confiança é baixa, o relatório diz isso. Ele diz o que foi bem (os fatos marcados [bom]), uma ou duas coisas para trabalhar "
        "(os marcados [trabalhar] ou [limite]), e um exercício para cada uma, escolhido do repertório e ajustado aos compassos e ao tempo do take. "
        "Sem fato [bom], o que foi bem vem do melhor dos [neutro]. "
        "Não compara com takes anteriores: só este take foi medido. Se um fato tem confiança baixa, o relatório termina com uma seção "
        "Confiança que diz isso.\n";
    int n = 0;
    auto block = [&](const Facts& f) {
        p += "\n### Take " + std::to_string(++n) + "\nFatos:\n";
        for (const std::string& l : f.lines) p += "- " + l + "\n";
        // The repertoire for what this take has to work on: two per kind, six at most.
        std::vector<std::string> ex;
        for (const std::string& k : to_work_on(f))
            if (auto it = exercises.find(k); it != exercises.end())
                for (size_t i = 0; i < it->second.size() && i < 2 && ex.size() < 6; i++) ex.push_back(it->second[i]);
        if (!ex.empty()) {
            p += "Repertório de exercícios:\n";
            for (const std::string& e : ex) p += "- " + e + "\n";
        }
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
    static const std::regex name(R"([A-Za-z#]+\d+)");   // A4, Cadd9, F#7: names, not numbers
    auto strip = [](const std::string& t) { return std::regex_replace(t, name, "§"); };
    std::vector<std::pair<double, std::string>> known;   // each number of the facts, with its line's tag
    for (const std::string& raw : f.lines) {
        const std::string tag = raw.rfind('[', 0) == 0 ? raw.substr(1, raw.find(']') - 1) : "";
        const std::string l = strip(raw);
        for (auto it = std::sregex_iterator(l.begin(), l.end(), number); it != std::sregex_iterator(); ++it) {
            const double v = value(it->str());
            known.emplace_back(v, tag), known.emplace_back(std::fabs(v), tag);
        }
    }
    // The tags of the facts that hold v (none: v is not a fact's).
    auto tagsOf = [&](double v) {
        std::set<std::string> tags;
        for (const auto& [k, tag] : known)
            if (std::fabs(k - v) < 0.051 || std::fabs(k - std::fabs(v)) < 0.051) tags.insert(tag);
        return tags;
    };
    auto isKnown = [&](double v) { return !tagsOf(v).empty(); };
    // An exercise's own numbers: a slower practice tempo, how many times, how long.
    static const std::regex exercise(R"((\d+)\s*(bpm|vezes|minutos|min|repetições|compassos seguidos))");

    Checked c;
    std::string section;
    std::map<std::string, int> kept;
    std::set<std::pair<std::string, std::multiset<double>>> said;   // (section, an item's numbers): no item twice
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
        std::string rest = strip(line);
        if (section.rfind("Exercícios", 0) == 0)
            for (std::smatch m; std::regex_search(rest, m, exercise);) {
                const double v = std::stod(m[1].str());
                const bool fine = m[2] != "bpm" || (f.bpm > 0 && v <= f.bpm) || isKnown(v);
                rest = m.prefix().str() + (fine ? " " : " 999999 ") + m.suffix().str();
            }
        // Only this take was measured: no comparison with an earlier one.
        static const std::regex earlier(R"(\b(melhorou|piorou|que antes|take anterior|último take|da última vez)\b)", std::regex::icase);
        const char* why = std::regex_search(line, earlier) ? "compares with an earlier take" : nullptr;
        bool ok = !why;
        std::multiset<double> nums;
        bool praise = false;   // a number from a fact that is not one to work on
        // A quoted position ('e', '&') must be one the facts name.
        static const std::regex quoted(R"('([^']{1,3})')");
        for (auto it = std::sregex_iterator(line.begin(), line.end(), quoted); it != std::sregex_iterator() && ok; ++it)
            if (!std::any_of(f.lines.begin(), f.lines.end(), [&](const std::string& l) { return l.find(it->str()) != std::string::npos; }))
                ok = false, why = "a position the facts do not name", c.invented++;
        const bool exercise = section.rfind("Exercícios", 0) == 0;
        for (auto it = std::sregex_iterator(rest.begin(), rest.end(), number); it != std::sregex_iterator() && ok; ++it) {
            const double v = value(it->str());
            std::set<std::string> tags = tagsOf(v);
            // An exercise may run on into the bar after one the facts name ("the bars 12 and 13").
            if (tags.empty() && exercise && v == std::floor(v) && v > 1 && line.find("compasso") != std::string::npos) tags = tagsOf(v - 1);
            ok = !tags.empty();
            if (!ok) why = "a number not in the facts", c.invented++;
            nums.insert(std::fabs(v));
            praise |= tags.count("bom") || tags.count("neutro");
        }
        // What went well cites a fact that is not one to work on; the same numbers twice in a section is a repeat.
        if (ok && section == "O que foi bem:" && !nums.empty() && !praise) ok = false, why = "a fact to work on, told as going well";
        if (ok && !nums.empty() && !said.insert({section, nums}).second) ok = false, why = "said already";
        if (ok) c.report += line + "\n", kept[section]++;
        else c.dropped++, c.droppedItems.push_back(std::string("(") + why + ") " + line);
    }
    c.hedged = c.report.find("confiança") != std::string::npos;
    c.ok = kept["O que foi bem:"] > 0 && kept["Para trabalhar:"] > 0 && kept["Exercícios:"] > 0 && c.dropped * 3 <= c.items &&
           (!f.lowConfidence || (c.hedged && kept["Confiança:"] > 0));
    return c;
}

}  // namespace dz::coach
