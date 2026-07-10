using System.IO.Pipes;
using NAudio.Wave;

namespace ScreenClipTool.Audio;

/// <summary>
/// Une source audio WASAPI streamée en PCM brut vers un named pipe que ffmpeg
/// lit comme un fichier (\\.\pipe\…).
/// </summary>
public sealed class AudioTrack : IDisposable
{
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly IWaveIn _capture;
    private readonly NamedPipeServerStream _pipe;
    private readonly object _writeLock = new();
    private volatile bool _connected;
    private volatile bool _broken;

    public string PipeName { get; }
    public WaveFormat Format => _capture.WaveFormat;

    /// <summary>Format d'échantillon côté ffmpeg (-f) : f32le / s16le / s32le.</summary>
    public string SampleFmt
    {
        get
        {
            var wf = _capture.WaveFormat;
            if (wf.Encoding == WaveFormatEncoding.IeeeFloat) return "f32le";
            if (wf is WaveFormatExtensible ext)
            {
                if (ext.SubFormat == SubtypeIeeeFloat) return "f32le";
                if (ext.SubFormat == SubtypePcm) return wf.BitsPerSample == 16 ? "s16le" : "s32le";
            }
            if (wf.Encoding == WaveFormatEncoding.Pcm) return wf.BitsPerSample == 16 ? "s16le" : "s32le";
            throw new NotSupportedException($"Format audio non géré : {wf}");
        }
    }

    public AudioTrack(IWaveIn capture, string pipeName)
    {
        _capture = capture;
        PipeName = pipeName;
        // 4 Mo de buffer sortant ≈ 10 s de PCM float stéréo 48 kHz : de la marge
        // si ffmpeg consomme en retard, sans bloquer le thread de capture.
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 4 * 1024 * 1024);
        _ = _pipe.WaitForConnectionAsync().ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) _connected = true;
        }, TaskScheduler.Default);
        _capture.DataAvailable += OnDataAvailable;
    }

    public void Start() => _capture.StartRecording();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_connected || _broken || e.BytesRecorded == 0) return;
        lock (_writeLock)
        {
            try { _pipe.Write(e.Buffer, 0, e.BytesRecorded); }
            catch
            {
                // ffmpeg parti (arrêt/restart) : on cesse d'écrire jusqu'à la
                // prochaine session (chaque session recrée ses AudioTrack).
                _broken = true;
            }
        }
    }

    public void Dispose()
    {
        try { _capture.DataAvailable -= OnDataAvailable; } catch { }
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
        lock (_writeLock)
        {
            try { _pipe.Dispose(); } catch { }
        }
    }
}

/// <summary>
/// Capture audio d'une session ffmpeg : loopback du bureau + micro optionnel
/// (piste séparée). Un WasapiOut jouant du silence maintient le flux loopback
/// actif même quand rien ne joue (sinon WASAPI ne délivre aucune donnée et le
/// muxer ffmpeg attendrait l'audio indéfiniment).
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    private IWavePlayer? _silenceKeepalive;

    public AudioTrack? Loopback { get; private set; }
    public AudioTrack? Mic { get; private set; }

    private AudioCaptureService() { }

    /// <summary>Lève une exception si aucun périphérique de rendu (loopback impossible).</summary>
    public static AudioCaptureService Create(bool micEnabled, Action<string>? status)
    {
        var svc = new AudioCaptureService();
        var loopCapture = new WasapiLoopbackCapture();
        svc.Loopback = new AudioTrack(loopCapture, "scb_loop_" + Guid.NewGuid().ToString("N")[..8]);
        Log.Info($"Loopback : {loopCapture.WaveFormat.SampleRate} Hz, {loopCapture.WaveFormat.Channels} ch, {svc.Loopback.SampleFmt}");

        if (micEnabled)
        {
            try
            {
                var micCapture = new NAudio.CoreAudioApi.WasapiCapture();
                svc.Mic = new AudioTrack(micCapture, "scb_mic_" + Guid.NewGuid().ToString("N")[..8]);
                Log.Info($"Micro : {micCapture.WaveFormat.SampleRate} Hz, {micCapture.WaveFormat.Channels} ch, {svc.Mic.SampleFmt}");
            }
            catch (Exception ex)
            {
                Log.Warn("Micro indisponible : " + ex.Message);
                status?.Invoke("Micro indisponible — ignoré");
            }
        }

        try
        {
            var silence = new WasapiOut();
            silence.Init(new SilenceProvider(loopCapture.WaveFormat));
            svc._silenceKeepalive = silence;
        }
        catch (Exception ex)
        {
            Log.Warn("Keepalive silence indisponible : " + ex.Message);
        }

        return svc;
    }

    public void StartStreaming()
    {
        _silenceKeepalive?.Play();
        Loopback?.Start();
        Mic?.Start();
    }

    public void Dispose()
    {
        try { _silenceKeepalive?.Stop(); } catch { }
        try { _silenceKeepalive?.Dispose(); } catch { }
        Loopback?.Dispose();
        Mic?.Dispose();
        Loopback = null;
        Mic = null;
    }
}
