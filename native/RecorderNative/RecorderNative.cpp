#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <mmreg.h>
#include <ksmedia.h>
#include <functiondiscoverykeys_devpkey.h>
#include <endpointvolume.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Globalization.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Media.Ocr.h>
#include <winrt/Windows.Storage.Streams.h>

using Microsoft::WRL::ComPtr;

namespace {
constexpr int kAudioRate = 48000;
constexpr int kAudioChannels = 2;
constexpr int64_t kSecond = 10000000;

struct Failure {
  HRESULT result;
  std::wstring operation;
};

void Check(HRESULT result, const wchar_t* operation) {
  if (FAILED(result)) throw Failure{result, operation};
}

int64_t CounterTime() {
  LARGE_INTEGER now{}, frequency{};
  QueryPerformanceCounter(&now);
  QueryPerformanceFrequency(&frequency);
  return now.QuadPart / frequency.QuadPart * kSecond +
      now.QuadPart % frequency.QuadPart * kSecond / frequency.QuadPart;
}

struct AudioChunk {
  int64_t first = 0;
  std::vector<float> samples;
};

// Each endpoint retains its native format; resampling is local and never changes
// the user's playback or microphone device configuration.
struct Endpoint {
  ComPtr<IAudioClient> client;
  ComPtr<IAudioCaptureClient> capture;
  WAVEFORMATEX* format = nullptr;
  std::deque<AudioChunk> chunks;
  int64_t next_sample = -1;
  bool floating_point = false;

  ~Endpoint() {
    if (client) client->Stop();
    CoTaskMemFree(format);
  }

  void Open(IMMDeviceEnumerator* enumerator, bool loopback, const wchar_t* id = nullptr) {
    ComPtr<IMMDevice> device;
    if (id && *id) {
      Check(enumerator->GetDevice(id, &device), L"选择的音频设备不可用，请重新选择设备");
      ComPtr<IMMEndpoint> endpoint;
      Check(device.As(&endpoint), L"读取音频设备类型");
      EDataFlow flow;
      Check(endpoint->GetDataFlow(&flow), L"读取音频设备方向");
      if (flow != (loopback ? eRender : eCapture))
        throw Failure{E_INVALIDARG, L"选择的音频设备类型不匹配"};
    } else {
      Check(enumerator->GetDefaultAudioEndpoint(loopback ? eRender : eCapture,
          eConsole, &device), loopback ? L"系统声音设备不可用" : L"麦克风设备不可用");
    }
    Check(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
        reinterpret_cast<void**>(client.GetAddressOf())), L"打开音频设备");
    Check(client->GetMixFormat(&format), L"读取音频设备格式");
    floating_point = format->wFormatTag == WAVE_FORMAT_IEEE_FLOAT;
    bool pcm = format->wFormatTag == WAVE_FORMAT_PCM;
    if (format->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
        format->cbSize >= sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)) {
      auto extended = reinterpret_cast<WAVEFORMATEXTENSIBLE*>(format);
      floating_point = extended->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
      pcm = extended->SubFormat == KSDATAFORMAT_SUBTYPE_PCM;
    }
    if ((!floating_point && !pcm) || format->nChannels == 0 ||
        format->nSamplesPerSec < 8000 || format->nSamplesPerSec > 384000 ||
        (floating_point && format->wBitsPerSample != 32) ||
        (!floating_point && format->wBitsPerSample != 16 &&
          format->wBitsPerSample != 24 && format->wBitsPerSample != 32))
      throw Failure{AUDCLNT_E_UNSUPPORTED_FORMAT, L"音频设备格式不受支持"};
    Check(client->Initialize(AUDCLNT_SHAREMODE_SHARED,
        loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0,
        kSecond / 2, 0, format, nullptr), L"初始化音频捕获");
    Check(client->GetService(__uuidof(IAudioCaptureClient),
        reinterpret_cast<void**>(capture.GetAddressOf())), L"打开音频捕获服务");
  }

  float ReadValue(const BYTE* input) const {
    if (floating_point) {
      float result;
      std::memcpy(&result, input, sizeof result);
      return std::isfinite(result) ? std::clamp(result, -1.f, 1.f) : 0.f;
    }
    if (format->wBitsPerSample == 16) {
      int16_t result;
      std::memcpy(&result, input, sizeof result);
      return result / 32768.f;
    }
    if (format->wBitsPerSample == 24) {
      int32_t result = static_cast<int32_t>(input[0] | input[1] << 8 |
          input[2] << 16) << 8;
      return result / 2147483648.f;
    }
    int32_t result;
    std::memcpy(&result, input, sizeof result);
    return result / 2147483648.f;
  }

  void Drain(int64_t start, int64_t paused_time, int64_t committed) {
    UINT32 available = 0;
    Check(capture->GetNextPacketSize(&available), L"查询音频缓冲区");
    while (available != 0) {
      BYTE* data = nullptr;
      UINT32 frames = 0;
      DWORD flags = 0;
      UINT64 device_position = 0, qpc_position = 0;
      Check(capture->GetBuffer(&data, &frames, &flags, &device_position,
          &qpc_position), L"读取音频缓冲区");
      try {
        AudioChunk chunk;
        int64_t packet_time = (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0
            ? CounterTime() - static_cast<int64_t>(frames) * kSecond / format->nSamplesPerSec
            : static_cast<int64_t>(qpc_position);
        chunk.first = std::max<int64_t>(0,
            (packet_time - start - paused_time) * kAudioRate / kSecond);
        // Device timestamps can jitter by a packet. Preserve consecutive samples
        // while retaining real silence gaps when a loopback endpoint is idle.
        if (next_sample >= 0 && std::abs(chunk.first - next_sample) < kAudioRate / 50)
          chunk.first = next_sample;
        size_t output_frames = static_cast<size_t>(
            (static_cast<uint64_t>(frames) * kAudioRate + format->nSamplesPerSec / 2) /
            format->nSamplesPerSec);
        chunk.samples.resize(output_frames * kAudioChannels, 0.f);
        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != nullptr) {
          int bytes_per_sample = format->wBitsPerSample / 8;
          for (size_t out = 0; out < output_frames; ++out) {
            double source = static_cast<double>(out) * format->nSamplesPerSec / kAudioRate;
            UINT32 low = std::min(frames - 1, static_cast<UINT32>(source));
            UINT32 high = std::min(frames - 1, low + 1);
            float ratio = static_cast<float>(source - low);
            for (int channel = 0; channel < kAudioChannels; ++channel) {
              int input_channel = std::min(channel, static_cast<int>(format->nChannels) - 1);
              float first = ReadValue(data + low * format->nBlockAlign + input_channel * bytes_per_sample);
              float second = ReadValue(data + high * format->nBlockAlign + input_channel * bytes_per_sample);
              chunk.samples[out * kAudioChannels + channel] = first + (second - first) * ratio;
            }
          }
        }
        next_sample = chunk.first + static_cast<int64_t>(output_frames);
        if (next_sample > committed) chunks.push_back(std::move(chunk));
        // The writer drains on every video frame. Bound retained device data if
        // disk/encoder stalls, reporting the error rather than growing memory.
        if (!chunks.empty() && next_sample - chunks.front().first > kAudioRate * 30)
          throw Failure{HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW), L"音频写入落后超过 30 秒"};
      } catch (...) {
        capture->ReleaseBuffer(frames);
        throw;
      }
      Check(capture->ReleaseBuffer(frames), L"释放音频缓冲区");
      Check(capture->GetNextPacketSize(&available), L"查询音频缓冲区");
    }
  }
};

class Recorder {
 public:
  ComPtr<IMFSinkWriter> sink;
  DWORD video_stream = 0, audio_stream = 0;
  int width = 0, height = 0, fps = 0;
  std::wstring error;
  HRESULT last_result = S_OK;
  bool finalized = false;
  bool com_initialized = false, mf_initialized = false;
  int64_t start = 0, pause_start = 0, paused_time = 0;
  bool paused = true;
  int64_t committed_audio = 0;
  std::vector<std::unique_ptr<Endpoint>> endpoints;
  std::thread audio_thread;
  std::atomic<bool> stop_audio{false};
  std::mutex audio_mutex;

  ~Recorder() {
    StopAudio();
    if (!finalized && sink) sink->Finalize();
    sink.Reset();
    endpoints.clear();
    if (mf_initialized) MFShutdown();
    if (com_initialized) CoUninitialize();
  }

  void Initialize(const wchar_t* path, int w, int h, int rate, int quality, int mode,
      const wchar_t* system_id = nullptr, const wchar_t* microphone_id = nullptr) {
    if (!path || w < 2 || h < 2 || w > 16384 || h > 16384 || rate < 1 || rate > 60 || mode < 0 || mode > 3)
      throw Failure{E_INVALIDARG, L"录制参数无效"};
    width = (w + 1) & ~1; height = (h + 1) & ~1; fps = rate;
    HRESULT co = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(co) && co != RPC_E_CHANGED_MODE) Check(co, L"初始化 COM");
    com_initialized = SUCCEEDED(co);
    Check(MFStartup(MF_VERSION), L"初始化系统媒体组件");
    mf_initialized = true;
    ComPtr<IMFAttributes> attributes;
    Check(MFCreateAttributes(&attributes, 2), L"创建媒体属性");
    Check(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE), L"设置系统编码器");
    Check(attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE), L"设置编码节奏");
    Check(MFCreateSinkWriterFromURL(path, nullptr, attributes.Get(), &sink), L"创建 MP4 文件");

    ComPtr<IMFMediaType> output, input;
    Check(MFCreateMediaType(&output), L"创建 H.264 格式");
    Check(output->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), L"设置视频格式");
    Check(output->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264), L"设置 H.264 编码");
    uint64_t bitrate = static_cast<uint64_t>(width) * height * fps *
        static_cast<uint64_t>(std::clamp(quality, 30, 100)) / 300;
    Check(output->SetUINT32(MF_MT_AVG_BITRATE, static_cast<UINT32>(
        std::clamp<uint64_t>(bitrate, 300000, 40000000))), L"设置视频码率");
    Check(output->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), L"设置逐行扫描");
    Check(MFSetAttributeSize(output.Get(), MF_MT_FRAME_SIZE, width, height), L"设置视频尺寸");
    Check(MFSetAttributeRatio(output.Get(), MF_MT_FRAME_RATE, fps, 1), L"设置视频帧率");
    Check(MFSetAttributeRatio(output.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1), L"设置视频像素比例");
    Check(sink->AddStream(output.Get(), &video_stream), L"添加 H.264 视频轨道");
    Check(MFCreateMediaType(&input), L"创建视频输入格式");
    Check(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), L"设置视频输入类型");
    Check(input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32), L"设置 RGB 输入");
    Check(input->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), L"设置输入扫描方式");
    Check(input->SetUINT32(MF_MT_DEFAULT_STRIDE, width * 4), L"设置输入行宽");
    Check(MFSetAttributeSize(input.Get(), MF_MT_FRAME_SIZE, width, height), L"设置输入尺寸");
    Check(MFSetAttributeRatio(input.Get(), MF_MT_FRAME_RATE, fps, 1), L"设置输入帧率");
    Check(MFSetAttributeRatio(input.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1), L"设置输入像素比例");
    Check(sink->SetInputMediaType(video_stream, input.Get(), nullptr), L"连接系统 H.264 编码器");
    if (mode != 0) {
      ComPtr<IMMDeviceEnumerator> enumerator;
      Check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
          IID_PPV_ARGS(&enumerator)), L"枚举音频设备");
      if (mode == 1 || mode == 3) {
        auto endpoint = std::make_unique<Endpoint>();
        endpoint->Open(enumerator.Get(), true, system_id);
        endpoints.push_back(std::move(endpoint));
      }
      if (mode == 2 || mode == 3) {
        auto endpoint = std::make_unique<Endpoint>();
        endpoint->Open(enumerator.Get(), false, microphone_id);
        endpoints.push_back(std::move(endpoint));
      }
      output.Reset(); input.Reset();
      Check(MFCreateMediaType(&output), L"创建 AAC 格式");
      Check(output->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio), L"设置音频类型");
      Check(output->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC), L"设置 AAC 编码");
      Check(output->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, kAudioChannels), L"设置音频声道");
      Check(output->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, kAudioRate), L"设置音频采样率");
      Check(output->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16), L"设置音频位深");
      Check(output->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 20000), L"设置音频码率");
      Check(output->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0), L"设置 AAC 封装");
      Check(sink->AddStream(output.Get(), &audio_stream), L"添加 AAC 音轨");
      Check(MFCreateMediaType(&input), L"创建 PCM 输入格式");
      Check(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio), L"设置音频输入类型");
      Check(input->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM), L"设置 PCM 输入");
      Check(input->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, kAudioChannels), L"设置输入声道");
      Check(input->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, kAudioRate), L"设置输入采样率");
      Check(input->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16), L"设置输入位深");
      Check(input->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, kAudioChannels * 2), L"设置 PCM 对齐");
      Check(input->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, kAudioRate * kAudioChannels * 2), L"设置 PCM 速率");
      Check(sink->SetInputMediaType(audio_stream, input.Get(), nullptr), L"连接系统 AAC 编码器");
    }
    Check(sink->BeginWriting(), L"开始写入 MP4");
    start = CounterTime();
    pause_start = start;
    if (!endpoints.empty()) audio_thread = std::thread([this] { CaptureAudio(); });
  }

  void CaptureAudio() {
    HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    while (!stop_audio.load()) {
      {
        std::lock_guard<std::mutex> lock(audio_mutex);
        if (!paused) {
          try { for (auto& endpoint : endpoints) endpoint->Drain(start, paused_time, committed_audio); }
          catch (const Failure& failure) {
            last_result = failure.result;
            error = failure.operation;
            break;
          } catch (...) {
            last_result = E_FAIL;
            error = L"音频捕获失败";
            break;
          }
        }
      }
      Sleep(5);
    }
    if (SUCCEEDED(co)) CoUninitialize();
  }

  void SetPaused(bool value) {
    std::lock_guard<std::mutex> lock(audio_mutex);
    if (FAILED(last_result)) throw Failure{last_result, error};
    if (paused == value) return;
    if (value) {
      for (auto& endpoint : endpoints) {
        endpoint->Drain(start, paused_time, committed_audio);
        Check(endpoint->client->Stop(), L"暂停音频捕获");
        // Discard device buffers so the next segment starts at resume time.
        Check(endpoint->client->Reset(), L"清理暂停音频缓冲");
        endpoint->next_sample = -1;
      }
      pause_start = CounterTime();
    } else {
      paused_time += CounterTime() - pause_start;
      for (auto& endpoint : endpoints) Check(endpoint->client->Start(), L"开始音频捕获");
    }
    paused = value;
  }

  void StopAudio() {
    stop_audio.store(true);
    if (audio_thread.joinable()) audio_thread.join();
    for (auto& endpoint : endpoints) endpoint->client->Stop();
  }

  void WriteSample(DWORD stream, const BYTE* data, DWORD length, int64_t timestamp, int64_t duration) {
    ComPtr<IMFMediaBuffer> buffer;
    ComPtr<IMFSample> sample;
    Check(MFCreateMemoryBuffer(length, &buffer), L"分配媒体缓冲区");
    BYTE* destination = nullptr;
    Check(buffer->Lock(&destination, nullptr, nullptr), L"锁定媒体缓冲区");
    std::memcpy(destination, data, length);
    Check(buffer->Unlock(), L"解锁媒体缓冲区");
    Check(buffer->SetCurrentLength(length), L"设置媒体数据长度");
    Check(MFCreateSample(&sample), L"创建媒体样本");
    Check(sample->AddBuffer(buffer.Get()), L"添加媒体样本缓冲区");
    Check(sample->SetSampleTime(timestamp), L"设置媒体时间戳");
    Check(sample->SetSampleDuration(duration), L"设置媒体样本时长");
    Check(sink->WriteSample(stream, sample.Get()), L"写入媒体样本");
  }

  void FlushAudio(int64_t target) {
    if (endpoints.empty()) return;
    std::lock_guard<std::mutex> lock(audio_mutex);
    if (FAILED(last_result)) throw Failure{last_result, error};
    while (committed_audio < target) {
      int count = static_cast<int>(std::min<int64_t>(1024, target - committed_audio));
      std::vector<float> mixed(static_cast<size_t>(count) * kAudioChannels, 0.f);
      for (auto& endpoint : endpoints) {
        while (!endpoint->chunks.empty() && endpoint->chunks.front().first +
            static_cast<int64_t>(endpoint->chunks.front().samples.size() / kAudioChannels) <= committed_audio)
          endpoint->chunks.pop_front();
        for (const auto& chunk : endpoint->chunks) {
          int64_t first = std::max(committed_audio, chunk.first);
          int64_t last = std::min(committed_audio + count,
              chunk.first + static_cast<int64_t>(chunk.samples.size() / kAudioChannels));
          if (chunk.first >= committed_audio + count) break;
          for (int64_t frame = first; frame < last; ++frame)
            for (int channel = 0; channel < kAudioChannels; ++channel)
              mixed[static_cast<size_t>(frame - committed_audio) * kAudioChannels + channel] +=
                  chunk.samples[static_cast<size_t>(frame - chunk.first) * kAudioChannels + channel];
        }
      }
      std::vector<int16_t> pcm(mixed.size());
      // Mixing uses fixed headroom for two sources; one source retains its gain.
      float gain = endpoints.size() > 1 ? 0.5f : 1.f;
      for (size_t i = 0; i < mixed.size(); ++i)
        pcm[i] = static_cast<int16_t>(std::clamp(mixed[i] * gain, -1.f, 1.f) * 32767.f);
      int64_t begin = committed_audio * kSecond / kAudioRate;
      int64_t end = (committed_audio + count) * kSecond / kAudioRate;
      WriteSample(audio_stream, reinterpret_cast<const BYTE*>(pcm.data()),
          static_cast<DWORD>(pcm.size() * sizeof(int16_t)), begin, end - begin);
      committed_audio += count;
    }
  }

  void WriteVideo(const BYTE* pixels, int stride, int64_t frame) {
    if (!pixels || stride < width * 4 || frame < 0 || finalized)
      throw Failure{E_INVALIDARG, L"视频帧参数无效"};
    std::vector<BYTE> packed(static_cast<size_t>(width) * height * 4);
    for (int y = 0; y < height; ++y)
      std::memcpy(packed.data() + static_cast<size_t>(y) * width * 4,
          pixels + static_cast<size_t>(y) * stride, width * 4);
    int64_t begin = frame * kSecond / fps;
    int64_t end = (frame + 1) * kSecond / fps;
    WriteSample(video_stream, packed.data(), static_cast<DWORD>(packed.size()), begin, end - begin);
    // A short delay allows endpoint packets to arrive before committing silence.
    FlushAudio(std::max<int64_t>(0, begin - kSecond / 10) * kAudioRate / kSecond);
  }

  void Finish(int64_t duration) {
    if (finalized) return;
    if (!paused) SetPaused(true);
    StopAudio();
    FlushAudio(duration * kAudioRate / kSecond);
    HRESULT result = sink->Finalize();
    finalized = true;
    Check(result, L"完成 MP4 文件索引");
  }
};

thread_local std::wstring creation_error;

// Audio tests retain at most five seconds in the caller's memory. They create no
// media writer and never activate an endpoint until the explicit start export.
class AudioTest {
 public:
  bool com_initialized = false;
  int64_t start = 0, committed = 0;
  std::vector<std::unique_ptr<Endpoint>> endpoints;
  ~AudioTest() {
    endpoints.clear();
    if (com_initialized) CoUninitialize();
  }
  void Initialize(int mode, const wchar_t* system_id, const wchar_t* microphone_id) {
    if (mode < 1 || mode > 3) throw Failure{E_INVALIDARG, L"请选择要测试的声音来源"};
    HRESULT co = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(co) && co != RPC_E_CHANGED_MODE) Check(co, L"初始化音频测试");
    com_initialized = SUCCEEDED(co);
    ComPtr<IMMDeviceEnumerator> enumerator;
    Check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        IID_PPV_ARGS(&enumerator)), L"枚举音频测试设备");
    if (mode == 1 || mode == 3) {
      auto endpoint = std::make_unique<Endpoint>();
      endpoint->Open(enumerator.Get(), true, system_id);
      endpoints.push_back(std::move(endpoint));
    }
    if (mode == 2 || mode == 3) {
      auto endpoint = std::make_unique<Endpoint>();
      endpoint->Open(enumerator.Get(), false, microphone_id);
      endpoints.push_back(std::move(endpoint));
    }
    start = CounterTime();
    for (auto& endpoint : endpoints) Check(endpoint->client->Start(), L"开始声音测试");
  }
  void Poll(int16_t* destination, int capacity, int* samples, float* peak, int* completed) {
    if (!destination || capacity < 2 || !samples || !peak || !completed)
      throw Failure{E_INVALIDARG, L"声音测试缓冲区无效"};
    *samples = 0; *peak = 0; *completed = 0;
    for (auto& endpoint : endpoints) endpoint->Drain(start, 0, committed);
    // Keep the same packet arrival margin as the real recording path.
    int64_t elapsed = CounterTime() - start;
    int64_t target = std::min<int64_t>(kAudioRate * 5,
        std::max<int64_t>(0, elapsed - kSecond / 10) * kAudioRate / kSecond);
    int frames = static_cast<int>(std::min<int64_t>(capacity / kAudioChannels, target - committed));
    float gain = endpoints.size() > 1 ? 0.5f : 1.f;
    for (int frame = 0; frame < frames; ++frame) {
      for (int channel = 0; channel < kAudioChannels; ++channel) {
        float mixed = 0;
        for (auto& endpoint : endpoints) {
          for (const auto& chunk : endpoint->chunks) {
            int64_t index = committed + frame - chunk.first;
            if (index >= 0 && index < static_cast<int64_t>(chunk.samples.size() / kAudioChannels))
              mixed += chunk.samples[static_cast<size_t>(index) * kAudioChannels + channel];
          }
        }
        mixed = std::clamp(mixed * gain, -1.f, 1.f);
        *peak = std::max(*peak, std::abs(mixed));
        destination[frame * kAudioChannels + channel] = static_cast<int16_t>(mixed * 32767.f);
      }
    }
    committed += frames;
    *samples = frames * kAudioChannels;
    for (auto& endpoint : endpoints) {
      while (!endpoint->chunks.empty() && endpoint->chunks.front().first +
          static_cast<int64_t>(endpoint->chunks.front().samples.size() / kAudioChannels) <= committed)
        endpoint->chunks.pop_front();
    }
    if (committed >= kAudioRate * 5) {
      for (auto& endpoint : endpoints) endpoint->client->Stop();
      *completed = 1;
    }
  }
};

template<typename Operation>
HRESULT AudioGuard(Operation operation) {
  try { operation(); creation_error.clear(); return S_OK; }
  catch (const Failure& failure) { creation_error = failure.operation; return failure.result; }
  catch (...) { creation_error = L"声音设备操作失败"; return E_FAIL; }
}

template<typename Operation>
HRESULT Guard(Recorder* recorder, Operation operation) {
  if (!recorder) return E_POINTER;
  try { operation(); return S_OK; }
  catch (const Failure& failure) {
    std::lock_guard<std::mutex> lock(recorder->audio_mutex);
    recorder->last_result = failure.result;
    recorder->error = failure.operation;
    return failure.result;
  }
  catch (...) {
    std::lock_guard<std::mutex> lock(recorder->audio_mutex);
    recorder->error = L"媒体组件内部错误";
    return E_FAIL;
  }
}

std::string EscapeJson(const std::wstring& s) {
  std::ostringstream o;
  for (auto c : s) {
    if (c == L'"') o << "\\\"";
    else if (c == L'\\') o << "\\\\";
    else if (c == L'\b') o << "\\b";
    else if (c == L'\f') o << "\\f";
    else if (c == L'\n') o << "\\n";
    else if (c == L'\r') o << "\\r";
    else if (c == L'\t') o << "\\t";
    else if (c < 32) {
      char buf[7];
      snprintf(buf, sizeof(buf), "\\u%04x", c);
      o << buf;
    } else {
      char utf8[4] = {0};
      int len = WideCharToMultiByte(CP_UTF8, 0, &c, 1, utf8, sizeof(utf8), nullptr, nullptr);
      if (len > 0) o.write(utf8, len);
    }
  }
  return o.str();
}
}  // namespace

extern "C" {
__declspec(dllexport) HRESULT __cdecl RcEnumerateAudioDevices(int system,
    wchar_t* buffer, int capacity) {
  if (!buffer || capacity < 1) return E_INVALIDARG;
  buffer[0] = L'\0';
  HRESULT co = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
  if (FAILED(co) && co != RPC_E_CHANGED_MODE) return co;
  HRESULT result = AudioGuard([&] {
    ComPtr<IMMDeviceEnumerator> enumerator;
    Check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        IID_PPV_ARGS(&enumerator)), L"读取声音设备列表");
    ComPtr<IMMDeviceCollection> collection;
    Check(enumerator->EnumAudioEndpoints(system ? eRender : eCapture,
        DEVICE_STATE_ACTIVE, &collection), L"枚举声音设备列表");
    UINT count = 0;
    Check(collection->GetCount(&count), L"读取声音设备数量");
    std::wstring text;
    for (UINT index = 0; index < count; ++index) {
      ComPtr<IMMDevice> device;
      Check(collection->Item(index, &device), L"读取声音设备");
      LPWSTR id = nullptr;
      Check(device->GetId(&id), L"读取声音设备标识");
      std::wstring id_value(id);
      CoTaskMemFree(id);
      ComPtr<IPropertyStore> properties;
      Check(device->OpenPropertyStore(STGM_READ, &properties), L"读取声音设备名称");
      PROPVARIANT name;
      PropVariantInit(&name);
      HRESULT named = properties->GetValue(PKEY_Device_FriendlyName, &name);
      std::wstring title = SUCCEEDED(named) && name.vt == VT_LPWSTR && name.pwszVal
          ? name.pwszVal : L"音频设备";
      PropVariantClear(&name);
      for (auto& c : title) if (c == L'\r' || c == L'\n' || c == L'\t') c = L' ';
      text += id_value + L"\t" + title + L"\n";
    }
    if (text.size() + 1 > static_cast<size_t>(capacity))
      throw Failure{HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER), L"声音设备列表过长"};
    wcscpy_s(buffer, capacity, text.c_str());
  });
  if (SUCCEEDED(co)) CoUninitialize();
  return result;
}

__declspec(dllexport) HRESULT __cdecl RcAudioTestStart(int mode,
    const wchar_t* system_id, const wchar_t* microphone_id, void** handle) {
  if (!handle) return E_POINTER;
  *handle = nullptr;
  return AudioGuard([&] {
    auto test = std::make_unique<AudioTest>();
    test->Initialize(mode, system_id, microphone_id);
    *handle = test.release();
  });
}

__declspec(dllexport) HRESULT __cdecl RcAudioTestRead(void* handle,
    int16_t* destination, int capacity, int* samples, float* peak, int* completed) {
  if (!handle) return E_POINTER;
  return AudioGuard([&] { static_cast<AudioTest*>(handle)->Poll(destination,
      capacity, samples, peak, completed); });
}

__declspec(dllexport) void __cdecl RcAudioTestDestroy(void* handle) {
  delete static_cast<AudioTest*>(handle);
}

__declspec(dllexport) HRESULT __cdecl RcCreateWithDevices(const wchar_t* path,
    int width, int height, int fps, int quality, int mode,
    const wchar_t* system_id, const wchar_t* microphone_id, void** handle) {
  if (!handle) return E_POINTER;
  *handle = nullptr;
  return AudioGuard([&] {
    auto recorder = std::make_unique<Recorder>();
    recorder->Initialize(path, width, height, fps, quality, mode, system_id, microphone_id);
    *handle = recorder.release();
  });
}

__declspec(dllexport) HRESULT __cdecl RcCreate(const wchar_t* path, int width,
    int height, int fps, int quality, int mode, void** handle) {
  if (!handle) return E_POINTER;
  *handle = nullptr;
  try {
    auto recorder = std::make_unique<Recorder>();
    recorder->Initialize(path, width, height, fps, quality, mode);
    *handle = recorder.release();
    creation_error.clear();
    return S_OK;
  } catch (const Failure& failure) {
    creation_error = failure.operation;
    return failure.result;
  } catch (...) {
    creation_error = L"创建媒体组件失败";
    return E_FAIL;
  }
}

__declspec(dllexport) HRESULT __cdecl RcWriteVideo(void* handle, const BYTE* pixels,
    int stride, int64_t frame) {
  auto recorder = static_cast<Recorder*>(handle);
  return Guard(recorder, [&] { recorder->WriteVideo(pixels, stride, frame); });
}

__declspec(dllexport) HRESULT __cdecl RcSetPaused(void* handle, int paused) {
  auto recorder = static_cast<Recorder*>(handle);
  return Guard(recorder, [&] { recorder->SetPaused(paused != 0); });
}

__declspec(dllexport) HRESULT __cdecl RcFinish(void* handle, int64_t duration) {
  auto recorder = static_cast<Recorder*>(handle);
  return Guard(recorder, [&] { recorder->Finish(duration); });
}

__declspec(dllexport) void __cdecl RcGetError(void* handle, wchar_t* buffer, int capacity) {
  if (!buffer || capacity <= 0) return;
  auto recorder = static_cast<Recorder*>(handle);
  std::wstring message;
  if (recorder) {
    std::lock_guard<std::mutex> lock(recorder->audio_mutex);
    message = recorder->error;
  } else message = creation_error;
  wcsncpy_s(buffer, capacity, message.c_str(), _TRUNCATE);
}



__declspec(dllexport) void __cdecl RcDestroy(void* handle) {
  delete static_cast<Recorder*>(handle);
}

__declspec(dllexport) int __cdecl NativeOcr_IsAvailable() {
  try {
    winrt::init_apartment(winrt::apartment_type::single_threaded);
    auto engine = winrt::Windows::Media::Ocr::OcrEngine::TryCreateFromUserProfileLanguages();
    if (!engine) {
      engine = winrt::Windows::Media::Ocr::OcrEngine::TryCreateFromLanguage(
          winrt::Windows::Globalization::Language(L"zh-Hans-CN"));
    }
    if (!engine) {
      engine = winrt::Windows::Media::Ocr::OcrEngine::TryCreateFromLanguage(
          winrt::Windows::Globalization::Language(L"en-US"));
    }
    return engine ? 1 : 0;
  } catch (...) {
    return 0;
  }
}

__declspec(dllexport) int __cdecl NativeOcr_Recognize(
    const uint8_t* bgra_bytes, int width, int height, int stride, char* json_buffer, int max_buffer_size) {
  if (!bgra_bytes || width <= 0 || height <= 0 || stride < width * 4) {
    return -1;
  }
  try {
    winrt::init_apartment(winrt::apartment_type::single_threaded);
    auto engine = winrt::Windows::Media::Ocr::OcrEngine::TryCreateFromUserProfileLanguages();
    if (!engine) {
      engine = winrt::Windows::Media::Ocr::OcrEngine::TryCreateFromLanguage(
          winrt::Windows::Globalization::Language(L"zh-Hans-CN"));
    }
    if (!engine) {
      engine = winrt::Windows::Media::Ocr::OcrEngine::TryCreateFromLanguage(
          winrt::Windows::Globalization::Language(L"en-US"));
    }
    if (!engine) {
      return -2;
    }

    winrt::Windows::Storage::Streams::DataWriter writer;
    writer.WriteBytes(winrt::array_view<const uint8_t>(bgra_bytes, bgra_bytes + (static_cast<size_t>(height) * stride)));
    auto buffer = writer.DetachBuffer();

    auto software_bitmap = winrt::Windows::Graphics::Imaging::SoftwareBitmap::CreateCopyFromBuffer(
        buffer,
        winrt::Windows::Graphics::Imaging::BitmapPixelFormat::Bgra8,
        width,
        height,
        winrt::Windows::Graphics::Imaging::BitmapAlphaMode::Premultiplied);

    auto result = engine.RecognizeAsync(software_bitmap).get();

    std::ostringstream json;
    std::wstring lang = engine.RecognizerLanguage().LanguageTag().c_str();
    json << "{\"engine\":\"Windows.Media.Ocr\",\"language\":\"" << EscapeJson(lang) << "\",\"lines\":[";

    bool first_line = true;
    for (auto line : result.Lines()) {
      if (!first_line) json << ",";
      first_line = false;

      std::wstring line_text = line.Text().c_str();
      float min_x = 1e9f, min_y = 1e9f, max_x = 0.0f, max_y = 0.0f;
      bool has_words = false;

      std::ostringstream words_json;
      words_json << "[";
      bool first_word = true;
      for (auto word : line.Words()) {
        has_words = true;
        if (!first_word) words_json << ",";
        first_word = false;

        auto rect = word.BoundingRect();
        min_x = (std::min)(min_x, rect.X);
        min_y = (std::min)(min_y, rect.Y);
        max_x = (std::max)(max_x, rect.X + rect.Width);
        max_y = (std::max)(max_y, rect.Y + rect.Height);

        words_json << "{\"text\":\"" << EscapeJson(word.Text().c_str()) << "\","
                   << "\"x\":" << rect.X << ",\"y\":" << rect.Y << ","
                   << "\"w\":" << rect.Width << ",\"h\":" << rect.Height << "}";
      }
      words_json << "]";

      if (!has_words) {
        min_x = 0; min_y = 0; max_x = 0; max_y = 0;
      }

      json << "{\"text\":\"" << EscapeJson(line_text) << "\","
           << "\"x\":" << min_x << ",\"y\":" << min_y << ","
           << "\"w\":" << (max_x - min_x) << ",\"h\":" << (max_y - min_y) << ","
           << "\"words\":" << words_json.str() << "}";
    }
    json << "]}";

    std::string out_str = json.str();
    if (!json_buffer || max_buffer_size <= 0) {
      return static_cast<int>(out_str.size() + 1);
    }
    if (static_cast<int>(out_str.size()) >= max_buffer_size) {
      return static_cast<int>(out_str.size() + 1);
    }

    memcpy(json_buffer, out_str.c_str(), out_str.size() + 1);
    return static_cast<int>(out_str.size());
  } catch (...) {
    return -3;
  }
}

__declspec(dllexport) float __cdecl NativeAudio_GetInputPeakLevel(int device_index) {
  try {
    ComPtr<IMMDeviceEnumerator> enumerator;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                  __uuidof(IMMDeviceEnumerator), (void**)&enumerator);
    if (FAILED(hr) || !enumerator) return 0.0f;

    ComPtr<IMMDeviceCollection> collection;
    hr = enumerator->EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, &collection);
    if (FAILED(hr) || !collection) return 0.0f;

    UINT count = 0;
    collection->GetCount(&count);
    if (count == 0) return 0.0f;

    ComPtr<IMMDevice> device;
    if (device_index < 0 || static_cast<UINT>(device_index) >= count) {
      enumerator->GetDefaultAudioEndpoint(eCapture, eConsole, &device);
    } else {
      collection->Item(device_index, &device);
    }
    if (!device) return 0.0f;

    ComPtr<IAudioMeterInformation> meter;
    hr = device->Activate(__uuidof(IAudioMeterInformation), CLSCTX_ALL, nullptr, (void**)&meter);
    if (FAILED(hr) || !meter) return 0.0f;

    float peak = 0.0f;
    meter->GetPeakValue(&peak);
    return peak;
  } catch (...) {
    return 0.0f;
  }
}
}
