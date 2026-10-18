#pragma once
#include "win_support.h"
#include <array>
#include <bit>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <string_view>

namespace nm::audio
{
// Windows x64 ABI, version 1. Every concurrently accessed word is accessed using
// Windows Interlocked operations, including packed float data. No non-atomic
// seqlock payload races. One writer; any number of independent, local readers.
inline constexpr int64_t abi = 2;
inline constexpr uint64_t capacity = 32768; // 683 ms at 48 kHz, never grows.
inline constexpr wchar_t default_mapping[] = L"Local\\NDIMachine.Audio.USB.v2";
inline int64_t load(const volatile LONG64 &value) noexcept
{
    return InterlockedCompareExchange64(const_cast<volatile LONG64 *>(&value), 0, 0);
}
inline void save(volatile LONG64 &value, int64_t next) noexcept
{
    InterlockedExchange64(&value, next);
}
struct Stereo
{
    float left{}, right{};
};
static_assert(sizeof(Stereo) == 8);
struct alignas(16) Cell
{
    volatile LONG64 sequence{}, samples{};
};
struct alignas(64) Shared
{
    volatile LONG64 version{}, generation{}, state{}, published{}, lastPacketQpc{}, frequency{},
        discontinuities{}, timestampErrors{}, nonfinite{}, sampleRate{}, channels{}, endpointLength{};
    volatile LONG64 endpoint[64]{};
    Cell cells[capacity];
};
static_assert(sizeof(LONG64) == 8);
class Mapping
{
    WinHandle handle_;
    Shared *data_{};

  public:
    explicit Mapping(bool writer, const wchar_t *name = default_mapping)
        : handle_(writer ? CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                                              sizeof(Shared), name)
                         : OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name))
    {
        // Interlocked reads require a writable mapping, but only the host publishes.
        if (!handle_.value)
            throw std::runtime_error("Audio capture host mapping unavailable");
        data_ = static_cast<Shared *>(MapViewOfFile(handle_, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Shared)));
        if (!data_)
            throw std::runtime_error("Cannot map audio shared buffer");
        if (!writer && load(data_->version) != abi)
        {
            UnmapViewOfFile(data_);
            data_ = nullptr;
            throw std::runtime_error("Audio shared buffer ABI mismatch");
        }
    }
    ~Mapping()
    {
        if (data_)
            UnmapViewOfFile(data_);
    }
    Mapping(const Mapping &) = delete;
    Shared &get()
    {
        return *data_;
    }
};
class Writer
{
    Shared &shared_;
    uint64_t position_{};

  public:
    explicit Writer(Shared &shared, std::string_view endpoint = {}) : shared_(shared)
    {
        if (endpoint.size() >= sizeof(shared_.endpoint))
            throw std::runtime_error("Endpoint ID exceeds shared ABI bound");
        save(shared_.state, 0);
        save(shared_.version, abi);
        FILETIME time{};
        GetSystemTimePreciseAsFileTime(&time);
        const auto generation = (static_cast<uint64_t>(time.dwHighDateTime) << 32) | time.dwLowDateTime;
        save(shared_.generation, static_cast<int64_t>(generation));
        std::array<char, sizeof(shared_.endpoint)> normalized{};
        for (size_t i = 0; i < endpoint.size(); ++i)
            normalized[i] = endpoint[i] >= 'A' && endpoint[i] <= 'Z' ? endpoint[i] + 32 : endpoint[i];
        for (size_t i = 0; i < 64; ++i)
        {
            int64_t word{};
            memcpy(&word, normalized.data() + i * 8, 8);
            save(shared_.endpoint[i], word);
        }
        save(shared_.endpointLength, static_cast<int64_t>(endpoint.size()));
        save(shared_.published, 0);
        save(shared_.lastPacketQpc, 0);
        save(shared_.frequency, qpc_frequency());
        save(shared_.sampleRate, 48000);
        save(shared_.channels, 2);
        save(shared_.discontinuities, 0);
        save(shared_.timestampErrors, 0);
        save(shared_.nonfinite, 0);
    }
    ~Writer()
    {
        save(shared_.state, 0);
    }
    void push(Stereo sample) noexcept
    {
        if (!std::isfinite(sample.left))
        {
            sample.left = 0;
            InterlockedIncrement64(&shared_.nonfinite);
        }
        if (!std::isfinite(sample.right))
        {
            sample.right = 0;
            InterlockedIncrement64(&shared_.nonfinite);
        }
        auto &cell = shared_.cells[position_ % capacity];
        const auto sequence = static_cast<int64_t>((position_ + 1) * 2);
        save(cell.sequence, sequence - 1);
        save(cell.samples, std::bit_cast<int64_t>(sample));
        save(cell.sequence, sequence);
        ++position_;
    }
    void publish(int64_t timestamp) noexcept
    {
        save(shared_.published, static_cast<int64_t>(position_));
        save(shared_.lastPacketQpc, timestamp);
        save(shared_.state, 1);
    }
};
inline bool read(const Shared &shared, uint64_t position, Stereo &sample) noexcept
{
    const auto &cell = shared.cells[position % capacity];
    const auto expected = static_cast<int64_t>((position + 1) * 2);
    if (load(cell.sequence) != expected)
        return false;
    const auto bits = load(cell.samples);
    if (load(cell.sequence) != expected)
        return false;
    sample = std::bit_cast<Stereo>(bits);
    return true;
}
inline bool fresh(const Shared &shared, int64_t now) noexcept
{
    const auto last = load(shared.lastPacketQpc), frequency = load(shared.frequency);
    return load(shared.state) == 1 && frequency > 0 && last > 0 && now >= last && now - last < frequency / 2;
}
} // namespace nm::audio
