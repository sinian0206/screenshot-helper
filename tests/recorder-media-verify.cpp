#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

static void Check(HRESULT hr) {
  if (FAILED(hr)) { std::printf("{\"error\":\"0x%08lx\"}\n", hr); std::fflush(stdout); ExitProcess(1); }
}

static double ToneLevel(const std::vector<float>& values, double frequency, UINT32 sample_rate) {
  long double power = 0;
  int windows = 0;
  constexpr size_t kWindow = 4096;
  constexpr double kPi = 3.14159265358979323846;
  for (size_t start = 0; start + kWindow <= values.size(); start += kWindow) {
    long double real = 0, imaginary = 0;
    for (size_t i = 0; i < kWindow; ++i) {
      double phase = 2 * kPi * frequency * static_cast<double>(i) / sample_rate;
      real += values[start + i] * std::cos(phase);
      imaginary += values[start + i] * std::sin(phase);
    }
    power += (real * real + imaginary * imaginary) * 4 / (kWindow * kWindow);
    ++windows;
  }
  return windows > 0 ? static_cast<double>(std::sqrt(power / windows)) : 0;
}

int wmain(int argc, wchar_t** argv) {
  if (argc != 2) return 2;
  Check(CoInitializeEx(nullptr, COINIT_MULTITHREADED));
  Check(MFStartup(MF_VERSION));
  ComPtr<IMFSourceReader> reader;
  ComPtr<IMFAttributes> options;
  Check(MFCreateAttributes(&options, 1));
  Check(options->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE));
  Check(MFCreateSourceReaderFromURL(argv[1], options.Get(), &reader));
  int video_index = -1, audio_index = -1;
  bool h264 = false, aac = false;
  UINT32 width = 0, height = 0;
  for (DWORD stream = 0; stream < 16; ++stream) {
    ComPtr<IMFMediaType> type;
    if (FAILED(reader->GetNativeMediaType(stream, 0, &type))) break;
    GUID major{}, subtype{};
    Check(type->GetGUID(MF_MT_MAJOR_TYPE, &major));
    Check(type->GetGUID(MF_MT_SUBTYPE, &subtype));
    if (major == MFMediaType_Video) {
      video_index = static_cast<int>(stream);
      h264 = subtype == MFVideoFormat_H264;
      Check(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height));
    }
    if (major == MFMediaType_Audio) {
      audio_index = static_cast<int>(stream);
      aac = subtype == MFAudioFormat_AAC;
    }
  }
  if (video_index < 0) return 3;
  ComPtr<IMFMediaType> decoded;
  Check(MFCreateMediaType(&decoded));
  Check(decoded->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
  Check(decoded->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32));
  Check(reader->SetCurrentMediaType(video_index, nullptr, decoded.Get()));
  decoded.Reset();
  Check(reader->GetCurrentMediaType(video_index, &decoded));
  UINT32 video_stride_unsigned = width * 4;
  decoded->GetUINT32(MF_MT_DEFAULT_STRIDE, &video_stride_unsigned);
  LONG video_stride = static_cast<LONG>(video_stride_unsigned);
  int64_t frames = 0, video_end = 0, audio_end = 0, audio_values = 0;
  long double square_sum = 0;
  std::vector<float> mono;
  UINT32 audio_channels = 2, audio_rate = 48000;
  int top_red = -1, bottom_red = -1, edge_green = -1;
  DWORD video_bytes = 0;
  for (;;) {
    DWORD actual = 0, flags = 0;
    LONGLONG timestamp = 0;
    ComPtr<IMFSample> sample;
    Check(reader->ReadSample(video_index, 0, &actual, &flags, &timestamp, &sample));
    if (sample) {
      LONGLONG duration = 0;
      Check(sample->GetSampleDuration(&duration));
      video_end = std::max<int64_t>(video_end, timestamp + duration);
      ++frames;
      if (frames == 1) {
        ComPtr<IMFMediaBuffer> buffer;
        Check(sample->ConvertToContiguousBuffer(&buffer));
        BYTE* data = nullptr;
        DWORD length = 0;
        ComPtr<IMF2DBuffer> surface;
        bool two_dimensional = SUCCEEDED(buffer.As(&surface));
        if (two_dimensional) Check(surface->Lock2D(&data, &video_stride));
        else Check(buffer->Lock(&data, nullptr, &length));
        video_bytes = length;
        decoded.Reset();
        Check(reader->GetCurrentMediaType(video_index, &decoded));
        UINT32 fresh_stride = 0;
        if (!two_dimensional && SUCCEEDED(decoded->GetUINT32(MF_MT_DEFAULT_STRIDE, &fresh_stride)))
          video_stride = static_cast<LONG>(fresh_stride);
        // Hardware/decoder surfaces may pad rows beyond the media type stride.
        if ((two_dimensional || length >= width * height * 4) && width > 0 && height > 0) {
          BYTE* first_row = two_dimensional || video_stride >= 0 ? data : data + (height - 1) * (-video_stride);
          top_red = *(first_row + (height / 4) * video_stride + width / 2 * 4 + 2);
          bottom_red = *(first_row + (height * 3 / 4) * video_stride + width / 2 * 4 + 2);
          edge_green = *(first_row + (height - 2) * video_stride + (width - 2) * 4 + 1);
          BITMAPFILEHEADER file_header{};
          BITMAPINFOHEADER image_header{};
          file_header.bfType = 0x4d42;
          file_header.bfOffBits = sizeof file_header + sizeof image_header;
          file_header.bfSize = file_header.bfOffBits + width * height * 4;
          image_header.biSize = sizeof image_header;
          image_header.biWidth = static_cast<LONG>(width);
          image_header.biHeight = -static_cast<LONG>(height);
          image_header.biPlanes = 1;
          image_header.biBitCount = 32;
          image_header.biCompression = BI_RGB;
          std::wstring bitmap_path = std::wstring(argv[1]) + L".bmp";
          HANDLE image = CreateFileW(bitmap_path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
          if (image != INVALID_HANDLE_VALUE) {
            DWORD written = 0;
            WriteFile(image, &file_header, sizeof file_header, &written, nullptr);
            WriteFile(image, &image_header, sizeof image_header, &written, nullptr);
            for (UINT32 row = 0; row < height; ++row)
              WriteFile(image, first_row + row * video_stride, width * 4, &written, nullptr);
            CloseHandle(image);
          }
        }
        if (two_dimensional) Check(surface->Unlock2D());
        else Check(buffer->Unlock());
      }
    }
    if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
  }
  if (audio_index >= 0) {
    PROPVARIANT position;
    PropVariantInit(&position);
    position.vt = VT_I8;
    position.hVal.QuadPart = 0;
    Check(reader->SetCurrentPosition(GUID_NULL, position));
    decoded.Reset();
    Check(MFCreateMediaType(&decoded));
    Check(decoded->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
    Check(decoded->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM));
    Check(decoded->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
    Check(reader->SetCurrentMediaType(audio_index, nullptr, decoded.Get()));
    decoded.Reset();
    Check(reader->GetCurrentMediaType(audio_index, &decoded));
    Check(decoded->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &audio_channels));
    Check(decoded->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &audio_rate));
    for (;;) {
      DWORD actual = 0, flags = 0;
      LONGLONG timestamp = 0;
      ComPtr<IMFSample> sample;
      Check(reader->ReadSample(audio_index, 0, &actual, &flags, &timestamp, &sample));
      if (sample) {
        LONGLONG duration = 0;
        Check(sample->GetSampleDuration(&duration));
        audio_end = std::max<int64_t>(audio_end, timestamp + duration);
        ComPtr<IMFMediaBuffer> buffer;
        Check(sample->ConvertToContiguousBuffer(&buffer));
        BYTE* data = nullptr;
        DWORD length = 0;
        Check(buffer->Lock(&data, nullptr, &length));
        for (DWORD i = 0; i + 1 < length; i += 2) {
          int16_t value;
          std::memcpy(&value, data + i, sizeof value);
          long double normalized = value / 32768.0L;
          square_sum += normalized * normalized;
          if (audio_values % audio_channels == 0) mono.push_back(static_cast<float>(normalized));
          ++audio_values;
        }
        Check(buffer->Unlock());
      }
      if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
    }
  }
  std::printf("{\"h264\":%s,\"aac\":%s,\"width\":%u,\"height\":%u,\"frames\":%lld,"
      "\"videoSeconds\":%.6f,\"audioSeconds\":%.6f,\"audioValues\":%lld,\"audioRms\":%.6f,"
      "\"topRed\":%d,\"bottomRed\":%d,\"edgeGreen\":%d,\"stride\":%ld,\"videoBytes\":%lu,"
      "\"tone440\":%.6f,\"tone880\":%.6f}\n",
      h264 ? "true" : "false", aac ? "true" : "false", width, height, frames,
      video_end / 10000000.0, audio_end / 10000000.0, audio_values,
      audio_values > 0 ? static_cast<double>(std::sqrt(square_sum / audio_values)) : 0,
      top_red, bottom_red, edge_green, video_stride, video_bytes,
      ToneLevel(mono, 440, audio_rate), ToneLevel(mono, 880, audio_rate));
  reader.Reset();
  MFShutdown();
  CoUninitialize();
  return 0;
}
