using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using VidFlow.Localization;

namespace VidFlow.Services;

/// <summary>A speech recognition model on offer: its file, its size (for saying so before downloading) and its name.</summary>
public sealed record SpeechModel(string Id, string File, long Bytes, string NameKey);

/// <summary>
/// Subtitles made from a video's sound, for videos that have none: whisper.cpp, run on this PC (nothing is uploaded).
/// It is optional and large, so it is downloaded only when asked for in Settings: the program (a few MB) and one
/// model (hundreds of MB), into its own folder. The CPU recognizes several times faster than the sound plays; an
/// NVIDIA card several times faster again, with a CUDA build of the program that is a separate, larger download.
/// </summary>
public static partial class SpeechRecognizer
{
    /// <summary>whisper.cpp's Windows programs come with its numbered builds, not with its version releases.</summary>
    const string EngineRelease = "b5454";

    public static string EngineZip(string arch) => arch == "arm64" ? "whisper-bin-win-cpu-arm64.zip" : "whisper-bin-x64.zip";

    static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>CUDA 12.4: the 11.8 build comes without its cuBLAS libraries, so it never loads. Runs on RTX 50 cards
    /// too (their code is compiled by the driver on first use, then cached).</summary>
    const string GpuZip = "whisper-bin-win-cuda-12.4.0-x64.zip";
    public const long GpuBytes = 684_913_404;

    /// <summary>Most accurate first: the default.</summary>
    public static readonly SpeechModel[] Models =
    [
        new("accurate", "ggml-large-v3-turbo-q5_0.bin", 574_041_195, "Asr_ModelAccurate"),
        new("fast", "ggml-small-q5_1.bin", 190_085_487, "Asr_ModelFast"),
    ];

    /// <summary>Voice activity detection: silence and music are skipped, where Whisper would otherwise make text up.</summary>
    const string VadFile = "ggml-silero-v5.1.2.bin";

    /// <summary>Hugging Face, then a mirror of it that answers from mainland China.</summary>
    static readonly string[] Hosts = ["https://huggingface.co", "https://hf-mirror.com"];

    public static string Dir => Path.Combine(ToolManager.BinDir, "whisper");
    static string EnginePath => Path.Combine(Dir, "whisper-cli.exe");
    static string GpuDir => Path.Combine(Dir, "cuda");
    static string GpuEnginePath => Path.Combine(GpuDir, "whisper-cli.exe");

    /// <summary>An NVIDIA driver is installed (it brings nvcuda.dll), on an x64 PC.</summary>
    public static bool HasNvidia => Arch == "x64" && File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));

    public static bool GpuReady => File.Exists(GpuEnginePath);
    static string VadPath => Path.Combine(Dir, VadFile);
    static string ModelPath(SpeechModel m) => Path.Combine(Dir, m.File);

    public static SpeechModel Model(string? id) => Models.FirstOrDefault(m => m.Id == id) ?? Models[0];
    public static SpeechModel Chosen => Model(AppSettings.Current.SpeechModel);

    public static bool IsReady(SpeechModel m) => File.Exists(EnginePath) && File.Exists(VadPath) && File.Exists(ModelPath(m));

    /// <summary>The chosen model is downloaded (what Settings shows).</summary>
    public static bool IsReady() => IsReady(Chosen);

    /// <summary>The model recognition uses: the chosen one, or, when that isn't downloaded, one that is (choosing
    /// another model in Settings shouldn't stop recognition until it is downloaded too). Null if there is none.</summary>
    public static SpeechModel? Usable => IsReady(Chosen) ? Chosen : Models.FirstOrDefault(IsReady);

    /// <summary>Recognition can run: the program and some model are there.</summary>
    public static bool CanRecognize => Usable is not null;

    /// <summary>Where recognitions keep the audio they work on; left behind only if the app was closed meanwhile.</summary>
    public static string WorkRoot => Path.Combine(Path.GetTempPath(), "VidFlow", "speech");

    /// <summary>At startup, when nothing can be using it: removes what a recognition interrupted by closing the app
    /// left (its audio can be hundreds of MB).</summary>
    public static void CleanUpWork()
    {
        try
        {
            if (Directory.Exists(WorkRoot)) Directory.Delete(WorkRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Leftover speech audio not removed: {ex.Message}");
        }
    }

    /// <summary>Disk space taken: everything, or the CUDA build alone.</summary>
    public static long SizeOnDisk(bool gpuOnly = false)
    {
        var dir = gpuOnly ? GpuDir : Dir;
        if (!Directory.Exists(dir)) return 0;
        var files = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories);
        // The CUDA build sits inside the main folder: "everything else" leaves it out.
        return files.Where(f => gpuOnly || !f.FullName.StartsWith(GpuDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Sum(f => f.Length);
    }

    /// <summary>Anything downloaded at all (for offering to remove it).</summary>
    public static bool AnyInstalled => Directory.Exists(Dir) && Directory.EnumerateFileSystemEntries(Dir).Any();

    // ---- Installing ------------------------------------------------------------------------------------------

    /// <summary>Raised from any thread when installing starts, moves on a percent, or ends.</summary>
    public static event Action? Changed;

    static Task? _install;
    static int _shownPercent = -1;

    public static bool Installing => _install is { IsCompleted: false };

    /// <summary>0-100 while installing.</summary>
    public static double Progress { get; private set; }

    /// <summary>Why the last install failed; null after a success.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Why the last removal didn't happen (recognition running, a file in use), and whether it was the
    /// graphics card's; null otherwise.</summary>
    public static string? RemoveProblem { get; private set; }
    public static bool RemoveProblemGpu { get; private set; }

    /// <summary>The program is downloaded, whatever the model (for telling "download the model" from "set it up").</summary>
    public static bool EngineInstalled => File.Exists(EnginePath);

    /// <summary>Downloads what <paramref name="model"/> still needs. One install at a time; it carries on when the
    /// page that started it is left.</summary>
    public static Task InstallAsync(SpeechModel model) => Start(() => RunInstallAsync(model));

    /// <summary>Downloads the CUDA build of the program, used instead of the CPU one from then on.</summary>
    public static Task InstallGpuAsync()
    {
        if (Installing) return _install!;
        InstallingGpu = true;
        return Start(async () =>
        {
            await InstallEngineAsync(GpuZip, GpuDir, Part(0, 100));
            Log.Info("Speech recognition on the graphics card ready");
        });
    }

    /// <summary>The install running is the graphics card's (shown on its own row in Settings).</summary>
    public static bool InstallingGpu { get; private set; }

    /// <summary>The last install was the graphics card's: which row its failure belongs on.</summary>
    public static bool LastInstallGpu { get; private set; }

    static Task Start(Func<Task> work)
    {
        if (Installing) return _install!;
        LastError = null;
        RemoveProblem = null;
        LastInstallGpu = InstallingGpu;
        Progress = 0;
        _shownPercent = -1;
        _install = Run(work);
        Changed?.Invoke();
        return _install;

        static async Task Run(Func<Task> work)
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                LastError = ex is HttpRequestException ? L.Get("Error_Network") : ex.Message;
                Log.Warn($"Speech recognition install failed: {ex}");
            }
            finally
            {
                InstallingGpu = false;
                Changed?.Invoke();
            }
        }
    }

    static async Task RunInstallAsync(SpeechModel model)
    {
        {
            Directory.CreateDirectory(Dir);
            // Shares of the bar: the program, the voice detector, then the model.
            const double engineShare = 3, vadShare = 1;
            if (!File.Exists(EnginePath))
                await InstallEngineAsync(EngineZip(Arch), Dir, Part(0, engineShare));
            if (!File.Exists(VadPath))
                await DownloadModelAsync($"/ggml-org/whisper-vad/resolve/main/{VadFile}", VadPath, Part(engineShare, vadShare));
            if (!File.Exists(ModelPath(model)))
                await DownloadModelAsync($"/ggerganov/whisper.cpp/resolve/main/{model.File}", ModelPath(model), Part(engineShare + vadShare, 100 - engineShare - vadShare));
            Log.Info($"Speech recognition ready: {model.File}");
        }
    }

    /// <summary>Progress of one step, as its share of the whole bar.</summary>
    static IProgress<double> Part(double start, double share) => new Progress<double>(p =>
    {
        Progress = start + share * p / 100;
        // FileDownloader reports every block: say so only when the whole percent moves.
        if ((int)Progress == _shownPercent) return;
        _shownPercent = (int)Progress;
        Changed?.Invoke();
    });

    static async Task InstallEngineAsync(string zipName, string dir, IProgress<double> progress)
    {
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "whisper.zip.download");
        await FileDownloader.DownloadAsync($"https://github.com/ggml-org/whisper.cpp/releases/download/{EngineRelease}/{zipName}",
            zipPath, progress, CancellationToken.None);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            // The program and the libraries it loads; the zip also has examples and tests.
            var wanted = zip.Entries.Where(e => e.Name.Equals("whisper-cli.exe", StringComparison.OrdinalIgnoreCase)
                                                || e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
            if (!wanted.Any(e => e.Name.Equals("whisper-cli.exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(L.Format("Error_ZipMissing", "whisper-cli.exe"));
            // The program last, so a half-finished unpack doesn't look installed.
            foreach (var entry in wanted.OrderBy(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                entry.ExtractToFile(Path.Combine(dir, entry.Name), overwrite: true);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    static async Task DownloadModelAsync(string path, string dest, IProgress<double> progress)
    {
        var part = dest + ".download";
        Exception? last = null;
        foreach (var host in Hosts)
        {
            try
            {
                await FileDownloader.DownloadAsync(host + path, part, progress, CancellationToken.None);
                File.Move(part, dest, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                Log.Info($"Model download from {host} failed: {ex.Message}");
                last = ex;
            }
        }
        throw last!;
    }

    /// <summary>Deletes the program and every model (<paramref name="gpuOnly"/>: just the CUDA build).</summary>
    public static void Remove(bool gpuOnly = false)
    {
        if (Installing) return;
        RemoveProblemGpu = gpuOnly;
        // Deleting under a running recognition would leave half of it: the model gone, the program still in use.
        if (Volatile.Read(ref _recognizing) > 0)
        {
            RemoveProblem = L.Get("Settings_SpeechBusy");
            Changed?.Invoke();
            return;
        }
        try
        {
            var dir = gpuOnly ? GpuDir : Dir;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            LastError = RemoveProblem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RemoveProblem = L.Format("Settings_SpeechRemoveFailed", ex.Message);
        }
        Changed?.Invoke();
    }

    // ---- Recognizing -----------------------------------------------------------------------------------------

    /// <summary>One recognition at a time: two would share the CPU (each set to use all of it) or the graphics card's
    /// memory, and both go slower; the others wait their turn.</summary>
    static readonly SemaphoreSlim Turn = new(1, 1);

    /// <summary>Recognitions running or waiting for their turn.</summary>
    static int _recognizing;

    /// <summary>Spoken languages offered: "auto" lets Whisper tell.</summary>
    public static readonly string[] Languages = ["auto", "zh", "en", "ja"];

    /// <summary>Whisper writes Chinese in either script; a Simplified Chinese sentence to carry on from steers it.</summary>
    internal const string ChinesePrompt = "以下是普通话的句子。";

    [GeneratedRegex(@"^\[(\d+):(\d{2}):(\d{2})\.\d+ --> (\d+):(\d{2}):(\d{2})\.\d+\]")]
    private static partial Regex Segment();

    [GeneratedRegex(@"auto-detected language: ([a-z]{2,3})")]
    private static partial Regex Detected();

    /// <summary>The arguments for whisper-cli (pure, for testing).</summary>
    public static List<string> Arguments(string model, string vad, string wav, string language, string outputBase, int threads)
    {
        var args = new List<string> { "-m", model, "-f", wav, "-l", language, "-t", threads.ToString(CultureInfo.InvariantCulture),
            "--vad", "-vm", vad, "-osrt", "-of", outputBase };
        if (language == "zh") args.AddRange(["--prompt", ChinesePrompt]);
        return args;
    }

    /// <summary>How far along, from a line whisper-cli prints for each piece it recognizes; null for other lines.</summary>
    public static double? ProgressOf(string line, double durationSeconds)
    {
        if (durationSeconds <= 0 || Segment().Match(line) is not { Success: true } m) return null;
        var end = int.Parse(m.Groups[4].Value) * 3600 + int.Parse(m.Groups[5].Value) * 60 + int.Parse(m.Groups[6].Value);
        return Math.Clamp(end * 100.0 / durationSeconds, 0, 100);
    }

    public static string? DetectedLanguage(string line) => Detected().Match(line) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Writes "<paramref name="outputBase"/>.srt" from the sound in <paramref name="audio"/> and returns its path
    /// and the language spoken (as given, or as Whisper heard it). Progress is 0-100, or -1 while waiting for another
    /// recognition to finish.</summary>
    public static async Task<(string Srt, string Language)> RecognizeAsync(string audio, string language, string outputBase,
        Action<double> onProgress, CancellationToken ct)
    {
        Interlocked.Increment(ref _recognizing);
        try
        {
            if (Turn.CurrentCount == 0) onProgress(-1);
            await Turn.WaitAsync(ct);
            try
            {
                onProgress(0);
                return await RecognizeNowAsync(audio, language, outputBase, onProgress, ct);
            }
            finally
            {
                Turn.Release();
            }
        }
        finally
        {
            // "Can't remove while recognizing" says nothing once none is left.
            if (Interlocked.Decrement(ref _recognizing) == 0 && RemoveProblem == L.Get("Settings_SpeechBusy"))
            {
                RemoveProblem = null;
                Changed?.Invoke();
            }
        }
    }

    static async Task<(string Srt, string Language)> RecognizeNowAsync(string audio, string language, string outputBase,
        Action<double> onProgress, CancellationToken ct)
    {
        // Decided once: a model chosen in Settings meanwhile must not change it between the two attempts below.
        var model = Usable ?? throw new YtDlpException(L.Get("Error_AsrNotReady"));
        var ffmpeg = ToolManager.FfmpegPath ?? throw new YtDlpException(L.Get("Error_AudioNeedsFfmpeg"));
        language = Languages.Contains(language) ? language : "auto";

        // 16 kHz mono is what Whisper listens to; converting here also reads any format the site served.
        var wav = outputBase + ".16k.wav";
        try
        {
            var (code, error) = await RunAsync(ffmpeg, ["-v", "error", "-y", "-i", audio, "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wav], null, ct);
            if (code != 0 || !File.Exists(wav)) throw new YtDlpException(L.Get("Error_AsrFailed"), error);
            var seconds = (new FileInfo(wav).Length - 44) / 32000.0;

            var threads = Math.Clamp(Environment.ProcessorCount, 1, 16);
            var detected = language;
            async Task<(int, string)> Recognize(string engine) => await RunAsync(engine,
                Arguments(ModelPath(model), VadPath, wav, language, outputBase, threads), line =>
                {
                    if (ProgressOf(line, seconds) is { } p) onProgress(p);
                    if (DetectedLanguage(line) is { } d) detected = d;
                }, ct);

            var gpu = GpuReady && HasNvidia;
            Log.Info($"Recognizing {seconds:0} s of speech ({model.File}, {language}, {(gpu ? "graphics card" : $"{threads} threads")})");
            (code, error) = gpu ? await Recognize(GpuEnginePath) : (-1, "");
            if (gpu && code != 0)
                // A driver too old for CUDA 12, a card without enough memory: the CPU still does it.
                Log.Warn($"Recognizing on the graphics card failed ({code}), using the CPU: {error.Split('\n').LastOrDefault()}");
            if (code != 0) (code, error) = await Recognize(EnginePath);
            var srt = outputBase + ".srt";
            if (code != 0 || !File.Exists(srt)) throw new YtDlpException(L.Get("Error_AsrFailed"), error);
            onProgress(100);
            return (srt, detected == "auto" ? "und" : detected);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    /// <summary>SRT into WebVTT, ASS or LRC (ffmpeg); the SRT is removed. Returns the new file.</summary>
    public static async Task<string> ConvertAsync(string srt, string format, CancellationToken ct)
    {
        var ffmpeg = ToolManager.FfmpegPath ?? throw new YtDlpException(L.Get("Error_AudioNeedsFfmpeg"));
        var dest = Path.ChangeExtension(srt, format);
        var (code, error) = await RunAsync(ffmpeg, ["-v", "error", "-y", "-i", srt, dest], null, ct);
        if (code != 0 || !File.Exists(dest)) throw new YtDlpException(L.Get("Error_AsrFailed"), error);
        File.Delete(srt);
        return dest;
    }

    static Task<(int Code, string Error)> RunAsync(string exe, IEnumerable<string> args, Action<string>? onLine, CancellationToken ct) =>
        ProcessRunner.RunAsync(exe, args, onLine, ct);
}
