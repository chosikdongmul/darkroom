using System.Numerics;
using Darkroom.Core;
using NAudio.Wave;

namespace Darkroom.Providers;

/// <summary>
/// audio 네임스페이스 — 스피커로 나가는 소리(WASAPI 루프백)를 주파수 막대로. 구독하는 위젯이 있을 때만 캡처한다.
///   audio.spectrum = [32개, 0~1] 낮은 음(40Hz) → 높은 음(16kHz), 약 20fps.
///   소리가 없으면 0으로 내려간 뒤 같은 값이라 Data Bus가 더 보내지 않는다(조용할 때 CPU를 쓰지 않음).
/// </summary>
public sealed class AudioProvider : IDataProvider
{
    public const int Bands = 32;
    const int N = 2048;
    const double MinHz = 40, MaxHz = 16000, FloorDb = -62;

    public string Namespace => "audio";
    public TimeSpan Interval => TimeSpan.FromMilliseconds(50);

    readonly object _lock = new();
    readonly float[] _ring = new float[N];
    int _pos;
    long _lastDataTicks;
    WasapiLoopbackCapture? _capture;
    WaveFormat? _format;
    DateTime _lastAttempt = DateTime.MinValue;
    readonly double[] _level = new double[Bands];
    static readonly double[] Window = Enumerable.Range(0, N).Select(i => 0.5 * (1 - Math.Cos(2 * Math.PI * i / (N - 1)))).ToArray();

    public Task UpdateAsync(IDataBus bus, CancellationToken ct)
    {
        EnsureCapture(ct);
        var samples = new float[N];
        bool fresh;
        lock (_lock)
        {
            for (int i = 0; i < N; i++) samples[i] = _ring[(_pos + i) % N];
            // 루프백은 아무것도 재생하지 않으면 데이터를 보내지 않는다 → 마지막 소리가 남지 않게 0으로 본다
            fresh = Environment.TickCount64 - _lastDataTicks < 250;
        }
        var bands = fresh ? Spectrum(samples, _format?.SampleRate ?? 48000) : new double[Bands];
        var output = new double[Bands];
        for (int i = 0; i < Bands; i++)
        {
            // 올라갈 땐 바로, 내려갈 땐 천천히(막대가 떨리지 않게)
            _level[i] = bands[i] >= _level[i] ? bands[i] : Math.Max(bands[i], _level[i] * 0.82 - 0.01);
            output[i] = _level[i] < 0.02 ? 0 : Math.Round(_level[i], 2);
        }
        bus.Publish("audio.spectrum", output);
        return Task.CompletedTask;
    }

    void EnsureCapture(CancellationToken ct)
    {
        if (_capture != null || DateTime.UtcNow - _lastAttempt < TimeSpan.FromSeconds(3)) return;
        _lastAttempt = DateTime.UtcNow;
        try
        {
            var cap = new WasapiLoopbackCapture();
            _format = cap.WaveFormat;
            cap.DataAvailable += OnData;
            cap.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Log.Warn($"소리 캡처 멈춤: {e.Exception.Message}");
                lock (_lock) { if (_capture == cap) _capture = null; }
                cap.Dispose();
            };
            cap.StartRecording();
            lock (_lock) _capture = cap;
            ct.Register(() => { try { cap.StopRecording(); } catch (Exception) { /* 이미 멈춤 */ } });
            Log.Info($"소리 캡처 시작 {_format.SampleRate}Hz {_format.Channels}ch {_format.Encoding}");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            Log.Warn($"소리 캡처를 시작하지 못함(출력 장치 없음?): {ex.Message}");
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var f = _format;
        if (f == null || e.BytesRecorded == 0) return;
        int ch = Math.Max(1, f.Channels);
        // 공유 모드 루프백은 보통 32비트 float(Extensible로 옴)
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32);
        int bytes = f.BitsPerSample / 8, frame = bytes * ch;
        if (frame == 0) return;
        lock (_lock)
        {
            for (int o = 0; o + frame <= e.BytesRecorded; o += frame)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++)
                {
                    int p = o + c * bytes;
                    sum += isFloat && bytes == 4 ? BitConverter.ToSingle(e.Buffer, p)
                         : bytes == 2 ? BitConverter.ToInt16(e.Buffer, p) / 32768f
                         : bytes == 4 ? BitConverter.ToInt32(e.Buffer, p) / 2147483648f : 0;
                }
                _ring[_pos] = sum / ch;
                _pos = (_pos + 1) % N;
            }
            _lastDataTicks = Environment.TickCount64;
        }
    }

    /// <summary>샘플 N개 → 로그 간격 32개 막대(0~1). 테스트에서 직접 부른다.</summary>
    public static double[] Spectrum(float[] samples, int sampleRate)
    {
        var buf = new Complex[N];
        for (int i = 0; i < N; i++) buf[i] = new Complex(samples[i] * Window[i], 0);
        Fft(buf);
        var result = new double[Bands];
        double binHz = (double)sampleRate / N;
        for (int b = 0; b < Bands; b++)
        {
            double lo = MinHz * Math.Pow(MaxHz / MinHz, (double)b / Bands), hi = MinHz * Math.Pow(MaxHz / MinHz, (double)(b + 1) / Bands);
            int i0 = Math.Max(1, (int)(lo / binHz)), i1 = Math.Min(N / 2 - 1, Math.Max(i0, (int)(hi / binHz)));
            double mag = 0;
            for (int i = i0; i <= i1; i++) mag = Math.Max(mag, buf[i].Magnitude);
            // 전체 크기(진폭 1) 사인파가 0dB가 되게(한 창 합 N/2, 한쪽 N/4). 음악은 높은 음일수록 약해서 위로 갈수록 조금 올린다.
            double db = 20 * Math.Log10(mag / (N / 4.0) + 1e-9) + 10.0 * b / Bands;
            result[b] = Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
        }
        return result;
    }

    static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k]; var v = a[i + k + len / 2] * w;
                    a[i + k] = u + v; a[i + k + len / 2] = u - v;
                    w *= wl;
                }
            }
        }
    }
}
