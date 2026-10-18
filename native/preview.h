#pragma once
#include "win_support.h"
#include <algorithm>
#include <array>
#include <cstdint>

namespace nm::preview {
inline constexpr int width = 320, height = 180, stride = width * 4;
// A bounded local-only preview, never a second NDI subscription. The media
// thread uses try-lock and skips a preview whenever the GUI owns the buffer.
struct Shared {
    int32_t version{}, width{}, height{}, stride{};
    int64_t revision{}, timestamp{}, sequence{};
    int32_t good{};
    uint8_t reserved[20]{};
    uint8_t pixels[preview::stride * preview::height]{};
};
static_assert(offsetof(Shared, pixels) == 64);
inline void pixel(uint8_t y, uint8_t u, uint8_t v, uint8_t* out) noexcept {
    const int c = std::max(0, int(y) - 16), d = int(u) - 128, e = int(v) - 128;
    auto clip = [](int value) { return static_cast<uint8_t>(std::clamp((value + 128) >> 8, 0, 255)); };
    out[0] = clip(298 * c + 541 * d); out[1] = clip(298 * c - 55 * d - 136 * e);
    out[2] = clip(298 * c + 459 * e); out[3] = 255;
}
inline void downscale(const uint8_t* input, int rowBytes, uint8_t* output) noexcept {
    for (int y = 0; y < height; ++y) {
        const auto row = input + static_cast<size_t>(y * 6) * rowBytes;
        for (int x = 0; x < width; ++x) {
            const int sx = x * 6;
            const auto pair = row + (sx / 2) * 4;
            pixel(pair[(sx & 1) ? 3 : 1], pair[0], pair[2], output + (y * width + x) * 4);
        }
    }
}
class Writer {
    WinHandle mapping_, mutex_;
    Shared* shared_{};
    int64_t next_{};
public:
    const std::wstring name;
    explicit Writer(std::wstring value) : name(std::move(value)) {
        mutex_.value = CreateMutexW(nullptr, FALSE, (name + L".lock").c_str());
        mapping_.value = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Shared), name.c_str());
        if (!mutex_.value || !mapping_.value) return; // Optional preview cannot prevent SDI startup.
        shared_ = static_cast<Shared*>(MapViewOfFile(mapping_, FILE_MAP_WRITE, 0, 0, sizeof(Shared)));
        if (shared_) { shared_->version = 1; shared_->width = width; shared_->height = height; shared_->stride = stride; }
    }
    ~Writer() { if (shared_) UnmapViewOfFile(shared_); }
    void publish(const uint8_t* input, int rowBytes, bool good, uint64_t revision) noexcept {
        if (!shared_) return;
        const auto now = qpc();
        if (now < next_) return;
        next_ = now + qpc_frequency() / 4; // No more than four previews per second.
        const auto lock = WaitForSingleObject(mutex_, 0);
        if (lock != WAIT_OBJECT_0 && lock != WAIT_ABANDONED) return;
        shared_->good = 0; // A publisher crash must not expose half-written pixels as fresh.
        if (good && input && rowBytes >= 3840) downscale(input, rowBytes, shared_->pixels);
        shared_->good = good && input && rowBytes >= 3840 ? 1 : 0; shared_->revision = static_cast<int64_t>(revision);
        shared_->timestamp = now; ++shared_->sequence;
        ReleaseMutex(mutex_);
    }
};
}
