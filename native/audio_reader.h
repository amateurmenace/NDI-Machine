#pragma once
#include "audio_shared.h"
#include <algorithm>
#include <numbers>

namespace nm::audio
{
// A bounded, slow PLL on buffer occupancy. Hardware completion consumption is
// the output clock. A nominal 48 kHz input is never assumed to share that clock.
class ClockAdapter
{
    double integral_{}, ratio_{1};

  public:
    void reset() noexcept
    {
        integral_ = 0;
        ratio_ = 1;
    }
    double update(double errorFrames, int outputFrames) noexcept
    {
        const double dt = outputFrames / 48000.0;
        integral_ = std::clamp(integral_ + errorFrames * dt * 1e-8, -.0008, .0008);
        const double desired = 1 + std::clamp(errorFrames * 2e-7 + integral_, -.001, .001);
        ratio_ += std::clamp(desired - ratio_, -2e-6, 2e-6);
        return ratio_;
    }
    double ratio() const noexcept
    {
        return ratio_;
    }
};
// 32-tap normalized windowed sinc, 1024 fractional phases, unity at DC.
// This bounded converter handles small clock errors only, not arbitrary rates.
class Sinc
{
    std::array<std::array<float, 32>, 1024> table_{};

  public:
    Sinc()
    {
        for (int phase = 0; phase < 1024; ++phase)
        {
            double sum{};
            for (int tap = 0; tap < 32; ++tap)
            {
                const double x = tap - 15 - phase / 1024.0;
                const double window = std::abs(x) >= 16 ? 0 : .5 + .5 * std::cos(std::numbers::pi * x / 16);
                const double z = .97 * std::numbers::pi * x;
                const double weight = .97 * (std::abs(z) < 1e-12 ? 1 : std::sin(z) / z) * window;
                table_[phase][tap] = static_cast<float>(weight);
                sum += weight;
            }
            for (auto &value : table_[phase])
                value = static_cast<float>(value / sum);
        }
    }
    Stereo sample(const Stereo *first, double fraction) const noexcept
    {
        const auto phase = std::clamp(static_cast<int>(fraction * 1024), 0, 1023);
        Stereo result{};
        for (int i = 0; i < 32; ++i)
        {
            result.left += first[i].left * table_[phase][i];
            result.right += first[i].right * table_[phase][i];
        }
        return result;
    }
};
class Reader
{
    Shared &shared_;
    Sinc sinc_;
    ClockAdapter clock_;
    std::array<Stereo, 2048> scratch_{};
    double position_{};
    int64_t generation_{};
    std::array<int64_t, 64> endpoint_{};
    int64_t discontinuities_{};
    bool primed_{};
    int preroll_{4};
    static constexpr int target = 2400; // 50 ms after each consumption.
  public:
    uint64_t resets{}, overruns{}, underruns{}, staleBlocks{}, renderedFrames{};
    double bufferFrames{}, correctionPpm{}, maxAbsCorrectionPpm{};
    explicit Reader(Shared &shared, std::string_view endpoint = {}) : shared_(shared)
    {
        if (endpoint.size() >= sizeof(endpoint_))
            throw std::runtime_error("Endpoint ID exceeds shared ABI bound");
        std::array<char, sizeof(endpoint_)> normalized{};
        for (size_t i = 0; i < endpoint.size(); ++i)
            normalized[i] = endpoint[i] >= 'A' && endpoint[i] <= 'Z' ? endpoint[i] + 32 : endpoint[i];
        memcpy(endpoint_.data(), normalized.data(), sizeof(endpoint_));
    }
    bool identity() const noexcept
    {
        if (load(shared_.version) != abi || load(shared_.sampleRate) != 48000 || load(shared_.channels) != 2)
            return false;
        for (size_t i = 0; i < endpoint_.size(); ++i)
            if (load(shared_.endpoint[i]) != endpoint_[i])
                return false;
        return true;
    }
    bool ready() const noexcept
    {
        return identity() && fresh(shared_, qpc()) && load(shared_.published) >= target + 6406 + 48;
    }
    bool render(Stereo *destination, int count) noexcept
    {
        if (count < 1 || count > 1602)
            return false;
        std::fill_n(destination, count, Stereo{});
        const auto generation = load(shared_.generation);
        const auto end = load(shared_.published);
        if (!identity() || !fresh(shared_, qpc()))
        {
            ++staleBlocks;
            primed_ = false;
            clock_.reset();
            return false;
        }
        if (generation != generation_)
        {
            generation_ = generation;
            primed_ = false;
            ++resets;
            clock_.reset();
        }
        const auto discontinuities = load(shared_.discontinuities);
        if (discontinuities != discontinuities_)
        {
            discontinuities_ = discontinuities;
            primed_ = false;
            clock_.reset();
        }
        if (!primed_)
        {
            const auto lookback = target + (preroll_ ? 6406 : count) + 32;
            if (end < lookback + 16)
            {
                ++underruns;
                return false;
            }
            position_ = static_cast<double>(end - lookback);
            primed_ = true;
        }
        bufferFrames = end - position_;
        if (bufferFrames > capacity - 32)
        {
            ++overruns;
            primed_ = false;
            clock_.reset();
            return false;
        }
        const auto ratio = preroll_ ? 1.0 : clock_.update(bufferFrames - count - target - 32, count);
        correctionPpm = (ratio - 1) * 1e6;
        maxAbsCorrectionPpm = std::max(maxAbsCorrectionPpm, std::abs(correctionPpm));
        const auto first = static_cast<int64_t>(std::floor(position_)) - 15;
        const auto last = static_cast<int64_t>(std::floor(position_ + (count - 1) * ratio)) + 16;
        const auto copied = last - first + 1;
        if (first < 0 || last >= end || copied > static_cast<int64_t>(scratch_.size()))
        {
            ++underruns;
            primed_ = false;
            return false;
        }
        for (int64_t index = 0; index < copied; ++index)
            if (!read(shared_, static_cast<uint64_t>(first + index), scratch_[index]))
            {
                ++overruns;
                primed_ = false;
                return false;
            }
        if (generation != load(shared_.generation) || !fresh(shared_, qpc()))
        {
            primed_ = false;
            ++staleBlocks;
            return false;
        }
        for (int index = 0; index < count; ++index)
        {
            const auto at = position_ + index * ratio;
            const auto integer = static_cast<int64_t>(std::floor(at));
            destination[index] = sinc_.sample(scratch_.data() + integer - 15 - first, at - integer);
        }
        position_ += count * ratio;
        if (preroll_)
            --preroll_;
        renderedFrames += count;
        return true;
    }
};
} // namespace nm::audio
