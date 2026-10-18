#pragma once
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <string>

namespace nm::video {
// Bounded SDR conversion into the ATEM's fixed 1080p output. Matching UYVY is
// copied exactly; other dimensions use nearest-neighbour sampling. No allocation.
inline void convert(const uint8_t* input, int width, int height, int stride, bool bgra,
                    uint8_t* output, int outputStride, const std::string& sizing) noexcept {
    if (!bgra && width == 1920 && height == 1080) {
        for (int y = 0; y < 1080; ++y) memcpy(output + y * outputStride, input + y * stride, 3840);
        return;
    }
    const double sx = width / 1920.0, sy = height / 1080.0;
    const double scale = sizing == "fill" ? std::min(sx, sy) : std::max(sx, sy);
    const double xScale = sizing == "stretch" ? sx : scale, yScale = sizing == "stretch" ? sy : scale;
    const double xOffset = (width - 1920 * xScale) / 2, yOffset = (height - 1080 * yScale) / 2;
    auto pixel = [&](int x, int y, int& luma, int& u, int& v) {
        if (x < 0 || x >= width || y < 0 || y >= height) { luma = 16; u = v = 128; return; }
        const auto p = input + y * stride;
        if (!bgra) { const auto pair = p + (x & ~1) * 2; u = pair[0]; v = pair[2]; luma = pair[(x & 1) ? 3 : 1]; }
        else {
            const auto rgb = p + x * 4; const int b = rgb[0], g = rgb[1], r = rgb[2];
            luma = std::clamp(16 + ((47*r + 157*g + 16*b + 128) >> 8), 16, 235);
            u = std::clamp(128 + ((-26*r - 87*g + 112*b + 128) >> 8), 16, 240);
            v = std::clamp(128 + ((112*r - 102*g - 10*b + 128) >> 8), 16, 240);
        }
    };
    for (int y = 0; y < 1080; ++y) {
        auto dst = output + y * outputStride; const int iy = static_cast<int>(std::floor((y+.5)*yScale+yOffset));
        for (int x = 0; x < 1920; x += 2) {
            int y0,u0,v0,y1,u1,v1;
            pixel(static_cast<int>(std::floor((x+.5)*xScale+xOffset)), iy, y0,u0,v0);
            pixel(static_cast<int>(std::floor((x+1.5)*xScale+xOffset)), iy, y1,u1,v1);
            dst[x*2] = static_cast<uint8_t>((u0+u1)/2); dst[x*2+1] = static_cast<uint8_t>(y0);
            dst[x*2+2] = static_cast<uint8_t>((v0+v1)/2); dst[x*2+3] = static_cast<uint8_t>(y1);
        }
    }
}
}
