// STUDIO S3 separation helper (optional component): Demucs v4 (demucs.cpp, MIT) on a whole file,
// run by the app as its own process so a crash or a cancel never touches the app. Writes one
// float32 stereo 44.1 kHz WAV per stem and reports progress on stdout:
//   "[THREAD i] ( 42.000%) …"  per inference thread (from demucs.cpp), then "stem <name> <path>".
//   dz_separate <ggml model> <audio in> <out dir> [threads]
#include <filesystem>
#include <iostream>
#include <string>
#include <thread>
#include <vector>

#include "dsp.hpp"
#include "miniaudio.h"
#include "model.hpp"
#include "threaded_inference.hpp"

int main(int argc, char** argv) {
    if (argc < 4) { std::cerr << "usage: dz_separate <model.bin> <audio> <out dir> [threads]\n"; return 2; }
    const std::string modelPath = argv[1], in = argv[2], outDir = argv[3];
    const int threads = argc > 4 ? std::max(1, std::atoi(argv[4])) : std::max(1, std::min(4, int(std::thread::hardware_concurrency() / 2)));

    // Decode to stereo 44.1 kHz float (miniaudio converts channels and resamples).
    ma_decoder dec;
    ma_decoder_config dc = ma_decoder_config_init(ma_format_f32, 2, demucscpp::SUPPORTED_SAMPLE_RATE);
    if (ma_decoder_init_file(in.c_str(), &dc, &dec) != MA_SUCCESS) { std::cerr << "error cannot decode " << in << "\n"; return 1; }
    std::vector<float> x;
    float buf[8192];
    for (ma_uint64 got = 0; ma_decoder_read_pcm_frames(&dec, buf, 4096, &got) == MA_SUCCESS && got > 0;) x.insert(x.end(), buf, buf + got * 2);
    ma_decoder_uninit(&dec);
    const long n = long(x.size() / 2);
    if (n == 0) { std::cerr << "error empty file\n"; return 1; }
    Eigen::MatrixXf audio(2, n);
    for (long i = 0; i < n; i++) { audio(0, i) = x[size_t(2 * i)]; audio(1, i) = x[size_t(2 * i + 1)]; }

    demucscpp::demucs_model model{};
    if (!demucscpp::load_demucs_model(modelPath, &model)) { std::cerr << "error cannot load model " << modelPath << "\n"; return 1; }
    const Eigen::Tensor3dXf targets = demucscppthreaded::threaded_inference(model, audio, threads);

    const char* names[6] = {"drums", "bass", "other", "vocals", "guitar", "piano"};
    std::filesystem::create_directories(outDir);
    for (int t = 0; t < (model.is_4sources ? 4 : 6); t++) {
        const std::string path = (std::filesystem::path(outDir) / (std::string(names[t]) + ".wav")).string();
        ma_encoder enc;
        ma_encoder_config ec = ma_encoder_config_init(ma_encoding_format_wav, ma_format_f32, 2, demucscpp::SUPPORTED_SAMPLE_RATE);
        if (ma_encoder_init_file(path.c_str(), &ec, &enc) != MA_SUCCESS) { std::cerr << "error cannot write " << path << "\n"; return 1; }
        std::vector<float> st(size_t(n) * 2);
        for (long i = 0; i < n; i++) { st[size_t(2 * i)] = targets(t, 0, i); st[size_t(2 * i + 1)] = targets(t, 1, i); }
        ma_encoder_write_pcm_frames(&enc, st.data(), ma_uint64(n), nullptr);
        ma_encoder_uninit(&enc);
        std::cout << "stem " << names[t] << " " << path << std::endl;
    }
    return 0;
}
