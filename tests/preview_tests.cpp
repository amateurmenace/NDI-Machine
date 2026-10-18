#include "preview.h"
#include <iostream>
#include <stdexcept>
#include <vector>
static void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
int main() {
    using namespace nm::preview;
    uint8_t out[4]{};
    pixel(16, 128, 128, out); require(out[0] == 0 && out[1] == 0 && out[2] == 0 && out[3] == 255, "Limited-range black");
    pixel(235, 128, 128, out); require(out[0] == 255 && out[1] == 255 && out[2] == 255, "Limited-range white");
    pixel(63, 102, 240, out); require(out[2] > 250 && out[1] < 3 && out[0] < 3, "Rec.709 red");
    constexpr int paddedStride = 3904;
    std::vector<uint8_t> input(paddedStride * 1080, 77), output(stride * height + 16, 0xab);
    for (int y = 0; y < 1080; ++y) for (int x = 0; x < 1920; x += 2) {
        auto p = &input[y * paddedStride + x * 2]; p[0] = 128; p[1] = y < 540 ? 16 : 235; p[2] = 128; p[3] = p[1];
    }
    downscale(input.data(), paddedStride, output.data());
    require(output[0] == 0 && output[90 * stride] == 255, "Downsample honors source row stride and location");
    for (size_t i = stride * height; i < output.size(); ++i) require(output[i] == 0xab, "Preview exceeded bounded output");
    const auto name = L"Local\\NDIMachine.Preview.test." + std::to_wstring(GetCurrentProcessId());
    Writer writer(name);
    nm::WinHandle mapping(OpenFileMappingW(FILE_MAP_READ, FALSE, name.c_str()));
    auto shared = static_cast<const Shared*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, sizeof(Shared)));
    require(shared != nullptr, "Preview mapping missing");
    writer.publish(input.data(), paddedStride, true, 7);
    require(shared->sequence == 1 && shared->revision == 7 && shared->good == 1, "Preview metadata missing");
    for (int n = 0; n < 10; ++n) writer.publish(input.data(), paddedStride, true, 8);
    require(shared->sequence == 1, "Preview exceeded rate limit");
    UnmapViewOfFile(shared);
    std::cout << "PASS: preview Rec.709, row stride, buffer bound, metadata and 4fps limit. No NDI receiver or SDI output.\n";
}
