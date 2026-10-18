#pragma once
#include <nlohmann/json.hpp>
#include <string>
#include <cmath>
#include <array>
#include <algorithm>
#include "audio_shared.h"
namespace nm {
struct OutputSettings {
    std::string source, url, bandwidth{"highest"}, sizing{"fit"}, audioMode{"silent"}, endpoint, audioKey;
    int left{1}, right{2}, delayMs{};
    double gainDb{};
    bool muted{};
    nlohmann::json json() const {
        return {{"source",source},{"sourceUrl",url},{"bandwidth",bandwidth},{"sizing",sizing},
            {"audioMode",audioMode},{"audioEndpoint",endpoint},{"audioKey",audioKey},{"left",left},{"right",right},
            {"gainDb",gainDb},{"muted",muted},{"delayMs",delayMs}};
    }
    static OutputSettings parse(const nlohmann::json& j) {
        OutputSettings s;
        s.source=j.at("source").get<std::string>(); s.url=j.value("sourceUrl","");
        s.bandwidth=j.value("bandwidth","highest"); s.sizing=j.value("sizing","fit");
        s.audioMode=j.value("audioMode","silent"); s.endpoint=j.value("audioEndpoint",""); s.audioKey=j.value("audioKey","");
        s.left=j.value("left",1); s.right=j.value("right",2); s.delayMs=j.value("delayMs",0);
        s.gainDb=j.value("gainDb",0.0); s.muted=j.value("muted",false);
        if(s.source.empty() || s.source.size()>512 || s.url.size()>512 || s.endpoint.size()>500 ||
            (s.bandwidth!="highest" && s.bandwidth!="lowest") ||
            (s.sizing!="fit" && s.sizing!="fill" && s.sizing!="stretch") ||
            (s.audioMode!="silent" && s.audioMode!="ndi" && s.audioMode!="fixed") ||
            s.left<1 || s.right<1 || s.left>64 || s.right>64 || s.delayMs<0 || s.delayMs>500 ||
            !std::isfinite(s.gainDb) || s.gainDb < -60 || s.gainDb > 12 ||
            (s.audioMode=="fixed" && (s.endpoint.empty() || (!s.audioKey.empty() &&
                (s.audioKey.size()!=64 || s.audioKey.find_first_not_of("0123456789ABCDEF")!=std::string::npos)))))
            throw std::runtime_error("Invalid output settings");
        return s;
    }
};
class AudioDelay {
    std::array<audio::Stereo,24001> samples_{};
    size_t position_{}; int length_{};
public:
    void reset(int milliseconds) noexcept { samples_.fill({}); position_=0; length_=std::clamp(milliseconds,0,500)*48; }
    audio::Stereo process(audio::Stereo sample) noexcept {
        if (!length_) return sample;
        auto old=samples_[position_]; samples_[position_]=sample;
        position_=(position_+1)%length_; return old;
    }
};
}
