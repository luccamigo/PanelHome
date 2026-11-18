/*++

Module Name:

    PanelFrameDiagnostics.cpp

Abstract:

    Stride-aware first-frame diagnostics shared by the driver and native tests.

--*/

#include "PanelFrameDiagnostics.h"

#include <array>
#include <limits>

namespace PanelOS
{
    namespace
    {
        constexpr ULONG DiagnosticGridWidth = 16;
        constexpr ULONG DiagnosticGridHeight = 16;

        const std::array<ULONG, 256>& Crc32Table()
        {
            static const std::array<ULONG, 256> table = []
            {
                std::array<ULONG, 256> values = {};
                for (ULONG index = 0; index < values.size(); ++index)
                {
                    ULONG value = index;
                    for (ULONG bit = 0; bit < 8; ++bit)
                    {
                        const ULONG mask = 0u - (value & 1u);
                        value = (value >> 1) ^ (0xEDB88320u & mask);
                    }
                    values[index] = value;
                }
                return values;
            }();
            return table;
        }

        bool TryReadPixel(ULONG Width,
                          ULONG Height,
                          ULONG Stride,
                          PANEL_FRAME_FORMAT Format,
                          const BYTE* Pixels,
                          size_t PayloadBytes,
                          ULONG X,
                          ULONG Y,
                          FramePixelSample& Sample)
        {
            if (Pixels == nullptr || X >= Width || Y >= Height)
            {
                return false;
            }

            const ULONGLONG offset =
                static_cast<ULONGLONG>(Y) * Stride + static_cast<ULONGLONG>(X) * 4u;
            if (offset > PayloadBytes || (PayloadBytes - static_cast<size_t>(offset)) < 4u)
            {
                return false;
            }

            const BYTE* pixel = Pixels + static_cast<size_t>(offset);
            if (Format == PanelFrameFormatBgra8888)
            {
                Sample.Blue = pixel[0];
                Sample.Green = pixel[1];
                Sample.Red = pixel[2];
                Sample.Alpha = pixel[3];
                return true;
            }

            if (Format == PanelFrameFormatRgba8888)
            {
                Sample.Red = pixel[0];
                Sample.Green = pixel[1];
                Sample.Blue = pixel[2];
                Sample.Alpha = pixel[3];
                return true;
            }

            return false;
        }
    }

    ULONG ComputeFrameCrc32(const BYTE* Bytes, size_t ByteCount)
    {
        if (Bytes == nullptr && ByteCount != 0)
        {
            return 0;
        }

        const auto& table = Crc32Table();
        ULONG crc = 0xFFFFFFFFu;
        for (size_t index = 0; index < ByteCount; ++index)
        {
            const BYTE tableIndex = static_cast<BYTE>((crc ^ Bytes[index]) & 0xFFu);
            crc = (crc >> 8) ^ table[tableIndex];
        }

        return ~crc;
    }

    bool TryAnalyzeFrame(ULONG Width,
                         ULONG Height,
                         ULONG Stride,
                         PANEL_FRAME_FORMAT Format,
                         const BYTE* Pixels,
                         size_t PayloadBytes,
                         FrameDiagnosticSummary& Summary)
    {
        Summary = {};

        if (Width == 0 || Height == 0 || Pixels == nullptr ||
            (Format != PanelFrameFormatBgra8888 && Format != PanelFrameFormatRgba8888))
        {
            return false;
        }

        const ULONGLONG minimumStride = static_cast<ULONGLONG>(Width) * 4u;
        const ULONGLONG requiredBytes = static_cast<ULONGLONG>(Stride) * Height;
        if (Stride < minimumStride || requiredBytes != PayloadBytes ||
            requiredBytes > (std::numeric_limits<size_t>::max)())
        {
            return false;
        }

        Summary.Crc32 = ComputeFrameCrc32(Pixels, PayloadBytes);

        if (!TryReadPixel(Width, Height, Stride, Format, Pixels, PayloadBytes,
                          0, 0, Summary.TopLeft) ||
            !TryReadPixel(Width, Height, Stride, Format, Pixels, PayloadBytes,
                          Width - 1, 0, Summary.TopRight) ||
            !TryReadPixel(Width, Height, Stride, Format, Pixels, PayloadBytes,
                          Width / 2, Height / 2, Summary.Center) ||
            !TryReadPixel(Width, Height, Stride, Format, Pixels, PayloadBytes,
                          0, Height - 1, Summary.BottomLeft) ||
            !TryReadPixel(Width, Height, Stride, Format, Pixels, PayloadBytes,
                          Width - 1, Height - 1, Summary.BottomRight))
        {
            Summary = {};
            return false;
        }

        ULONG minimum = 255;
        ULONG maximum = 0;
        ULONGLONG total = 0;
        ULONG nonBlack = 0;
        ULONG sampleCount = 0;

        for (ULONG row = 0; row < DiagnosticGridHeight; ++row)
        {
            const ULONG y = Height == 1
                ? 0
                : static_cast<ULONG>((static_cast<ULONGLONG>(row) * (Height - 1)) /
                                     (DiagnosticGridHeight - 1));

            for (ULONG column = 0; column < DiagnosticGridWidth; ++column)
            {
                const ULONG x = Width == 1
                    ? 0
                    : static_cast<ULONG>((static_cast<ULONGLONG>(column) * (Width - 1)) /
                                         (DiagnosticGridWidth - 1));

                FramePixelSample sample = {};
                if (!TryReadPixel(Width, Height, Stride, Format, Pixels, PayloadBytes,
                                  x, y, sample))
                {
                    Summary = {};
                    return false;
                }

                const ULONG luminance =
                    (static_cast<ULONG>(sample.Red) + sample.Green + sample.Blue) / 3u;
                minimum = luminance < minimum ? luminance : minimum;
                maximum = luminance > maximum ? luminance : maximum;
                total += luminance;
                if (sample.Red != 0 || sample.Green != 0 || sample.Blue != 0)
                {
                    ++nonBlack;
                }
                ++sampleCount;
            }
        }

        Summary.Luminance.Minimum = minimum;
        Summary.Luminance.Maximum = maximum;
        Summary.Luminance.Average = sampleCount == 0
            ? 0
            : static_cast<ULONG>(total / sampleCount);
        Summary.Luminance.NonBlackSamples = nonBlack;
        Summary.Luminance.SampleCount = sampleCount;
        return true;
    }
}
