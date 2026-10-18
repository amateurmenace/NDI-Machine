#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <limits>
#include <stdexcept>

namespace nm
{
// Integer timeline: floor(frame * 48000 * 1001 / 30000), safe for years of HD playout.
constexpr int64_t audio_position(int64_t frame)
{
    return frame * 8008 / 5;
}
constexpr int audio_count(int64_t frame)
{
    return static_cast<int>(audio_position(frame + 1) - audio_position(frame));
}
constexpr int64_t frame_duration = 1001, frame_scale = 30000;

enum class Calibration
{
    Smpte,
    Ebu,
    Unity,
    Custom
};
inline float calibrate(float sample, Calibration kind, float custom = 1.0f)
{
    if (!std::isfinite(sample))
        return 0;
    const float scale = kind == Calibration::Smpte   ? 0.1f
                        : kind == Calibration::Ebu   ? 0.125892541f
                        : kind == Calibration::Unity ? 1.0f
                                                     : custom;
    return std::isfinite(scale) && scale >= 0 ? sample * scale : 0;
}
inline int32_t pcm32(float value)
{
    if (!std::isfinite(value))
        return 0;
    if (value >= 1)
        return INT32_MAX;
    if (value <= -1)
        return INT32_MIN;
    return static_cast<int32_t>(static_cast<double>(value) * INT32_MAX);
}
struct Ramp
{
    float value{}, target{}, step{};
    uint32_t remaining{};
    void set(float next, uint32_t samples = 240)
    {
        target = next;
        remaining = samples;
        step = samples ? (next - value) / samples : 0;
        if (!samples)
            value = next;
    }
    float next()
    {
        if (remaining)
        {
            value += step;
            if (!--remaining)
                value = target;
        }
        return value;
    }
};
// Single writer and single reader only. Producer never overwrites an unread item.
template <class T, size_t N> class Spsc
{
    static_assert(N > 1);
    std::array<T, N> slots_{};
    alignas(64) std::atomic<size_t> write_{0};
    alignas(64) std::atomic<size_t> read_{0};

  public:
    bool push(const T &value) noexcept
    {
        auto w = write_.load(std::memory_order_relaxed);
        auto next = (w + 1) % N;
        if (next == read_.load(std::memory_order_acquire))
            return false;
        slots_[w] = value;
        write_.store(next, std::memory_order_release);
        return true;
    }
    bool pop(T &value) noexcept
    {
        auto r = read_.load(std::memory_order_relaxed);
        if (r == write_.load(std::memory_order_acquire))
            return false;
        value = slots_[r];
        read_.store((r + 1) % N, std::memory_order_release);
        return true;
    }
};
struct Freshness
{
    int64_t last_count{}, last_change_ms{-1};
    void observe(int64_t count, int64_t now)
    {
        if (count != last_count)
        {
            last_count = count;
            last_change_ms = now;
        }
    }
    bool fresh(int64_t now, int64_t limit = 1000) const
    {
        return last_change_ms >= 0 && now >= last_change_ms && now - last_change_ms < limit;
    }
};
} // namespace nm
