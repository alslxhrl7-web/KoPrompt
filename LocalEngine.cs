using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace KoPrompt;

/// <summary>
/// PC 안에서만 도는 번역 엔진. models 폴더의 .gguf 모델을 llama.cpp로 직접 실행하며,
/// 네트워크는 전혀 쓰지 않는다.
/// </summary>
public static class LocalEngine
{
    public const string DefaultNegative =
        "lowres, blurry, bad anatomy, bad hands, extra fingers, deformed, disfigured, " +
        "watermark, text, signature, jpeg artifacts, worst quality, low quality";

    private const string InputMarker = "@@KOPROMPT_INPUT@@";
    private const string AnswerStart = "PROMPT:";   // 답의 첫머리를 미리 적어 두어 모델이 바로 본문부터 쓰게 한다

    /// <summary>
    /// 답이 반드시 "프롬프트 한 줄 / NOTES: …" 모양이 되게 모델이 고를 수 있는 글자를 제한한다.
    /// 길고 요구 사항이 많은 입력에서는 모델이 프롬프트를 번호·글머리 목록으로 여러 줄에 걸쳐 쓰면서
    /// 지어낸 설명을 줄줄이 늘어놓는다. 줄바꿈 뒤에는 NOTES만 올 수 있게 해서 한 줄로 끝내게 한다.
    /// 콜론도 막는다. 콜론을 쓰게 두면 "… five fingers defined:"에서 줄을 끝내고 정작 내용은 목록으로 쓰려다 잘린다.
    /// </summary>
    private static readonly Grammar OneLinePrompt = new("root ::= [^\\n:]+ \"\\nNOTES:\" rest\nrest ::= [^\\x00]*", "root");

    /// <summary>풀이(NOTES)에 쓸 수 있는 토큰 수. 풀이는 "은어 => 영어" 몇 줄이면 되는데, 가끔 모델이 설명을 끝없이 늘어놓는다.</summary>
    private const int MaxNoteTokens = 48;

    /// <summary>프롬프트 줄에 쓸 수 있는 토큰 수. 긴 요청문도 70토큰쯤이면 충분하다.</summary>
    private const int MaxPromptTokens = 150;

    /// <summary>줄바꿈으로 끝나는 글의 마지막 줄.</summary>
    private static string LastLine(string text)
    {
        string body = text.TrimEnd('\n');
        return body[(body.LastIndexOf('\n') + 1)..].Trim();
    }

    private const int ContextTokens = 4096;
    /// <summary>
    /// 0이면 매번 가장 그럴듯한 낱말만 고른다(같은 입력이면 늘 같은 결과). 0.2로 두었을 때는 같은 문장도 실행할 때마다
    /// 표현이 바뀌고, 가끔 엉뚱한 군말을 붙였다. 0으로 시험해 봐도 번역 품질은 떨어지지 않았다.
    /// </summary>
    private const float DefaultTemperature = 0f;
    private const int MaxOutputTokens = 300;
    private const int MaxCachedResults = 200;
    private const int ReadChunk = 16;        // 변환할 때 CPU로 한 번에 읽는 토큰 수
    private const int PrereadChunk = 8;      // 미리 읽을 때는 잘게 나눠서, 변환이 시작되면 바로 비켜 준다

    /// <summary>
    /// 한 번에 이만큼 이상 읽으면 llama.cpp가 그 계산을 GPU에 맡긴다(모델을 GPU에 올리지 않았어도).
    /// 그래서 읽을 것이 많을 때(붙여 넣은 긴 글, 처음 지침 읽기)는 잘게 나누지 않고 크게 묶어 읽는 편이 훨씬 빠르다.
    /// </summary>
    private const int GpuAssistBatch = 32;
    private const int BigBatch = 256;
    private const int PrereadHoldBack = 2;

    /// <summary>지침 뒤에 지금 모델이 읽어 둔 사용자 입력 토큰. 다음 변환과 앞부분이 같으면 재사용한다.</summary>
    private static readonly List<LLamaToken> KvTail = [];

    /// <summary>같은 입력을 다시 변환하면 AI를 돌리지 않고 바로 돌려준다.</summary>
    private static readonly Dictionary<string, string> ResultCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 모델을 GPU에 올리려면 모델 크기 말고도 이만큼(MiB)이 더 비어 있어야 한다. 대화 기억·계산에 600MiB쯤 들고,
    /// 나머지는 같은 GPU를 쓰는 다른 프로그램(이미지 생성기 등) 몫으로 남겨 둔다.
    /// 6GB 카드에 큰 모델을 꽉 채워 올려 봤더니 CPU보다 빠르지도 않았다(초당 4.4토큰).
    /// </summary>
    private const int GpuHeadroomMiB = 1500;

    private static readonly string[] StopMarkers =
        ["[|endofturn|]", "[|user|]", "<end_of_turn>", "<|im_end|>", "<|eot_id|>", "<|end|>"];

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly StringBuilder NativeLog = new();
    private static readonly NativeLogConfig.LLamaLogCallback LogCallback = OnNativeLog;
    private static readonly LLamaSeqId Seq = (LLamaSeqId)0;

    private static LLamaWeights? _model;
    private static LLamaContext? _context;
    private static int _prefixCount;         // 미리 읽어 둔 지침(시스템 프롬프트 + 예시)의 토큰 수
    private static string _suffixText = "";  // 사용자 입력 뒤에 붙는 템플릿 꼬리
    private static bool _nativeConfigured;

    public static string Backend { get; private set; } = "";
    public static bool IsLoaded => _context is not null;

    public static string ModelsFolder => AppPaths.Models;

    /// <summary>
    /// 빠른 모드. 모델이 둘 이상 있으면 가장 작은 모델을 쓴다(빠르지만 사전에 없는 은어 해석은 덜 정확하다).
    /// 꺼져 있으면 가장 큰 모델을 쓴다. 바꾼 뒤에는 UnloadAsync로 내렸다가 다시 불러와야 적용된다.
    /// </summary>
    public static bool FastMode { get; set; }

    /// <summary>지금 올라와 있는 모델 파일 이름(확장자 제외).</summary>
    public static string ModelName { get; private set; } = "";

    /// <summary>
    /// 쓸 수 있는 .gguf 모델들을 작은 것부터. 사용자 폴더의 models를 보되, exe를 폴더째 들고 다니는
    /// 경우를 위해 exe 옆에 models 폴더가 있으면 그쪽을 먼저 본다.
    /// </summary>
    public static IReadOnlyList<FileInfo> FindModels()
    {
        foreach (string dir in new[] { Path.Combine(AppContext.BaseDirectory, "models"), AppPaths.Models })
        {
            if (!Directory.Exists(dir)) continue;
            FileInfo[] found = [.. new DirectoryInfo(dir).GetFiles("*.gguf").OrderBy(f => f.Length)];
            if (found.Length > 0) return found;
        }
        return [];
    }

    /// <summary>지금 설정(빠른 모드 여부)에서 쓸 모델 파일.</summary>
    public static string? FindModelFile()
    {
        IReadOnlyList<FileInfo> models = FindModels();
        if (models.Count == 0) return null;
        return (FastMode ? models[0] : models[^1]).FullName;
    }

    public static string GetNativeLog()
    {
        lock (NativeLog) return NativeLog.ToString();
    }

    private static void OnNativeLog(LLamaLogLevel level, string message)
    {
        lock (NativeLog)
        {
            if (NativeLog.Length < 600_000) NativeLog.Append(message);
        }
    }

    private static void Trace(string message) => OnNativeLog(LLamaLogLevel.Info, "\n[koprompt] " + message + "\n");

    // ── 모델 불러오기 ─────────────────────────────────────────
    public static async Task EnsureLoadedAsync(bool allowGpu, IProgress<string>? status, CancellationToken ct)
    {
        if (_context is not null) return;

        await Gate.WaitAsync(ct);
        try
        {
            if (_context is not null) return;
            await Task.Run(() => Load(allowGpu, status, ct), ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>GPU 사용 설정을 바꿨을 때 모델을 내려서 다음 변환 때 다시 불러오게 한다.</summary>
    public static async Task UnloadAsync()
    {
        await Gate.WaitAsync();
        try
        {
            Unload();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void Unload()
    {
        _context?.Dispose();
        _context = null;
        _model?.Dispose();
        _model = null;
        KvTail.Clear();
        ResultCache.Clear();   // 다른 모델이나 설정으로 다시 올릴 수 있으니 저장해 둔 결과도 버린다
        Backend = "";
        ModelName = "";
    }

    private static void Load(bool allowGpu, IProgress<string>? status, CancellationToken ct)
    {
        string path = FindModelFile() ?? throw new UserFacingException(
            "AI 모델 파일이 없습니다.\r\n\r\n" +
            "아래 폴더에 .gguf 모델 파일을 넣은 뒤 다시 실행하세요.\r\n" + ModelsFolder);

        if (!_nativeConfigured)
        {
            // llama.cpp DLL(runtimes 폴더)도 사용자 폴더에 있다. exe 옆에 있으면 그쪽을 쓴다.
            string runtimeHome = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes"))
                ? AppContext.BaseDirectory
                : AppPaths.Data;
            NativeLibraryConfig.All.WithSearchDirectory(runtimeHome).WithLogCallback(LogCallback);
            _nativeConfigured = true;
        }

        try
        {
            NativeApi.llama_empty_call();   // 작업 폴더를 바꾸기 전에 네이티브 DLL부터 불러 둔다
        }
        catch (Exception e)
        {
            throw new UserFacingException(
                "AI 실행 파일을 불러오지 못했습니다. 아래 폴더에 'runtimes' 폴더가 있는지 확인하세요.\r\n" +
                AppPaths.Data + "\r\n\r\n" + (e.InnerException ?? e).Message);
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("KOPROMPT_GPU_LAYERS"), out int forced))
        {
            LoadWeights(path, forced, status);   // 점검용 강제 설정
        }
        else if (!allowGpu || !GpuHasRoom(path) || !TryLoadOnGpu(path, status))
        {
            LoadWeights(path, 0, status);
        }

        ct.ThrowIfCancellationRequested();
        PrimeInstructions(path, status, ct);
    }

    /// <summary>
    /// NVIDIA GPU면 지금 비어 있는 메모리를 드라이버 도구(nvidia-smi, 로컬 실행)로 물어본다.
    /// 이미지 생성기 같은 다른 프로그램이 GPU를 쓰고 있어 모델이 들어갈 자리가 없으면 false.
    /// 알 수 없으면 일단 시도해 보도록 true.
    /// </summary>
    private static bool GpuHasRoom(string modelPath)
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo(
                "nvidia-smi", "--query-gpu=memory.total,memory.used --format=csv,noheader,nounits")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            });
            if (p is null) return true;

            string line = p.StandardOutput.ReadLine() ?? "";
            if (!p.WaitForExit(3000)) p.Kill();

            string[] parts = line.Split(',');
            if (parts.Length < 2 || !double.TryParse(parts[0], out double total) || !double.TryParse(parts[1], out double used))
                return true;

            double needMiB = new FileInfo(modelPath).Length / 1048576.0 + GpuHeadroomMiB;
            Trace($"nvidia free {total - used:0} MiB, need {needMiB:0} MiB");
            return total - used >= needMiB;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return true;   // NVIDIA가 아니거나 도구가 없음
        }
    }

    /// <summary>GPU에 통째로 올린다. 메모리가 모자라 넘칠 것 같으면 내리고 false를 돌려준다.</summary>
    private static bool TryLoadOnGpu(string path, IProgress<string>? status)
    {
        int logStart = GetNativeLog().Length;
        try
        {
            LoadWeights(path, 999, status);
        }
        catch (Exception e) when (e is not UserFacingException)
        {
            Trace("GPU load failed: " + e.Message);
            Unload();
            return false;
        }

        string log = GetNativeLog()[logStart..];
        Match offload = Regex.Match(log, @"offloaded (\d+)/(\d+) layers to GPU");
        if (!offload.Success || offload.Groups[1].Value == "0")
        {
            Backend = "CPU";   // GPU 백엔드가 없는 PC
            return true;
        }

        // 다른 프로그램(이미지 생성기 등)이 GPU 메모리를 쓰고 있으면 모델이 일반 메모리로 넘쳐서
        // CPU보다도 느려진다. 그럴 때는 GPU를 포기한다.
        Match free = Regex.Match(log, @"using device \w+ \(.*?\).*?- (\d+) MiB free");
        Match size = Regex.Match(log, @"Vulkan\d+ model buffer size =\s*([\d.]+) MiB");
        if (free.Success && size.Success)
        {
            double freeMiB = double.Parse(free.Groups[1].Value);
            double modelMiB = double.Parse(size.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Trace($"gpu free {freeMiB:0} MiB, model {modelMiB:0} MiB");
            if (freeMiB < modelMiB + GpuHeadroomMiB)
            {
                Unload();
                return false;
            }
        }
        return true;
    }

    private static void LoadWeights(string path, int gpuLayers, IProgress<string>? status)
    {
        status?.Report(gpuLayers > 0 ? "AI 모델을 GPU에 올리는 중…" : "AI 모델을 불러오는 중…");

        // llama.cpp는 한글이 들어간 경로(C:\Users\홍길동\...)의 파일을 열지 못한다.
        // 작업 폴더를 모델 폴더로 옮기고 파일 이름만 넘겨서 피한다.
        string previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetDirectoryName(path)!;

            int threads = Math.Max(1, Environment.ProcessorCount / 2);
            if (int.TryParse(Environment.GetEnvironmentVariable("KOPROMPT_THREADS"), out int forcedThreads) && forcedThreads > 0)
                threads = forcedThreads;   // 점검용
            ModelParams p = new(Path.GetFileName(path))
            {
                ContextSize = ContextTokens,
                GpuLayerCount = gpuLayers,
                BatchSize = 512,
                Threads = threads,
                BatchThreads = threads,
            };
            int mainGpu = PickDiscreteGpu(p);

            _model = LLamaWeights.LoadFromFile(p);
            _context = _model.CreateContext(p);
            Backend = gpuLayers > 0 ? DescribeGpu(mainGpu) : "CPU";
        }
        catch (Exception e) when (e is not UserFacingException)
        {
            Unload();
            if (gpuLayers > 0) throw;
            throw new UserFacingException(
                "AI 모델을 불러오지 못했습니다.\r\n" + Path.GetFileName(path) + "\r\n\r\n" + e.Message);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    /// <summary>
    /// 내장 그래픽과 외장 그래픽이 같이 있는 노트북에서는 llama.cpp가 모델을 두 GPU에 나눠 올려
    /// 오히려 느려진다. 로그에 찍힌 Vulkan 장치 목록에서 외장(uma: 0) GPU 하나만 쓰도록 고정한다.
    /// </summary>
    private static int PickDiscreteGpu(ModelParams p)
    {
        if (p.GpuLayerCount <= 0) return 0;
        try
        {
            NativeApi.llama_supports_gpu_offload();  // GPU 장치 목록이 로그에 찍히도록 한다
        }
        catch (Exception)
        {
            return 0;
        }

        foreach (Match d in Regex.Matches(GetNativeLog(), @"ggml_vulkan:\s*(\d+)\s*=\s*(.+?)\s*\|\s*uma:\s*(\d)"))
        {
            if (d.Groups[3].Value == "0")
            {
                p.MainGpu = int.Parse(d.Groups[1].Value);
                p.SplitMode = GPUSplitMode.None;
                return p.MainGpu;
            }
        }
        return 0;
    }

    private static string DescribeGpu(int index)
    {
        foreach (Match d in Regex.Matches(GetNativeLog(), @"ggml_vulkan:\s*(\d+)\s*=\s*(.+?)\s*\("))
        {
            if (int.Parse(d.Groups[1].Value) == index) return "GPU · " + d.Groups[2].Value.Trim();
        }
        return "GPU";
    }

    /// <summary>
    /// 지침(시스템 프롬프트 + 예시)을 한 번 읽혀서 기억시켜 둔다. 이후 변환은 사용자 입력만 새로 읽으면 된다.
    /// 읽은 결과는 디스크에 저장해 두어 다음 실행부터는 바로 불러온다.
    /// </summary>
    private static void PrimeInstructions(string modelPath, IProgress<string>? status, CancellationToken ct)
    {
        LLamaWeights model = _model!;
        LLamaContext context = _context!;

        LLamaTemplate template = new(model) { AddAssistant = true };
        template.Add("system", SystemPrompt.Replace("\r\n", "\n"));
        foreach ((string user, string assistant) in Examples)
        {
            template.Add("user", user.Replace("\r\n", "\n"));
            template.Add("assistant", assistant.Replace("\r\n", "\n"));
        }
        template.Add("user", InputMarker);
        string rendered = Encoding.UTF8.GetString(template.Apply());

        int at = rendered.IndexOf(InputMarker, StringComparison.Ordinal);
        if (at < 0) throw new UserFacingException("이 모델의 대화 형식을 읽지 못했습니다. 다른 .gguf 모델을 써 보세요.");
        string prefixText = rendered[..at];
        _suffixText = rendered[(at + InputMarker.Length)..] + AnswerStart;

        LLamaToken[] prefix = context.Tokenize(prefixText, addBos: true, special: true);
        _prefixCount = prefix.Length;

        // 모델·지침·실행 방식이 같으면 예전에 저장한 기억을 그대로 쓴다.
        // 파일 이름은 "모델·실행방식 꼬리표 - 지침 꼬리표". 모델마다 따로 저장해 두어야 빠른 모드를 켰다 껐다 해도
        // 매번 지침을 다시 읽지 않는다.
        FileInfo modelFile = new(modelPath);
        ModelName = Path.GetFileNameWithoutExtension(modelFile.Name);
        string Tag(string text, int length) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..length];
        string modelTag = Tag($"{modelFile.Name}|{modelFile.Length}|{(Backend == "CPU" ? "cpu" : "gpu")}", 8);
        string key = Tag($"{ContextTokens}|{prefixText}", 16);
        string cacheDir = AppPaths.Cache;
        string cacheFile = Path.Combine(cacheDir, $"{modelTag}-{key}.state");

        if (File.Exists(cacheFile))
        {
            try
            {
                status?.Report("AI 준비 중…");
                context.LoadState(cacheFile);
                Trace("instruction state loaded from cache");
                return;
            }
            catch (Exception e)
            {
                Trace("state cache unusable: " + e.Message);
                context.NativeHandle.MemorySequenceRemove(Seq, 0, -1);
            }
        }

        Stopwatch watch = Stopwatch.StartNew();
        LLamaBatch batch = new();
        int batchSize = (int)context.BatchSize;
        for (int i = 0; i < prefix.Length; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            status?.Report($"AI가 은어 지침을 익히는 중… {100 * i / prefix.Length}% (처음 한 번만)");
            batch.Clear();
            int end = Math.Min(prefix.Length, i + batchSize);
            for (int j = i; j < end; j++) batch.Add(prefix[j], j, Seq, false);
            DecodeResult result = context.Decode(batch);
            if (result != DecodeResult.Ok) throw new UserFacingException("AI가 지침을 읽다가 실패했습니다. (" + result + ")");
        }
        Trace($"instructions primed: {prefix.Length} tokens in {watch.Elapsed.TotalSeconds:0.0}s");

        try
        {
            Directory.CreateDirectory(cacheDir);
            // 같은 모델의 옛 지침 기억과, 예전 버전이 남긴 파일(이름에 '-'가 없음)만 지운다.
            foreach (string old in Directory.GetFiles(cacheDir, "*.state"))
            {
                string name = Path.GetFileName(old);
                if (name.StartsWith(modelTag + "-", StringComparison.Ordinal) || !name.Contains('-')) File.Delete(old);
            }
            context.SaveState(cacheFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace("could not save state cache: " + e.Message);   // 저장 못 해도 변환에는 지장 없음
        }
    }

    // ── 변환 ─────────────────────────────────────────────────
    /// <param name="partial">
    /// 글자가 생성될 때마다 지금까지의 (프롬프트, 풀이)를 알려 준다.
    /// 프롬프트가 다 써지면 PromptDone이 true가 되고, 그 뒤로는 풀이만 이어진다.
    /// </param>
    public static async Task<PromptResult> ConvertAsync(
        ConvertRequest req, SlangDictionary dict, bool allowGpu,
        IProgress<string>? status, IProgress<PromptResult>? partial, CancellationToken ct)
    {
        await EnsureLoadedAsync(allowGpu, status, ct);

        string head = BuildHead(req.Korean, dict, out List<Note> glossary);
        string userMessage = head;
        string cacheKey = (req.Explain ? "E|" : "P|") + head;

        await Gate.WaitAsync(ct);
        try
        {
            if (_context is null) throw new UserFacingException("AI 모델이 내려가 있습니다. 다시 변환해 주세요.");

            if (ResultCache.TryGetValue(cacheKey, out string? cached))
                return BuildResult(cached, req, glossary, final: true) with { Info = "같은 입력이라 저장해 둔 결과를 썼습니다" };

            Generation g = await Task.Run(() => Translate(req, dict, userMessage, glossary, status, partial, ct), ct);

            PromptResult result = BuildResult(g.Raw, req, glossary, final: true);
            if (result.Prompt.Length == 0)
                throw new UserFacingException("AI가 빈 답을 내놓았습니다. 다시 변환해 보세요.");

            if (ResultCache.Count >= MaxCachedResults) ResultCache.Clear();
            ResultCache[cacheKey] = g.Raw;

            return result with
            {
                Info = $"로컬 AI · {Backend} · 읽기 {g.ReadSeconds:0.0}초 · 프롬프트 {g.PromptSeconds:0.0}초 · " +
                       $"초당 {g.Tokens / Math.Max(g.WriteSeconds, 0.1):0.#}토큰",
            };
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 사용자가 문장을 치는 동안, 지금까지 친 부분을 AI에게 미리 읽혀 둔다.
    /// 그러면 변환을 시작할 때는 문장 끝부분만 읽으면 된다. 변환 중이거나 모델이 없으면 아무것도 하지 않는다.
    /// </summary>
    public static async Task PrereadAsync(string korean, SlangDictionary dict, CancellationToken ct)
    {
        if (_context is null || korean.Trim().Length < 2) return;
        try
        {
            // 바로 앞의 미리 읽기가 한 토막을 마저 읽는 중일 수 있다. 끝나기를 기다리되,
            // 그사이 글자가 더 입력되거나 변환이 시작되면(ct) 그만둔다.
            await Gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_context is not { } context) return;
            string head = BuildHead(korean, dict, out _);
            await Task.Run(() =>
            {
                LLamaToken[] tokens = context.Tokenize(head, addBos: false, special: true);
                int count = tokens.Length - PrereadHoldBack;   // 끝의 몇 토큰은 뒤에 글자가 더 붙으면 달라질 수 있다
                if (count <= 0 || _prefixCount + tokens.Length + 64 + MaxOutputTokens > ContextTokens) return;

                Stopwatch watch = Stopwatch.StartNew();
                int read = SyncTail(context, new LLamaBatch(), tokens[..count], logitsOnLast: false, PrereadChunk, ct);
                if (read > 0) Trace($"pre-read {read} tokens in {watch.Elapsed.TotalSeconds:0.0}s");
            }, CancellationToken.None);
        }
        catch (Exception e) when (e is OperationCanceledException or UserFacingException)
        {
            // 미리 읽기는 못 해도 변환에는 지장이 없다.
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 지침 뒤에 읽혀 둔 내용을 주어진 토큰 열과 맞춘다. 이미 읽어 둔 앞부분이 같으면 그대로 두고,
    /// 달라지는 곳부터만 지우고 새로 읽는다.
    /// </summary>
    /// <param name="logitsOnLast">바로 이어서 답을 쓸 것인지. 그러려면 마지막 토큰은 반드시 새로 읽어야 한다.</param>
    /// <returns>새로 읽은 토큰 수.</returns>
    private static int SyncTail(
        LLamaContext context, LLamaBatch batch, LLamaToken[] tail, bool logitsOnLast, int chunk, CancellationToken ct)
    {
        int limit = logitsOnLast ? tail.Length - 1 : tail.Length;
        int keep = 0;
        while (keep < limit && keep < KvTail.Count && tail[keep] == KvTail[keep]) keep++;

        // 지난 답과 달라진 부분을 지운다.
        context.NativeHandle.MemorySequenceRemove(Seq, _prefixCount + keep, -1);
        KvTail.RemoveRange(keep, KvTail.Count - keep);

        int position = _prefixCount + keep;
        for (int i = keep, end; i < tail.Length; i = end)   // 나눠 읽어야 중간에 멈출 수 있다
        {
            ct.ThrowIfCancellationRequested();
            batch.Clear();
            int left = tail.Length - i;
            end = i + (left >= GpuAssistBatch ? Math.Min(left, BigBatch) : Math.Min(left, chunk));
            for (int j = i; j < end; j++) batch.Add(tail[j], position++, Seq, logitsOnLast && j == tail.Length - 1);
            DecodeResult result = context.Decode(batch);
            if (result != DecodeResult.Ok) throw new UserFacingException("AI가 입력을 읽다가 실패했습니다. (" + result + ")");
            for (int j = i; j < end; j++) KvTail.Add(tail[j]);
        }
        return tail.Length - keep;
    }

    /// <param name="PromptSeconds">입력을 읽기 시작해서 프롬프트 줄이 다 써질 때까지 걸린 시간.</param>
    private sealed record Generation(string Raw, int Tokens, double ReadSeconds, double PromptSeconds, double WriteSeconds);

    /// <summary>앞뒤로 이어서 돌린 두 번의 생성을 하나로 셈한다. 글은 뒤의 것을 쓴다.</summary>
    private static Generation After(Generation first, Generation second) => second with
    {
        Tokens = first.Tokens + second.Tokens,
        ReadSeconds = first.ReadSeconds + second.ReadSeconds,
        PromptSeconds = first.ReadSeconds + first.WriteSeconds + second.PromptSeconds,
        WriteSeconds = first.WriteSeconds + second.WriteSeconds,
    };

    /// <summary>
    /// 입력을 영어로 옮긴다. 낱말이나 짧은 구를 늘어놓은 목록은 모델이 그 낱말들로 장면을 엮어 버리기 쉬워서
    /// ("검, 방패, 투구, 횃불" → a knight wielding a sword …) 항목 수를 맞춰 하나씩 옮기게 한다.
    /// </summary>
    private static Generation Translate(
        ConvertRequest req, SlangDictionary dict, string userMessage, List<Note> glossary,
        IProgress<string>? status, IProgress<PromptResult>? partial, CancellationToken ct)
    {
        string normalized = NormalizeRequest(req.Korean);   // "검, 방패 그려줘"의 "그려줘"가 목록 항목에 섞이지 않게
        if (CommaListItems(normalized) is { } items)
            return TranslateList(req, dict, items, glossary, status, partial, ct);

        // 사전에 있는 낱말 하나짜리 입력("고양이")은 AI 없이 바로 그 뜻을 쓴다. 그냥 옮기게 하면 모델이 가끔
        // "cat curled up asleep"처럼 동작을 지어낸다. 띄어쓰기 없이 쓴 문장("고양이가소파에앉아있음")이나
        // 사전에 없는 은어는 여기에 걸리지 않고 아래에서 평소대로 옮긴다(풀이도 쓴다).
        // 사전에 없어도 짧고 조사·어미가 없는 한 낱말("드래곤", "갓벽")은 한 항목으로 짧게만 옮기게 한다(풀이는 쓴다).
        if (normalized.Length > 0 && normalized.IndexOfAny([' ', ',', '\n']) < 0 &&
            (DictionaryMeaning(normalized, dict, req.Korean) is not null ||
             (normalized.Length <= 6 && !Array.Exists(GrammarEndings, e => normalized.EndsWith(e, StringComparison.Ordinal)))))
        {
            return TranslateList(req, dict, [normalized], glossary, status, partial, ct);
        }

        Generation g = Infer(req, userMessage, glossary, OneLinePrompt, status, partial, ct);

        // 쉼표 없이 낱말만 늘어놓은 입력("소녀 고양이 나무 집")은 "사이버펑크 도시 야경" 같은 한 덩어리 이름과
        // 겉모습이 같아서 미리 가려낼 수 없다. 일단 옮겨 보고, 입력에 없는 동작·위치 관계로 엮었으면 목록으로 다시 옮긴다.
        if (BareNounWords(normalized, glossary) is { } words && LooksComposed(Parse(g.Raw).Item1))
        {
            Trace("bare nouns were woven into a scene; translating again as a list");
            return After(g, TranslateList(req, dict, words, glossary, status, partial, ct));
        }
        return g;
    }

    /// <summary>
    /// 목록을 항목마다 따로 옮겨 쉼표로 잇는다. 한꺼번에 옮기게 하면 항목 수를 묶어 두어도 모델이 목록을 한 문장으로
    /// 이어 쓰면서("A woman with black hair wearing a red…") 항목이 밀리고 끝에 쓰레기 글자가 붙었다.
    /// 항목마다 쓸 수 있는 낱말 수도 묶어 두어 한 항목에 장면을 덧붙이지 못하게 한다. 사전에 통째로 있는 항목은
    /// AI 없이 사전의 뜻을 쓴다.
    /// </summary>
    private static Generation TranslateList(
        ConvertRequest req, SlangDictionary dict, string[] items, List<Note> glossary,
        IProgress<string>? status, IProgress<PromptResult>? partial, CancellationToken ct)
    {
        string?[] english = [.. items.Select(item => DictionaryMeaning(item, dict, req.Korean))];
        Generation total = new("", 0, 0, 0, 0);
        string notes = "\nNOTES:\n- 없음";
        bool single = items.Length == 1;   // 한 낱말 입력이면 풀이도 그 답의 것을 쓴다(사전에 없는 은어 하나만 넣은 경우)

        for (int i = 0; i < items.Length; i++)
        {
            if (english[i] is not null) continue;
            ct.ThrowIfCancellationRequested();

            string head = BuildHead(items[i], dict, out List<Note> itemGlossary);
            int cap = items[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length + 3;
            Generation one = Infer(req with { Explain = single && req.Explain }, head, itemGlossary, ItemGrammar(cap), status, null, ct);
            total = After(total, one);
            int notesAt = one.Raw.IndexOf("\nNOTES:", StringComparison.Ordinal);
            if (single && notesAt >= 0) notes = one.Raw[notesAt..];

            string phrase = TrimListItem(Parse(one.Raw).Item1.Trim().TrimEnd('.'), items[i]);
            if (i > 0 && !Regex.IsMatch(phrase, @"^(?:Korean|Japanese|Chinese|English|Christmas|Halloween)\b"))
                phrase = Regex.Replace(phrase, @"^(?:(?:A|An|The)\b|[A-Z](?=[a-z]))", m => m.Value.ToLowerInvariant());   // 목록 중간은 소문자로 시작
            english[i] = phrase;

            string soFar = AnswerStart + " " + string.Join(", ", english.Where(e => !string.IsNullOrEmpty(e)));
            partial?.Report(BuildResult(soFar, req, glossary, final: false));
        }

        Trace($"list translated item by item: {items.Length} items, {total.Tokens} tokens");
        string line = string.Join(", ", english.Where(e => !string.IsNullOrEmpty(e)));
        return total with { Raw = AnswerStart + " " + line + notes };
    }

    /// <summary>
    /// 목록의 한 항목을 옮긴 말을 다듬는다.
    /// (1) 낱말 수 제한에 걸린 모델이 띄어쓰기 없이 이어 붙인 쓰레기 낱말("standingalone", "aircraftedDetails")부터 뒤를 버린다.
    /// (2) 한 낱말짜리 항목("횃불", "눈")에 붙은 동작·위치 군말("torch burning", "snowflakes falling gently")을 뗀다.
    /// </summary>
    private static string TrimListItem(string phrase, string korean)
    {
        // 이어 붙인 쓰레기 낱말: 하이픈 없이 14글자를 넘거나, 소문자 셋 이상 뒤에 대문자가 오는 것("aircraftedDetails").
        // "off-the-shoulder"(하이픈), "iPhone"·"McDonald"(앞 소문자 1~2개)는 정상 낱말로 본다.
        string[] words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int glued = Array.FindIndex(words, w => (w.Length > 14 && !w.Contains('-')) || Regex.IsMatch(w, "[a-z]{3,}[A-Z]"));
        string text = string.Join(' ', glued >= 0 ? words[..glued] : words);
        if (IsOnlyFiller(text)) text = phrase;

        if (!korean.Trim().Contains(' '))
        {
            // 위치·동작 군말. 동작 낱말은 뒤에 다른 낱말이 붙어 나와도("standingalone") 잡히게 앞부분만 본다.
            string cut = Regex.Replace(
                text,
                @"\s+(?:(?:with|from|in|on|at|by|under|over|amid|amidst|among|atop|near|beside)\b|with(?=[a-z]{3,})|" +
                @"(?:nestled|standing|burning|falling|perched|surrounded|featuring|showing|clad|wearing|dressed|glowing|" +
                @"shining|flickering|crackling|floating|flying|lying|sitting|bustling)).*$",
                "",
                RegexOptions.IgnoreCase);
            if (!IsOnlyFiller(cut)) text = cut;   // "A flying saucer"를 "A"로 만들지 않는다
        }
        text = Regex.Replace(text, @"\s+(?:a|an|the|and|of|with|in|on)$", "", RegexOptions.IgnoreCase);
        return IsOnlyFiller(text) ? phrase : text;
    }

    /// <summary>관사·이음말만 남았거나 비었는지.</summary>
    private static bool IsOnlyFiller(string text) =>
        Regex.IsMatch(text, @"^\s*(?:(?:a|an|the|and|of|with|in|on)\s*)*$", RegexOptions.IgnoreCase);

    /// <summary>항목이 은어 사전("!" 표시는 뗀 것)이나 기본 낱말 사전에 통째로 있으면 그 뜻.</summary>
    /// <param name="context">항목이 들어 있는 전체 입력. 뜻이 여럿인 낱말("눈")은 여기서 단서("겨울")를 찾는다.</param>
    private static string? DictionaryMeaning(string item, SlangDictionary dict, string context)
    {
        string trimmed = item.Trim();
        dict.Annotate(trimmed, out List<Note> found);
        if (found.Count == 1 && found[0].Term == trimmed) return found[0].English;
        // 기본 낱말은 조사가 붙지 않은 낱말 그대로일 때만 쓴다. "고양이랑", "엄마와"처럼 조사가 붙어 있으면
        // 관계(and, with)를 잃지 않게 AI가 옮긴다.
        if (!trimmed.Contains(' '))
            return found.Count > 0 ? null : BasicLexicon.Meaning(trimmed, context, allowParticles: false);   // 은어가 일부 들어 있으면 AI가 옮긴다

        // 여러 낱말 항목도 낱말마다 사전의 뜻이 있으면 그대로 잇는다("벚꽃 배경" → "cherry blossoms background",
        // "검은 머리" → "black hair"). 한국어의 꾸밈말-이름 순서는 영어와 같다.
        List<string> parts = [];
        foreach (string word in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            dict.Annotate(word, out List<Note> slang);
            string? meaning = slang.Count == 1 && slang[0].Term == word ? slang[0].English
                : slang.Count == 0 ? BasicLexicon.Meaning(word, context, allowParticles: false)
                : null;
            if (meaning is null) return null;
            parts.Add(meaning);
        }
        return string.Join(' ', parts);
    }

    /// <summary>
    /// 목록의 한 항목을 옮길 때의 답 꼴: 낱말 cap개 이하의 영어 구 하나. 쉼표·마침표·콜론은 쓸 수 없고,
    /// 낱말 하나도 18글자까지라 띄어쓰기 없이 글자를 끝없이 이어 붙이지 못한다.
    /// </summary>
    private static Grammar ItemGrammar(int cap) => new(
        "root ::= \" \"? word (\" \" word){0," + (cap - 1) + "} \"\\nNOTES:\" rest\n" +
        "word ::= [A-Za-z0-9] [A-Za-z0-9'-]{0,17}\n" +
        "rest ::= [^\\x00]*",
        "root");

    /// <summary>쉼표로 나눈 토막이 둘 이상이고 모두 세 낱말 이하면 목록으로 본다.</summary>
    private static string[]? CommaListItems(string korean)
    {
        string[] segments = korean.Trim().TrimEnd('.', '!', '~').Split(
            [',', '\n', '、', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool isList = segments.Length >= 2 &&
                      Array.TrueForAll(segments, s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3);
        return isList ? segments : null;
    }

    private static readonly string[] GrammarEndings =
        ["는", "은", "을", "를", "에", "의", "로", "서", "고", "며", "게", "줘", "요", "다", "함", "듯", "채", "와", "과", "랑", "하고"];

    private static readonly HashSet<string> PlaceWords = ["위", "아래", "옆", "앞", "뒤", "안", "밖", "속", "사이", "중", "때"];

    /// <summary>
    /// 쉼표 없이 낱말 셋 이상을 늘어놓았고, 조사·어미·꾸밈말로 보이는 낱말이 하나도 없으면 그 낱말들(사전에 있는
    /// 여러 낱말짜리 말은 한 항목). "커피 마시는 남자", "빨간 스포츠카", "책상 위 사과"는 해당하지 않는다.
    /// </summary>
    private static string[]? BareNounWords(string korean, List<Note> glossary)
    {
        string text = korean.Trim().TrimEnd('.', '!', '~');
        if (text.IndexOfAny([',', '\n', '、', '，']) >= 0) return null;

        foreach (Note g in glossary.Where(g => g.Term.Contains(' ')))
            text = text.Replace(g.Term, g.Term.Replace(' ', '\u00A0'), StringComparison.Ordinal);

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3) return null;

        foreach (string word in words)
        {
            if (BasicLexicon.IsWord(word)) continue;   // "사과"처럼 낱말 자체가 어미 모양으로 끝나는 것
            char last = word[^1];
            bool endsWithN = last is >= '가' and <= '힣' && (last - '가') % 28 == 4;   // 받침 ㄴ: 빨간, 입은, 놓인
            if (endsWithN || PlaceWords.Contains(word) || Array.Exists(GrammarEndings, e => word.EndsWith(e, StringComparison.Ordinal)))
                return null;
        }
        return [.. words.Select(w => w.Replace('\u00A0', ' '))];
    }

    /// <summary>이름만 늘어놓은 입력을 옮긴 글에 동작이나 위치 관계가 들어 있는지.</summary>
    private static bool LooksComposed(string english)
    {
        string text = english.ToLowerInvariant();
        return Regex.IsMatch(text, @"\b(?:on|in|at|by|with|near|beside|under|over|behind|among|around|next to|surrounded|perched|nestled)\b") ||
               Regex.Matches(text, @"\b[a-z]{2,}ing\b").Any(m => !IngNouns.Contains(m.Value));
    }

    private static Generation Infer(
        ConvertRequest req, string userMessage, List<Note> glossary, Grammar grammar,
        IProgress<string>? status, IProgress<PromptResult>? partial, CancellationToken ct)
    {
        LLamaWeights model = _model!;
        LLamaContext context = _context!;

        LLamaToken[] tail = context.Tokenize(userMessage + _suffixText, addBos: false, special: true);
        if (_prefixCount + tail.Length + MaxOutputTokens > ContextTokens)
            throw new UserFacingException("입력이 너무 깁니다. 문장을 나눠서 변환해 주세요.");

        status?.Report("AI가 문장을 읽는 중…");
        partial?.Report(BuildResult("", req, glossary, final: false));   // 사전에서 찾은 뜻은 바로 보여 준다

        Stopwatch watch = Stopwatch.StartNew();
        LLamaBatch batch = new();
        int read = SyncTail(context, batch, tail, logitsOnLast: true, ReadChunk, ct);
        int position = _prefixCount + tail.Length;
        double readSeconds = watch.Elapsed.TotalSeconds;
        Trace($"input read: {read} of {tail.Length} tokens in {readSeconds:0.0}s");

        float temperature = DefaultTemperature;
        if (float.TryParse(Environment.GetEnvironmentVariable("KOPROMPT_TEMP"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float forcedTemperature))
        {
            temperature = forcedTemperature;   // 점검용
        }
        using DefaultSamplingPipeline sampler = new()
        {
            Temperature = temperature, TopP = 0.9f, RepeatPenalty = 1.05f, Grammar = grammar,
        };
        StreamingTokenDecoder decoder = new(context);
        StringBuilder sb = new(AnswerStart);
        int tokens = 0;
        int promptTokens = 0;
        double promptSeconds = 0;
        bool promptDone = false;

        while (tokens < MaxOutputTokens)
        {
            ct.ThrowIfCancellationRequested();

            LLamaToken token = sampler.Sample(context.NativeHandle, batch.TokenCount - 1);
            if (token.IsEndOfGeneration(model.Vocab)) break;

            decoder.Add(token);
            sb.Append(decoder.Read());
            tokens++;

            string soFar = sb.ToString();
            if (Array.Exists(StopMarkers, m => soFar.Contains(m, StringComparison.Ordinal))) break;

            if (!promptDone && tokens >= MaxPromptTokens)
            {
                Trace("prompt line stopped: too long");   // 프롬프트 줄을 끝내지 못하고 같은 말을 늘어놓는 중
                break;
            }

            if (!promptDone && PromptLineFinished(soFar))
            {
                promptDone = true;
                promptTokens = tokens;
                promptSeconds = watch.Elapsed.TotalSeconds;
                Trace($"prompt line done: {tokens} tokens, {promptSeconds:0.0}s after start");
                partial?.Report(BuildResult(soFar, req, glossary, final: false) with { PromptDone = true });
                if (!req.Explain) break;   // 풀이를 끈 경우 여기서 끝
                status?.Report("풀이를 쓰는 중…");
            }
            else if (tokens % 3 == 0)
            {
                if (!promptDone) status?.Report($"AI가 프롬프트를 쓰는 중… ({tokens}토큰)");
                partial?.Report(BuildResult(soFar, req, glossary, final: false) with { PromptDone = promptDone });
            }

            if (promptDone)
            {
                // 풀이할 것이 없다는 답이 나오면 더 기다리지 않는다.
                // "- 어이없음 => …"처럼 은어가 "없음"으로 끝나는 경우와 헷갈리지 않게 줄 전체를 본다.
                if (soFar.EndsWith("없음", StringComparison.Ordinal) &&
                    LastLine(soFar + "\n").TrimStart('-', ' ') == "없음")
                {
                    break;
                }

                if (tokens - promptTokens >= MaxNoteTokens)
                {
                    Trace("notes stopped: too long");
                    break;
                }

                // 풀이는 "- 말 => 영어" 줄로만 이루어진다. 그런 꼴이 아닌 줄을 쓰기 시작하면 딴소리이므로 끊는다.
                int notesAt = soFar.IndexOf("NOTES:", StringComparison.Ordinal);
                if (notesAt >= 0 && soFar.EndsWith('\n') && LastLine(soFar[notesAt..]) is { Length: > 0 } last &&
                    !last.StartsWith('-') && !last.StartsWith("NOTES", StringComparison.Ordinal))
                {
                    Trace("notes stopped: not a note line");
                    break;
                }

                // 모델이 지침과 달리 사전에 이미 있는 말만 줄줄이 풀이하고 있으면 그만 쓰게 한다.
                if (soFar.EndsWith('\n') && NotesAreRedundant(soFar, glossary))
                {
                    Trace("notes stopped early: only dictionary terms");
                    break;
                }
            }

            batch.Clear();
            batch.Add(token, position++, Seq, true);
            DecodeResult result = context.Decode(batch);
            if (result != DecodeResult.Ok) throw new UserFacingException("AI가 글을 쓰다가 실패했습니다. (" + result + ")");
        }

        double total = watch.Elapsed.TotalSeconds;
        if (!promptDone) promptSeconds = total;
        Trace($"generated {tokens} tokens in {total - readSeconds:0.0}s");
        return new Generation(sb.ToString(), tokens, readSeconds, promptSeconds, total - readSeconds);
    }

    /// <summary>
    /// 모델이 습관처럼 붙이는 부사들. 오른쪽은 입력에 이런 말이 있으면 사용자가 원한 것이므로 지우지 않는다는 단서다.
    /// </summary>
    private static readonly (string Adverb, string[] KoreanCues)[] FillerAdverbs =
    [
        ("casually", ["무심", "캐주얼", "편하게", "여유", "대충"]),
        ("intensely", ["강렬", "뚫어져라", "빤히", "격하게", "심하게", "격렬"]),
        ("intently", ["뚫어져라", "빤히", "집중", "유심히", "골똘"]),
        ("enthusiastically", ["열정", "신나", "열심", "신명"]),
        ("passionately", ["열정", "격정", "뜨겁", "진하게", "진한"]),
        ("dramatically", ["극적", "드라마틱", "과장"]),
        ("uncontrollably", ["주체", "걷잡을", "미친 듯", "미친듯"]),
        ("smugly", ["우쭐", "의기양양", "거만", "득의", "잘난"]),
        ("eagerly", ["열심", "간절", "허겁지겁"]),
        ("happily", ["행복", "즐겁", "즐거", "신나", "기쁘", "기쁜", "해맑", "좋아"]),
        ("seductively", ["유혹", "요염", "섹시", "야하", "야한", "야릇", "꼴릿", "농염", "관능"]),
        ("deeply", ["깊"]),
        ("wildly", ["미친", "격렬", "광란", "신나게", "마구", "막"]),
        ("brightly", ["환하게", "환한", "밝게", "밝은", "활짝", "해맑"]),
        ("sadly", ["슬프", "슬픈", "우울", "서럽", "쓸쓸"]),
        ("patiently", ["참을성", "인내", "차분"]),
        ("proudly", ["자랑", "뿌듯", "당당"]),
        ("gracefully", ["우아", "사뿐"]),
        ("elegantly", ["우아", "고상", "기품"]),
        ("confidently", ["자신", "당당"]),
        ("energetically", ["활기", "힘차", "신나", "기운"]),
        ("aggressively", ["공격", "거칠", "과격", "사납", "위협"]),
        ("meticulously", ["꼼꼼", "정성", "가지런"]),
        ("excitedly", ["신나", "신난", "흥분", "들뜬", "들떠"]),
        ("diligently", ["열심", "부지런", "성실"]),
        ("sincerely", ["진심", "진지"]),
        ("sternly", ["엄하", "엄격", "호되", "무섭게"]),
        ("joyfully", ["기쁘", "기쁜", "즐겁", "즐거", "신나", "행복", "해맑"]),
        ("effortlessly", ["가볍게", "손쉽", "가뿐", "쉽게"]),
        ("playfully", ["장난"]),
        ("violently", ["격렬", "세차", "거세", "사납", "세게"]),
        ("nervously", ["긴장", "떨", "초조", "불안", "덜덜", "쫄"]),
        ("discreetly", ["몰래", "슬쩍", "살짝"]),
        ("visibly", []),
        ("clearly", ["또렷", "선명", "뚜렷"]),
    ];

    /// <summary>
    /// 큰 모델이 입력에 없는데 장면을 그럴듯하게 만들려고 덧붙이는 자세·표정·장소·때. 오른쪽은 입력에 이런 말이 있으면
    /// 사용자가 원한 것이므로 지우지 않는다는 단서(정규식)다.
    /// </summary>
    private static readonly (string Phrase, string KoreanCues)[] InventedPhrases =
    [
        (@"(?:standing|sitting|seated|posing|leaning|kneeling|crouching)(?: (?:tall|still|proudly|calmly|quietly|gracefully|confidently))?",
            "서 |서있|서 있|선 |앉|눕|누워|누운|포즈|자세|기대|기댄|무릎|엎드|쪼그|일어|기립"),
        (@"(?:smiling|grinning|laughing|beaming)(?: (?:warmly|brightly|happily|softly|gently))?|with a (?:warm |bright |gentle |soft |big )?smile",
            "웃|미소|쪼개|싱글|방긋|히죽|깔깔|빙그레|헤벌쭉|함박|스마일"),
        (@"(?:on|at) (?:a|the) (?:wooden |small |kitchen |dining |white )?(?:table|desk|counter|countertop)",
            "탁자|테이블|책상|식탁|상 위|카운터|놓인|올려|위에"),
        (@"late (?:at|into the) night|at night|at sunset|at dawn|at dusk|during sunset|in the evening|on a (?:sunny|rainy|snowy) day|under (?:a |the )?(?:starry|night|moonlit) sky",
            "밤|야경|야간|야근|심야|노을|해 지|해지|새벽|저녁|황혼|해질|맑은|햇살|햇빛|비 |비가|비오|비 오|눈 오|눈이|별|달빛|달밤|일몰|석양|일출"),
    ];

    /// <summary>
    /// 모델이 입력에 없는데 습관처럼 덧붙이는 꾸밈을 걷어 낸다. 지침으로 막아도 가끔 붙기 때문에 글자 처리로 한 번 더 거른다.
    /// 지우는 것은 (1) 문장 끝의 "radiating joy", "looking adorable" 같은 구절, (2) 입력에 근거가 없는 부사,
    /// (3) "in a lab setting"의 setting, (4) 괄호. 괄호는 이미지 생성기에서 강조 표시로 읽히므로 쉼표로 풀어 쓴다.
    /// </summary>
    private static string Polish(string english, string korean, List<Note> glossary)
    {
        string text = english;

        // 괄호 안의 덧말("(hanok village)", "(chimyeok)" 같은 되풀이·음차)은 통째로 지운다.
        // 사전에 '!'로 표시된 뜻이 여기서 빠지면 뒤의 EnforceGlossary가 다시 넣는다.
        text = Regex.Replace(text, @"\s*\([^()]*\)", "");
        text = text.Replace("(", "").Replace(")", "");

        // "…, known as a "chon-di"," 처럼 지어낸 별명
        text = Regex.Replace(text, @",?\s*(?:also\s+)?(?:known as|nicknamed|called)\s+(?:an?\s+|the\s+)?[""“][^""”]*[""”],?", "", RegexOptions.IgnoreCase);

        // 입력에 벗었다는 말이 없는데 모델이 지어낸 nude / naked / topless
        string[] nudityCues = ["알몸", "벗", "누드", "나체", "전라", "맨몸", "노출", "상탈", "웃통", "반라", "홀딱", "누디", "naked", "nude"];
        if (!Array.Exists(nudityCues, c => korean.Contains(c, StringComparison.OrdinalIgnoreCase)) &&
            !glossary.Exists(g => Regex.IsMatch(g.English, @"\b(nude|naked|topless|bare)\b", RegexOptions.IgnoreCase)))
        {
            text = Regex.Replace(text, @"\s*\b(?:completely |fully |totally |stark )?(?:nude|naked|topless)\b", "", RegexOptions.IgnoreCase);
        }

        // 문장 끝의 꾸밈 구절 (여러 개 붙어 있으면 차례로)
        for (int i = 0; i < 3; i++)
        {
            string cut = Regex.Replace(
                text,
                @"[,;]\s*(?:and\s+)?(?:radiating|exuding|showcasing|capturing|evoking|creating|adding|emphasizing|highlighting|conveying|reflecting|embodying|suggesting|hinting|expressing|showing respect|reminiscent of|focusing)\b[^,;]*[,.\s]*$",
                "",
                RegexOptions.IgnoreCase);
            cut = Regex.Replace(
                cut,
                @",?\s*(?:and\s+)?looking\s+(?:so\s+|absolutely\s+|incredibly\s+|very\s+)?(?:adorable|cute|emotional|stunning|gorgeous|lovely|charming|cool)[,.\s]*$",
                "",
                RegexOptions.IgnoreCase);
            cut = Regex.Replace(cut, @",?\s*with (?:great|much) (?:enthusiasm|joy|excitement|delight)[,.\s]*$", "", RegexOptions.IgnoreCase);
            cut = Regex.Replace(cut, @",?\s*with an air of [a-z\- ]+[,.\s]*$", "", RegexOptions.IgnoreCase);
            cut = Regex.Replace(cut, @",\s*(?:full of|filled with|clearly|visibly)\s[^,;]*[,.\s]*$", "", RegexOptions.IgnoreCase);
            cut = Regex.Replace(cut, @"\s+in an? (?:enchanting|magical|beautiful|stunning|dramatic|captivating|picturesque) (?:scene|moment|setting)[,.\s]*$", "", RegexOptions.IgnoreCase);
            if (cut == text) break;
            text = cut;
        }

        // 모델이 습관처럼 끼워 넣는 꾸밈 동사: "a cottage nestled in a forest" → "a cottage in a forest",
        // "a cafe lit by warm lighting" → "a cafe with warm lighting"
        text = Regex.Replace(
            text,
            @"\b(?:nestled|tucked|situated|positioned|placed|set) (?=(?:in|on|at|among|amid|between|within|atop|against|beside|near|under)\b)",
            "",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b(?:illuminated|lit|bathed) (?:by|in|with)\b", "with", RegexOptions.IgnoreCase);

        // 입력에 없는 동작이나 주변 풍경을 문장 끝에 덧붙인 것: "a dog in a spacesuit exploring an alien landscape",
        // "a tree standing tall amidst spring blossoms"
        if (!Regex.IsMatch(korean, "탐험|탐사|돌아다니|헤매|모험|여행|떠도|누비|산책"))
        {
            text = Regex.Replace(
                text, @"[,\s]+(?:exploring|wandering|roaming|venturing|traversing|drifting|journeying)\b[^,;]*$", "", RegexOptions.IgnoreCase);
        }
        if (!Regex.IsMatch(korean, "사이|속|가운데|둘러|한가운데|틈"))
            text = Regex.Replace(text, @"[,\s]+(?:amidst|amid|surrounded by)\b[^,;]*$", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b(\w+) standing tall\b", "$1", RegexOptions.IgnoreCase);

        // "… with heavy snow covering everything", "a village enveloped in snow" → "covering everything" 떼기, "enveloped in" → "in"
        text = Regex.Replace(
            text,
            @"\s+(?:covering|blanketing|engulfing|filling) (?:everything|it all|the (?:entire |whole )?(?:scene|area|ground|landscape|village|town|city))\b",
            "",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b(?:enveloped|engulfed|blanketed|shrouded|bathed) in\b", "in", RegexOptions.IgnoreCase);

        // 입력에 근거가 없는 자세·표정·장소·때: "a man standing on a bridge" ← "다리 위의 남자",
        // "five apples on a table" ← "사과 5개", "a cat smiling" ← "고양이". 입력이나 사전의 뜻에 그런 말이 있으면 둔다.
        foreach ((string phrase, string cues) in InventedPhrases)
        {
            if (Regex.IsMatch(korean, cues)) continue;
            if (glossary.Exists(g => Regex.IsMatch(g.English, @"\b(?:" + phrase + @")\b", RegexOptions.IgnoreCase))) continue;
            text = Regex.Replace(text, @",?\s+(?:" + phrase + @")\b", "", RegexOptions.IgnoreCase);
        }

        // "… taking a selfie at a cafe while radiating extreme beauty"
        text = Regex.Replace(
            text,
            @"\s+(?:while |and )?(?:radiating|exuding|evoking|conveying|embodying|showcasing her|showing off her)\b[^,;]*$",
            "",
            RegexOptions.IgnoreCase);

        // 모델이 확신 없이 덧붙이는 "perhaps …", 습관처럼 붙이는 "gazing off into the distance"
        text = Regex.Replace(text, @"[,\s]+(?:perhaps|maybe|possibly)\b.*$", "", RegexOptions.IgnoreCase);
        if (!Regex.IsMatch(korean, "먼 곳|먼곳|멀리|먼산|먼 산|허공|바라보"))
            text = Regex.Replace(text, @"[,\s]+(?:gazing|staring|looking)(?: off)? (?:into|at) the (?:distance|horizon)\b", "", RegexOptions.IgnoreCase);

        // 뜻이 여럿인 낱말의 "window / spear" 표시를 모델이 그대로 베꼈으면 앞의 뜻 하나만 남긴다.
        foreach (Note g in glossary.Where(g => g.English.Contains(" / ", StringComparison.Ordinal)))
            text = text.Replace(g.English, g.English.Split(" / ")[0], StringComparison.OrdinalIgnoreCase);

        // "6개 빼고 5개로"는 5개만 그리라는 말인데, 모델이 "excluding six extra fingers"까지 옮기면 그림에 6이 들어간다.
        if (Regex.IsMatch(korean, "빼고|말고|대신|제외"))
        {
            text = Regex.Replace(
                text,
                @"(?:[,.;]\s*|\s+)(?:exclude|excluding|except(?: for)?|instead of|rather than|without)\b[^,.;]*",
                "",
                RegexOptions.IgnoreCase);
        }
        text = Regex.Replace(text, @"\b(views?) perspectives?\b", "$1", RegexOptions.IgnoreCase);   // "back view perspectives"
        if (!Regex.IsMatch(korean, "디테일|세밀|정교|자세|꼼꼼|섬세"))
            text = Regex.Replace(text, @"\b(?:highly |intricately )?detailed\s+", "", RegexOptions.IgnoreCase);   // "a detailed character sheet"

        // 입력에 근거가 없는 인상·분위기 형용사: "a cozy cafe" → "a cafe" ("cozy and snug"처럼 이어진 것도 함께)
        foreach ((string words, string[] cues) in FillerAdjectives)
        {
            if (Array.Exists(cues, c => korean.Contains(c, StringComparison.Ordinal))) continue;
            if (glossary.Exists(g => Regex.IsMatch(g.English, @"\b(?:" + words + @")\b", RegexOptions.IgnoreCase))) continue;
            text = Regex.Replace(text, @"\b(?:" + words + @")\b(?:,? and \w+)?\s*", "", RegexOptions.IgnoreCase);
        }

        // "in a lab setting" → "in a lab"
        text = Regex.Replace(text, @"\b(in|at|within) (an?|the) ([A-Za-z\- ]+?) setting\b", "$1 $2 $3", RegexOptions.IgnoreCase);

        // 근거 없는 부사. 다만 사용자가 "~하게", "~히" 같은 꾸밈말을 직접 썼다면 어느 부사가 그 뜻인지
        // 가려낼 수 없으므로, 그 문장에서는 부사를 건드리지 않는다(원한 말을 지우는 것보다 꾸밈이 남는 편이 낫다).
        bool userWroteAdverb = Regex.IsMatch(korean, @"[가-힣](?:하게|스럽게|롭게|있게|차게|히)(?=\s|,|\.|$)");
        foreach ((string adverb, string[] cues) in FillerAdverbs)
        {
            if (userWroteAdverb) break;
            if (Array.Exists(cues, c => korean.Contains(c, StringComparison.Ordinal))) continue;
            if (glossary.Exists(g => g.English.Contains(adverb, StringComparison.OrdinalIgnoreCase))) continue;
            text = Regex.Replace(text, @"\s*\b" + adverb + @"\b", "", RegexOptions.IgnoreCase);
        }

        // a / an 바로잡기 ("an motorcycle" → "a motorcycle", "a umbrella" → "an umbrella")
        text = Regex.Replace(text, @"\b([Aa])n (?=[bcdfgjklmnpqrstvwxyz])(?!hour|honest|honor|heir)", "$1 ");
        text = Regex.Replace(text, @"\b([Aa]) (?=[aeio])(?!one\b|once\b)", "$1n ");

        // 지우고 남은 쉼표·공백 정리
        text = Regex.Replace(text, @"\s+", " ");
        text = Regex.Replace(text, @"\s+([,.;])", "$1");
        text = Regex.Replace(text, @",(\s*,)+", ",");
        text = text.Trim(' ', ',', ';');
        text = Regex.Replace(text, @"[,\s]+(?:while|and|with|as|but|in|at|on)$", "", RegexOptions.IgnoreCase);   // 구절을 지우고 남은 꼬리말
        return text.Length == 0 ? english : text;
    }

    private static readonly HashSet<string> MinorWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "with", "for", "from", "into", "over", "having", "being", "wearing", "giving", "getting",
        "style", "position", "look", "looking", "through", "only", "one", "her", "his", "their",
    };

    /// <summary>
    /// 사전에 '!'로 표시된 뜻(성적인 말처럼 모델이 스스로 돌려 말하기 쉬운 것)이 프롬프트에 들어 있는지 본다.
    /// 그 뜻의 핵심 낱말이 없으면 모델이 빼거나 순화한 것이므로, 사전의 영어를 프롬프트 끝에 그대로 덧붙인다.
    /// </summary>
    /// <param name="allSlang">
    /// true면 '!' 표시가 없는 은어 사전의 뜻도 빠졌으면 덧붙인다. 빠른 모드의 작은 모델은 긴 문장에서
    /// "메이드복(=maid outfit)" 같은 뜻을 통째로 빠뜨리는 일이 잦다. 큰 모델은 비슷한 말로 바꿔 쓰는 일이 많아서
    /// (extremely beautiful → stunningly beautiful) 켜면 같은 뜻이 겹쳐 붙으므로 쓰지 않는다.
    /// </param>
    private static string EnforceGlossary(string english, List<Note> glossary, bool allSlang = false)
    {
        foreach (Note g in glossary)
        {
            // 사람 낱말(소녀, 할머니…)은 그림의 주인공이라 빠지면 덧붙인다. 그 밖의 기본 낱말은 건드리지 않는다.
            bool slang = !g.Explanation.StartsWith(BasicLexicon.Marker, StringComparison.Ordinal);
            bool must = g.Explanation == SlangDictionary.MustAppear || g.Explanation == BasicLexicon.MustMarker;
            if (!must && !(allSlang && slang)) continue;

            // 빠른 모드에서 채워 넣는 일반 은어는, 뜻의 핵심 낱말(5글자 이상) 가운데 하나라도 이미 있으면 옮긴 것으로 본다.
            // ("fried chicken and beer" ← "chicken and beer", "cocky delinquent school bully" ← "delinquent")
            if (!must && Regex.Matches(g.English, "[A-Za-z]{5,}").Select(m => m.Value)
                    .Any(w => !MinorWords.Contains(w) && Regex.IsMatch(english, @"\b" + Regex.Escape(w[..5]), RegexOptions.IgnoreCase)))
            {
                continue;
            }

            // 뜻의 첫 핵심 낱말(예: "doggy style sex position"의 doggy)이 프롬프트에 있으면 제대로 옮긴 것으로 본다.
            string? key = Regex.Matches(g.English, "[A-Za-z]{3,}")
                .Select(m => m.Value)
                .FirstOrDefault(w => !MinorWords.Contains(w));
            if (key is null) continue;

            if (Mentions(english.Replace("-", ""), key)) continue;   // bra-less 와 braless 를 같은 말로 본다

            // 사람 낱말은 어근(앞 네 글자)만 맞아도 옮긴 것으로 본다: "slimy" ↔ "slime", "elderly gentleman" ↔ "elderly person".
            if (g.Explanation == BasicLexicon.MustMarker &&
                Regex.Matches(g.English, "[A-Za-z]{4,}").Select(m => m.Value).Where(w => !MinorWords.Contains(w))
                    .Any(w => Regex.IsMatch(english, @"\b" + Regex.Escape(w[..4]), RegexOptions.IgnoreCase)))
            {
                continue;
            }

            english = english.TrimEnd('.', ',', ' ') + ", " + g.English;
        }
        return english;
    }

    /// <summary>
    /// 프롬프트에 그 낱말이 들어 있는지. 앞 다섯 글자만 견주어 어미가 달라도 맞게 하고(masturbating / masturbates),
    /// 불규칙 복수도 같은 말로 본다("남자 둘" → "two men"이면 man이 있는 것).
    /// </summary>
    private static bool Mentions(string english, string key)
    {
        string stem = key.Length > 5 ? key[..5] : key;
        if (Regex.IsMatch(english, @"\b" + Regex.Escape(stem), RegexOptions.IgnoreCase)) return true;
        // 불규칙 복수와, 같은 것을 가리키는 흔한 다른 말(moon ↔ lunar, dog ↔ puppy …)
        string? alternative = key.ToLowerInvariant() switch
        {
            "man" => "men", "woman" => "women", "child" => "children|kid", "person" => "people",
            "mouse" => "mice", "goose" => "geese", "elderly" => "old",
            "moon" => "lunar", "sun" => "solar", "sea" => "ocean|marine", "dog" => "pupp|canine",
            "cat" => "kitten|feline", "kid" => "child", "house" => "home", "car" => "vehicle",
            "grandfather" => "grandpa|old man|elderly man|elderly gentleman|old gentleman",
            "grandmother" => "grandma|old woman|elderly woman|elderly lady|old lady",
            _ => null,
        };
        return alternative is not null && Regex.IsMatch(english, @"\b(?:" + alternative + ")", RegexOptions.IgnoreCase);
    }

    /// <summary>풀이 줄의 한국어 표현에서 모델이 베껴 적은 "(=영어)" 표시를 떼어 낸다.</summary>
    private static string CleanTerm(string term) => Regex.Replace(term, @"\(=.*?\)", "").Trim(' ', ')', '(');

    /// <summary>지금까지 쓴 풀이가 두 줄 이상인데 전부 사전에서 이미 찾은 말인지.</summary>
    private static bool NotesAreRedundant(string soFar, List<Note> glossary)
    {
        (_, List<Note> notes) = Parse(soFar);
        return notes.Count >= 2 && notes.TrueForAll(n => CoveredByGlossary(CleanTerm(n.Term), glossary));
    }

    /// <summary>"PROMPT: ..." 줄이 끝나고 다음 줄로 넘어갔는지.</summary>
    private static bool PromptLineFinished(string soFar)
    {
        int start = soFar.IndexOf(AnswerStart, StringComparison.Ordinal);
        if (start < 0) return false;
        string body = soFar[(start + AnswerStart.Length)..].TrimStart();
        return body.Length > 0 && body.Contains('\n');
    }

    /// <summary>
    /// 빠른 모드의 작은 모델이 긴 문장에서 통째로 빠뜨린 그림 요소를 채운다. 입력 속 기본 낱말을 명사구로 묶고
    /// ("고양이 귀" → cat ears, "빨간 드레스" → red dress), 그 중심 낱말(맨 뒤 낱말)이 프롬프트에 없으면 명사구를 덧붙인다.
    /// 큰 모델은 비슷한 말로 바꿔 쓰는 일이 많아(dog → puppy) 같은 것이 겹쳐 붙으므로 빠른 모드에서만 쓴다.
    /// </summary>
    private static string EnsureCoverage(string english, string korean)
    {
        string text = NormalizeRequest(korean);
        List<List<string>> phrases = [];
        List<string> current = [];
        foreach (string token in Regex.Split(text, @"[\s,]+").Where(t => t.Length > 0))
        {
            string word = token.Trim('.', '!', '?', '~', '"', '\'', '(', ')');
            string? bare = BasicLexicon.Meaning(word, text, allowParticles: false);
            string? withParticle = bare ?? BasicLexicon.Meaning(word, text);
            if (withParticle is null)   // 사전에 없는 말에서 명사구가 끊긴다
            {
                if (current.Count > 0) phrases.Add(current);
                current = [];
                continue;
            }
            current.Add(withParticle);
            if (bare is null)           // 조사가 붙은 낱말이 명사구의 끝
            {
                phrases.Add(current);
                current = [];
            }
        }
        if (current.Count > 0) phrases.Add(current);

        foreach (List<string> phrase in phrases)
        {
            // 중심 낱말의 뜻("elderly person", "slime monster") 가운데 핵심 낱말 하나라도 있으면 옮긴 것으로 본다.
            string head = phrase[^1];
            string[] keys = [.. Regex.Matches(head, "[A-Za-z]{3,}").Select(m => m.Value).Where(w => !MinorWords.Contains(w))];
            if (keys.Length == 0) continue;
            string key = keys[^1];
            string plain = english.Replace("-", " ");
            if (keys.Any(k => (k.Length >= 4 || k == key) && Mentions(plain, k.Length > 5 ? k : k.TrimEnd('s')))) continue;
            Trace($"coverage added \"{string.Join(' ', phrase)}\" (key {key}) to: {english}");
            english = english.TrimEnd('.', ',', ' ') + ", " + string.Join(' ', phrase);
        }
        return english;
    }

    /// <summary>
    /// 모델이 문장형 프롬프트를 "Medieval Knight Character Sheet"처럼 제목 꼴로 쓰면 첫 글자만 남기고 소문자로 바꾼다.
    /// 고유명사(Korean, Han River, Rembrandt…)는 되살린다.
    /// </summary>
    private static string FixTitleCase(string english)
    {
        string[] words = [.. Regex.Matches(english, "[A-Za-z]{3,}").Select(m => m.Value)];
        if (words.Length < 3 || words.Count(w => char.IsUpper(w[0])) < words.Length * 0.6) return english;

        string text = english[..1] + english[1..].ToLowerInvariant();
        foreach (string proper in ProperNouns)
            text = Regex.Replace(text, @"\b" + Regex.Escape(proper) + @"\b", proper, RegexOptions.IgnoreCase);
        return text;
    }

    private static readonly string[] ProperNouns =
    [
        "Korean", "Korea", "Japanese", "Japan", "Chinese", "China", "English", "Han River", "Seoul", "Busan", "Rembrandt",
        "Studio Ghibli", "Ghibli", "Pixar", "Disney", "Unreal Engine", "Milky Way", "Earth", "Christmas", "Halloween",
        "Buddhist", "Shinto", "Lovecraftian", "American", "Renaissance",
    ];

    private static PromptResult BuildResult(string raw, ConvertRequest req, List<Note> glossary, bool final)
    {
        (string english, List<Note> aiNotes) = Parse(raw);

        // 프롬프트 줄이 다 써졌으면, 사전에 '!'로 표시된 뜻이 빠지거나 순화되지 않았는지 확인해서 채워 넣는다.
        bool tags = req.Format == PromptFormat.Tags;
        bool lineDone = english.Length > 0 && (final || PromptLineFinished(raw));
        if (lineDone) english = Polish(english, req.Korean, glossary);
        if (tags) english = ToTags(english, req.Korean, glossary);
        if (lineDone)
        {
            english = EnforceGlossary(english, glossary, allSlang: FastMode);
            if (FastMode && final) english = EnsureCoverage(english, req.Korean);
            if (tags) english = english.ToLowerInvariant();
            else english = FixTitleCase(english);
        }

        // 풀이: 사전에서 찾은 뜻을 먼저, 그다음 AI가 스스로 풀어낸 말.
        List<Note> notes = [];
        foreach (Note g in glossary.Where(g => !g.Explanation.StartsWith(BasicLexicon.Marker, StringComparison.Ordinal)))
            notes.Add(g with { Explanation = "사전" });
        foreach (Note n in aiNotes)
        {
            // 모델이 "(=영어)" 표시까지 베껴 적거나 풀이 대신 긴 설명을 늘어놓은 줄은 버린다.
            string term = CleanTerm(n.Term);
            if (term.Length == 0 || n.English.Length > 80 || n.English.Contains("(=", StringComparison.Ordinal)) continue;

            // 풀이는 짧은 영어 구여야 한다. "focusing on exactly five fingers to exclude any ambiguity." 같은 설명문은 버린다.
            if (n.English.TrimEnd().EndsWith('.') || n.English.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 7) continue;

            // 모델이 평범한 말을 풀이하면 "Space station", "Visible through"처럼 대문자로 시작하는 일이 많다(은어 풀이는 소문자).
            if (char.IsUpper(n.English[0]) && !ProperNouns.Any(p => n.English.StartsWith(p, StringComparison.Ordinal))) continue;

            // 입력에 없는 말을 풀이했다면 모델이 지어낸 것이다(작은 모델에서 가끔 생긴다).
            if (!req.Korean.Contains(term, StringComparison.Ordinal)) continue;

            if (!CoveredByGlossary(term, glossary) && !notes.Exists(x => x.Term == term))
                notes.Add(new Note(term, n.English, "AI 해석"));
        }

        if (req.Format == PromptFormat.Varco3D)
        {
            // VARCO 3D용: 번역문을 3D로 바꾸기 좋은 이미지 프롬프트 틀에 넣고, VARCO에서 고를 설정을 안내한다.
            (string prompt, string guide) = english.Length == 0
                ? ("", "")
                : Varco3D.Compose(english, req.Korean, req.Varco ?? Default3D);
            return new PromptResult(prompt, "", notes, "", english, final, guide);
        }

        return new PromptResult(
            english.Length == 0 ? "" : PromptComposer.Compose(english, req),
            req.Negative ? DefaultNegative : "",
            notes,
            "",
            english,
            final);
    }

    private static readonly Varco3DOptions Default3D = new(Subject3D.Auto, Pose3D.TPose, View3D.Auto, 0, 0, "자동");

    // ── 태그형으로 쪼갠 뒤에 걸러 내는, 모델이 습관처럼 덧붙이는 말 ──
    // 오른쪽은 입력에 이런 말이 있으면 사용자가 원한 것이므로 건드리지 않는다는 단서다(FillerAdverbs와 같은 방식).

    /// <summary>태그가 이 말로 끝나면 태그째 버린다. "golden hour lighting", "serene atmosphere", "heroic pose" 따위.</summary>
    private static readonly (string Words, string[] KoreanCues)[] TagDropEndings =
    [
        ("lighting|illumination|glow|golden hour", ["조명", "빛", "라이팅", "햇살", "역광", "네온", "골든아워", "황금"]),
        ("atmosphere|ambiance|ambience|mood|vibes?|feel|feeling", ["분위기", "무드", "느낌", "감성", "바이브"]),
        ("expression", ["표정", "웃", "미소", "울", "찡그", "화난", "화가", "놀란", "놀라", "인상"]),
        ("pose|posture|stance", ["포즈", "자세"]),
        ("attire|clothing|clothes", ["옷", "차림", "복장", "의상", "패션", "입은", "입고", "걸친"]),
        ("appearance|individual|figure", ["외모", "생김", "외형", "피규어", "인형", "실루엣"]),
    ];

    /// <summary>태그 끝에 붙은 이 말만 떼어 낸다. "cafe setting" → "cafe", "rainy street backdrop" → "rainy street".</summary>
    private static readonly (string Words, string[] KoreanCues)[] TagTailFillers =
    [
        ("setting|surroundings|environment|location|area", []),
        ("background|backdrop", ["배경", "뒤에", "뒤로", "뒤편"]),
        ("scene", ["장면", "씬"]),
        ("perspectives?", ["원근", "투시", "시점"]),
    ];

    /// <summary>태그 안에서 이 말만 지운다. 지우고 남는 것이 없으면 태그도 사라진다.</summary>
    private static readonly (string Words, string[] KoreanCues)[] TagFillerWords =
    [
        (@"close-?up(?: detail)?(?: view| shot)?(?: of)?", ["클로즈업", "가까이", "확대", "접사", "근접"]),
        (@"(?:highly |extremely |ultra[- ]?)?detailed|intricate(?: details?)?", ["디테일", "세밀", "정교", "자세", "꼼꼼", "섬세"]),
        (@"masterpiece|(?:best|high) quality|high resolution|sharp focus|[48]k|u?hd", ["고화질", "고퀄", "퀄리티", "선명", "또렷", "해상도"]),
        (@"photo-?realistic|hyper-?realistic|ultra[- ]?realistic|realistic", ["사실", "실사", "리얼", "진짜"]),
    ];

    /// <summary>
    /// 모델이 장면에 습관처럼 붙이는 인상·분위기 형용사. 태그형뿐 아니라 문장형에서도(Polish) 입력에 근거가 없으면 지운다.
    /// 위의 TagFillerWords(구도·품질 용어)는 문장에서 지우면 문장이 어색해지므로 태그형에서만 지운다.
    /// </summary>
    private static readonly (string Words, string[] KoreanCues)[] FillerAdjectives =
    [
        ("serene|peaceful|tranquil|calm", ["평화", "고요", "잔잔", "차분", "한적", "평온"]),
        ("picturesque|charming|quaint|idyllic", ["그림 같은", "그림같은", "아름다운", "예쁜", "아기자기", "정겨운", "목가", "운치"]),
        ("cozy", ["아늑", "포근", "따뜻", "따스"]),
        ("dreamy|ethereal|whimsical", ["몽환", "꿈", "동화"]),
        ("epic|majestic", ["웅장", "장엄", "에픽", "위엄", "장대"]),
        ("heroic", ["영웅", "용맹", "늠름"]),
        ("dramatic|cinematic", ["극적", "드라마틱", "시네마틱", "영화"]),
        ("romantic", ["로맨틱", "낭만", "연인", "사랑", "커플"]),
        ("moody", ["분위기", "무드", "우울", "음울"]),
        ("mysterious", ["신비", "미스터리", "수상"]),
        ("rustic", ["시골", "투박", "소박", "낡은", "허름"]),
        ("secluded", ["외딴", "한적", "인적"]),
        ("solitary|lonely", ["혼자", "홀로", "외로", "쓸쓸", "고독"]),
        ("casual", ["캐주얼", "편한", "편하게", "무심", "평상"]),
        ("playful", ["장난"]),
        ("vibrant", ["화려", "알록", "선명", "쨍"]),
        ("natural", ["자연"]),
    ];

    /// <summary>뜻을 보태지 않는 낱말. 태그가 앞 태그의 되풀이인지 볼 때 이 낱말들은 셈에서 뺀다.</summary>
    private static readonly HashSet<string> EmptyTagWords =
    [
        "a", "an", "the", "of", "in", "on", "at", "by", "with", "and", "to", "for", "from",
        "view", "scene", "style", "wearing", "shown", "visible", "included", "including", "captured", "seen", "depicted", "styled",
    ];

    /// <summary>
    /// 모델이 쓴 영어 문장을 쪼개서 태그형 프롬프트("소문자 구, 소문자 구, …")로 만든다.
    /// 모델에게 태그를 직접 쓰게 하지 않는 까닭: 문장으로는 입력을 충실히 옮기는데, 태그로 쓰게 하면 태그 줄을
    /// 그럴듯한 길이로 채우려고 입력에 없는 것을 덧붙인다("고양이" → cat, feline, sleeping, playful /
    /// "해변에서 서핑하는 남자" → man surfing, ocean waves, sandy shore). 지침·예시·태그 수 제한으로도 막지 못했다.
    /// 문장을 쪼개기만 하면 문장에 없는 태그는 생길 수 없고, 형식만 바꿀 때는 AI를 다시 돌릴 필요도 없다.
    /// 쪼갠 뒤에는 입력에 근거가 없는 꾸밈말(조명·분위기·포즈·배경 따위)과 되풀이된 태그를 뺀다.
    /// </summary>
    private static string ToTags(string sentence, string korean, List<Note> glossary)
    {
        string text = sentence.ToLowerInvariant().Trim().TrimEnd('.');

        // 문장 머리의 군말: "creating a detailed illustration of five fingers …"
        text = Regex.Replace(text, @"^(?:creating|depicting|showing|featuring|generating|drawing|making)\s+", "");
        text = Regex.Replace(text, @"^(?:an?\s+|the\s+)?(?:(?:highly\s+)?detailed\s+)?(?:illustration|image|picture|drawing|depiction)\s+of\s+", "");

        // 쪼개는 자리를 쉼표로 바꾼다.
        text = Regex.Replace(text, @"\s*[;:]\s*|\.\s+|\s+[-–—]\s+", ", ");
        text = Regex.Replace(text, @"\s+(?:with|while|featuring|showing|showcasing|including|depicting)\s+", ", ");
        text = Regex.Replace(text, @"\s+and\s+(?=(?:an?|the)\s|\w+ing\b)", ", ");   // "black and white", "chicken and beer"는 그대로
        text = Regex.Replace(text, @"\s+(?=(?:wearing|holding|carrying)\s)", ", ");
        // "depicted in a …", "rendered in …", "set against a …", "viewed from a …" 같은 이음말은 떼어 낸다.
        text = Regex.Replace(
            text,
            @"\s+(?:depicted|rendered|captured|shown|painted|drawn|illustrated|presented|set|viewed|seen)\s+(?:(?:in|with|against|as)\s+|from\s+(?=(?:an?|the)\s))",
            ", ");

        // 장소·때를 붙이는 "in a cafe", "on a rainy night"는 전치사를 떼고 따로 세운다. 다만 "sitting on a bench",
        // "looking at the camera"처럼 동작에 딸린 것과 "in front of"는 한 덩어리로 둔다. 위치 관계(under, over, by …)도 쪼개지 않는다.
        text = Regex.Replace(
            text,
            @"(?<!\b(?:sitting|standing|lying|leaning|kneeling|riding|looking|smiling|staring|glaring|gazing|pointing|waving|aiming|shooting|yelling|shouting|laughing|knocking|stepping|sleeping|resting|perched|seated))" +
            @"\s+(?:in|on|at|along|during|within|inside)\s+(?=(?:an?|the)\s)",
            ", ");
        text = Regex.Replace(text, @"\s+during\s+", ", ");
        text = Regex.Replace(text, @"\s+against\s+(?:(?:an?|the)\s+)?", ", ");   // "bob haircut against a night view background"
        text = Regex.Replace(text, @"\s+and\s+(?=(?:his|her|their|its|two|three)\s)", ", ");   // "playing the piano and his granddaughter"
        text = Regex.Replace(text, @"\s+(?=engaged in\s)", ", ");                               // "wielding a sword engaged in combat"

        bool userExcluded = Regex.IsMatch(korean, "빼고|말고|대신|제외");
        HashSet<string> seenWords = [];

        // 입력이나 사전의 뜻에 근거가 있는 말은 그대로 둔다.
        bool Wanted(string words, string[] cues) =>
            Array.Exists(cues, c => korean.Contains(c, StringComparison.Ordinal)) ||
            glossary.Exists(g => Regex.IsMatch(g.English, @"\b(?:" + words + @")\b", RegexOptions.IgnoreCase));

        // 토막마다 머리의 관사·"she has" 따위와 꼬리의 "to highlight the structure …" 같은 군말을 떼고, 동작이 둘이면 나눈다.
        IEnumerable<string> parts = text.Split(',')
            .Select(p => Regex.Replace(
                p.Trim(' ', '.'), @"^(?:(?:and|or|an?|the|(?:she|he|it) (?:has|is)|they (?:have|are)|there (?:is|are))\s+)+", ""))
            .Select(p => Regex.Replace(
                p,
                @"\s+(?:to (?:highlight|showcase|emphasize|capture|create|convey|illustrate|demonstrate|show)|" +
                @"highlighting|emphasizing|accentuating|radiating|exuding|evoking|conveying|embodying|suggesting|creating|adding)\b.*$",
                ""))
            .SelectMany(SplitAtActions);

        List<string> tags = [];
        List<string> untouched = [];
        foreach (string part in parts)
        {
            string tag = part;
            if (tag.Length == 0) continue;

            // "radiating extreme beauty"처럼 장면에 없는 인상을 덧붙인 토막
            if (Regex.IsMatch(tag, @"^(?:radiating|exuding|evoking|conveying|embodying|suggesting|hinting|adding|creating|focusing|focus on|emphasis on|exclude|excluding)\b")) continue;
            if (!untouched.Contains(tag)) untouched.Add(tag);

            foreach ((string words, string[] cues) in TagFillerWords.Concat(FillerAdjectives))
            {
                if (!Wanted(words, cues)) tag = Regex.Replace(tag, @"\b(?:" + words + @")\b\s*", "");
            }
            foreach ((string words, string[] cues) in TagDropEndings)
            {
                if (!Wanted(words, cues) && Regex.IsMatch(tag, @"\b(?:" + words + @")$")) tag = "";
            }
            foreach ((string words, string[] cues) in TagTailFillers)
            {
                if (!Wanted(words, cues)) tag = Regex.Replace(tag, @"(?:^|\s+)(?:" + words + @")$", "");
            }

            // "6개 빼고 5개로"는 5개만 그리라는 말인데, 모델이 "excluding six"까지 태그로 옮기면 그림에 6이 들어간다.
            if (userExcluded) tag = Regex.Replace(tag, @"\s*\b(?:excluding|except(?: for)?|instead of|rather than)\b.*$", "");

            tag = Regex.Replace(tag, @"\s+", " ").Trim();
            tag = Regex.Replace(tag, @"^(?:of|with|and|in|at|on) ", "");            // 앞말을 지우고 남은 머리
            tag = Regex.Replace(tag, @" (?:of|with|and|in|at|on|a|an|the)$", "");   // 뒷말을 지우고 남은 꼬리

            // 앞 태그들에 이미 나온 낱말뿐이면 같은 말의 되풀이다.
            if (tag.Length == 0) continue;
            // 낱말은 앞 네 글자로 견준다: "slimy monster" 뒤의 "slime monster"도 같은 말의 되풀이로 본다.
            string[] content = [.. tag.Split(' ').Where(w => !EmptyTagWords.Contains(w)).Select(w => w.Length >= 5 ? w[..4] : w)];
            if (Array.TrueForAll(content, seenWords.Contains)) continue;

            tags.Add(tag);
            seenWords.UnionWith(content);
        }
        return string.Join(", ", tags.Count > 0 ? tags : untouched);   // 전부 걸러졌다면 거르지 않은 것을 쓴다
    }

    /// <summary>"-ing"로 끝나지만 동작이 아닌 낱말. 이 앞에서는 태그를 나누지 않는다.</summary>
    private static readonly HashSet<string> IngNouns =
    [
        "during", "being", "lighting", "clothing", "building", "painting", "drawing", "rendering", "ceiling", "evening",
        "morning", "thing", "something", "nothing", "anything", "everything", "string", "spring", "swing", "sibling",
        "darling", "wedding", "stocking", "legging", "railing", "setting", "feeling", "opening", "crossing", "dwelling",
        "housing", "boxing", "viking", "pudding", "dumpling",
    ];

    /// <summary>이 낱말 바로 뒤의 "-ing"는 동작이 아니라 꾸밈말이거나("a smiling woman") 앞말에 딸린 것이다("by reading").</summary>
    private static readonly HashSet<string> IngBlockers =
    [
        "a", "an", "the", "of", "and", "or", "by", "for", "to", "from", "with", "without", "is", "are", "while", "no", "not",
        "very", "extremely", "insanely", "absolutely", "long", "short", "his", "her", "their", "its", "some", "two", "three", "many",
        "in", "on", "at", "under", "over", "through", "into", "after", "before", "near", "behind", "beside", "amid", "against", "like", "as",
    ];

    /// <summary>
    /// 한 토막에 대상과 동작, 또는 동작이 둘 들어 있으면 동작(-ing) 앞에서 나눈다.
    /// "wearing glasses reading a newspaper" → "wearing glasses" / "reading a newspaper".
    /// 짧은 토막은 앞에 두 낱말 이상이 있을 때만 나누므로 "man sipping coffee", "long flowing hair"는 그대로다.
    /// </summary>
    private static IEnumerable<string> SplitAtActions(string chunk)
    {
        string[] words = chunk.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int start = 0;
        for (int i = 1; i < words.Length; i++)
        {
            string word = words[i];
            // 앞에 낱말이 하나뿐이면 토막이 길 때만 나눈다 ("uniform standing under cherry blossom trees").
            bool enoughBefore = i - start >= 2 || (i - start == 1 && words.Length - start >= 5);
            if (!enoughBefore || word.Length < 5 || !word.EndsWith("ing", StringComparison.Ordinal)) continue;
            if (IngNouns.Contains(word) || IngBlockers.Contains(words[i - 1])) continue;

            yield return string.Join(' ', words[start..i]);
            start = i;
        }
        yield return string.Join(' ', words[start..]);
    }

    /// <summary>AI가 적은 풀이가 사전에서 이미 찾은 말뿐이면(붙여 쓴 경우 포함) 다시 보여 줄 필요가 없다.</summary>
    private static bool CoveredByGlossary(string term, List<Note> glossary)
    {
        string rest = term;
        foreach (Note g in glossary.OrderByDescending(g => g.Term.Length))
            rest = rest.Replace(g.Term, "", StringComparison.Ordinal);
        return rest.Trim().Length <= 1;
    }

    /// <summary>
    /// 모델에게 줄 사용자 메시지(입력 문장). 사전에서 찾은 뜻은 그 은어 바로 뒤에
    /// "은어(=영어)" 꼴로 끼워 넣는다. 문장을 이어서 치면 이 글도 뒤에만 덧붙으므로 미리 읽혀 둘 수 있다.
    /// </summary>
    private static string BuildHead(string korean, SlangDictionary dict, out List<Note> glossary)
    {
        string annotated = dict.Annotate(NormalizeRequest(korean), out glossary);

        // 여러 낱말짜리 은어("눈 돌아간")의 안쪽 낱말에는 기본 뜻을 붙이지 않는다. 그 은어의 띄어쓰기를
        // 잠시 보이지 않는 글자(U+2060)로 바꿔 한 낱말로 묶어 두었다가 되돌린다.
        foreach (Note g in glossary.Where(g => g.Term.Contains(' ')))
            annotated = annotated.Replace(g.Term + "(=", g.Term.Replace(' ', '⁠') + "(=", StringComparison.Ordinal);
        annotated = BasicLexicon.Annotate(annotated, glossary).Replace('⁠', ' ');   // 흔한 낱말에도 뜻을 붙인다(해석 칸에는 안 보임)
        return ClarifyLayering(annotated);
    }

    /// <summary>
    /// 요청 말투를 걷어 내고 그림 내용만 남긴다. 모델은 "배경은 야경으로 하고 머리는 단발로 해줘"를 그대로 받으면
    /// "short hair as background scenery of city lights"처럼 엮어 버리고, "고양이 그림 그려줘"는 "cat drawing"으로 옮긴다.
    /// 그래서 "야경 배경, 단발 머리", "고양이"처럼 그림 이름만 남겨 넘긴다.
    /// </summary>
    internal static string NormalizeRequest(string korean)
    {
        string text = korean.Trim().Replace("\r\n", "\n");

        // "손가락5개" → "손가락 5개" (붙여 쓰면 사전에서 낱말을 찾지 못한다)
        // 수 세는 말 앞에서만 띄운다. "중2병", "재벌2세" 같은 은어는 건드리지 않는다.
        text = Regex.Replace(text, @"([가-힣])(\d+\s*(?:개|명|마리|송이|그루|채|대|장|권|잔|켤레|벌|쌍|살))", "$1 $2");   // "병"은 "중2병" 때문에 뺐다

        // "고양이 그림 그려줘" → "고양이": 요청 동사 바로 앞의 그림·이미지는 요청의 일부다("사진"은 사진풍이라는 뜻이라 남긴다)
        text = Regex.Replace(text, @"\s*(?:그림|이미지|일러스트)(?:으로|로|을|를)?(?=\s*(?:그려|만들어|뽑아|생성해))", "");

        // 요청 동사: "그려줘", "만들어 주세요", "뽑아줘"
        text = Regex.Replace(text, @"\s*(?:그려|만들어|뽑아|생성해)\s*(?:줘|주세요|줄래|봐|주라|줄 수 있어\??)[.!~?]*(?=\s|,|$)", ",");

        // "~를 만드는데", "~ 그리는데"
        text = Regex.Replace(text, @"\s*(?:만드는데|그리는데|만들 건데|그릴 건데|만들건데|그릴건데)(?=\s|,|$)", ",");

        // "배경은 야경으로 하고" → "야경 배경,", "머리는 단발로 해줘" → "단발 머리,", "옷은 교복으로" → "교복,"
        text = Regex.Replace(
            text,
            // k는 낱말 첫머리에서 시작하는 두 글자 이상(또는 "옷")이라야 한다: "검은 단발로"의 "검"을 k로 읽지 않게.
            // v는 여러 낱말이어도 된다: "머리는 검은 단발로" → "검은 단발 머리".
            @"(?<=^|[\s,])(?<k>옷|[가-힣]{2,})[은는] (?<v>[^,\n]+?)(?:으로|로) (?:해 ?주고|해 ?줘|해 ?주세요|해서|하고|해라|해|바꿔 ?줘|바꾸고)(?=\s|,|$)",
            m => m.Groups["k"].Value is "옷" or "의상" or "복장" or "차림" or "옷차림"
                ? m.Groups["v"].Value + ","
                : m.Groups["v"].Value + " " + m.Groups["k"].Value + ",");

        // "옆에 강아지 한 마리 있게 해줘" → "옆에 강아지 한 마리", "다 보이게 해줘" → "다 보이게"
        text = Regex.Replace(text, @"\s*있게 해 ?(?:줘|주세요)[.!~]*", ",");
        text = Regex.Replace(text, @"(?<=게) 해 ?(?:줘|주세요)[.!~]*", ",");

        text = Regex.Replace(text, @"\s*,(?:\s*,)+", ",");
        text = Regex.Replace(text, @"\s+,", ",");
        return text.Trim().TrimEnd(',').Trim();
    }

    /// <summary>
    /// "후드티 위에 청자켓 입은"을 모델이 자꾸 거꾸로(hoodie over a denim jacket) 옮긴다.
    /// 영어 어순과 같은 "청자켓을 후드티 위에 겹쳐 입은"으로 바꿔서 넘긴다. 낱말 뒤에 붙은 사전 뜻 "(=…)"는 그대로 따라간다.
    /// </summary>
    private static string ClarifyLayering(string annotated) =>
        Regex.Replace(
            annotated,
            @"(?<under>[^\s,()]+(?:\(=[^)]*\))?) 위에 (?<over>[^\s,()]+(?:\(=[^)]*\))?)(?:을|를)? (?<verb>걸친|입은|걸치고|입고|걸쳐 입은|걸쳐입은)",
            "${over}을 ${under} 위에 겹쳐 ${verb}");

    private static (string, List<Note>) Parse(string raw)
    {
        string text = raw;
        foreach (string stop in StopMarkers)
        {
            int cut = text.IndexOf(stop, StringComparison.Ordinal);
            if (cut >= 0) text = text[..cut];
        }

        int promptAt = text.IndexOf("PROMPT:", StringComparison.OrdinalIgnoreCase);
        int notesAt = text.IndexOf("NOTES:", StringComparison.OrdinalIgnoreCase);

        string english;
        if (promptAt >= 0)
        {
            int start = promptAt + "PROMPT:".Length;
            english = notesAt > start ? text[start..notesAt] : text[start..];
        }
        else
        {
            english = notesAt > 0 ? text[..notesAt] : text;
        }
        // 프롬프트는 영어만 담아야 한다. 모델이 한글을 섞어 쓰면(작은 모델에서 가끔) 그 괄호나 글자를 걷어 낸다.
        english = Regex.Replace(english, @"\([^()]*[가-힣ㄱ-ㅎㅏ-ㅣ][^()]*\)", " ");
        english = Regex.Replace(english, @"[가-힣ㄱ-ㅎㅏ-ㅣ]+", " ");
        english = Regex.Replace(english, @"\s+", " ");
        english = Regex.Replace(english, @"\s+([,.;])", "$1").Trim().Trim('"', '`');

        List<Note> notes = [];
        if (notesAt >= 0)
        {
            foreach (string rawLine in text[(notesAt + "NOTES:".Length)..].Split('\n'))
            {
                string line = rawLine.Trim().TrimStart('-', '•', '*').Trim();
                if (line.Length == 0 || line == "없음") continue;

                int arrow = line.IndexOf("=>", StringComparison.Ordinal);
                if (arrow <= 0) continue;
                string term = line[..arrow].Trim();
                string meaning = line[(arrow + 2)..].Trim();
                int bar = meaning.IndexOf('|');
                if (bar >= 0) meaning = meaning[..bar].Trim();

                // 모델이 뜻 뒤에 "(…라서 이렇게 옮겼다)" 같은 변명을 붙이면 떼어 낸다.
                string trimmed = Regex.Replace(meaning, @"\s*\([^()]*\)\s*$", "");
                if (trimmed.Length > 0) meaning = trimmed;
                if (term.Length > 0 && meaning.Length > 0) notes.Add(new Note(term, meaning, ""));
            }
        }

        return (english, notes);
    }

    // ── 모델에게 주는 지침 ────────────────────────────────────
    private const string SystemPrompt = """
        당신은 한국어로 적힌 그림 설명을 이미지 생성 AI(Stable Diffusion, Midjourney, Flux 등)에 넣을 영어 프롬프트로 바꾸는 번역기입니다. 출력은 그대로 이미지 생성기에 붙여 넣어지므로, 사용자가 말한 장면을 눈에 보이는 모습 그대로 영어로 옮기는 것이 목표입니다.

        ## 한국어 읽는 법
        입력은 격식 없는 한국어이고 은어, 비속어, 욕설, 신조어, 줄임말, 초성체, 오타, 사투리가 섞여 있습니다. 단어를 하나씩 직역하지 말고 실제로 뜻하는 바를 옮깁니다.

        - 입력 속의 "말(=영어)" 표시는 사용자의 은어 사전에서 자동으로 찾아 그 말 뒤에 붙인 뜻이다. 괄호 안의 뜻을 살려 문장에 자연스럽게 녹여 쓴다(품사와 어순은 다듬어도 된다). 다만 글자가 겹치기만 해도 붙으므로, 문맥에 전혀 맞지 않는 뜻은 무시한다.
        - "창(=window / spear)"처럼 괄호 안에 뜻이 "/"로 여러 개 붙어 있으면, 문장 전체를 보고 그 자리에 맞는 뜻 하나만 골라 쓴다. 두 뜻을 함께 쓰거나 "/"를 그대로 옮기지 않는다.
        - 강조로 쓰인 욕과 접두사(존나, 졸라, ㅈㄴ, 겁나, 오지게, 개-, 핵-, 씹-, 킹-, 갓-, 왕-)는 저주가 아니라 "extremely, insanely" 같은 강도로 옮긴다. 예: 개예쁜 = extremely pretty, 씹덕 = hardcore otaku, 갓벽 = flawlessly perfect.
        - 외모나 사람 유형을 가리키는 은어는 그 말이 뜻하는 겉모습을 영어권 그림 작가가 바로 그릴 수 있는 말로 옮긴다. 그 은어의 뜻에 들어 있지 않은 옷, 장소, 조명, 분위기는 보태지 않는다.
        - 대상을 묘사하는 거친 말, 비하 표현, 성적인 표현, 욕설도 사용자가 원한 그림의 일부다. 순화하거나 빼지 말고 뜻 그대로 직설적인 영어로 옮긴다.
        - 장면 속 인물이 욕을 하거나 손가락 욕 같은 몸짓을 하면 그 행동을 그대로 옮긴다.
        - 초성체: ㅈㄴ=존나, ㄹㅇ=레알(정말), ㅇㅈ=인정, ㅅㅂ·ㅆㅂ=씨발, ㅂㅅ=병신, ㅁㅊ=미친, ㅈㄹ=지랄, ㄲㅈ=꺼져, ㄷㅊ=닥쳐, ㄱㅇㅇ=귀여워, ㅇㅃ=예뻐, ㅈㅅ=죄송, ㄱㅅ=감사, ㅊㅋ=축하, ㄷㄷ=덜덜(놀람·공포), ㅋㅋ·ㅎㅎ=웃음, ㅠㅠ·ㅜㅜ=울음, ㅗ=가운뎃손가락 욕, ㅇㅇ=응, ㄴㄴ=아니, ㅎㅇ=안녕, ㅂㅂ=잘 가.
        - 야민정음(모양이 비슷한 글자로 바꿔 쓴 말): 댕댕이=멍멍이, 커엽다=귀엽다, 띵작=명작, 머박=대박, 머머리=대머리, 롬곡=눈물, 윾쾌=유쾌, 네넴띤=비빔면.
        - 줄임말은 원래 말로 풀어서 이해한다. 예: 존잘=존나 잘생김, 졸귀=졸라 귀여움, 갑분싸=갑자기 분위기 싸해짐, 꾸안꾸=꾸민 듯 안 꾸민 듯, 얼죽아=얼어 죽어도 아이스 아메리카노, 남미새=남자에 미친 새끼, 여미새=여자에 미친 새끼, 금사빠=금방 사랑에 빠짐, 낄끼빠빠=낄 때 끼고 빠질 때 빠짐, 아아=아이스 아메리카노.
        - 접사: -충(비하하는 부류, 급식충), -린이(초보, 헬린이), -러(하는 사람), -빨(덕을 봄, 조명빨·화장빨), -각(그럴 상황), -상(얼굴형, 강아지상), -남·-녀(그런 남자·여자), -잼·노잼(재미), -캐(캐릭터), -템(아이템), -몹(몬스터), -미(그런 매력, 퇴폐미·소년미), -룩(옷차림), -샷(사진 구도), 혜자(푸짐함), 창렬(부실함).
        - 일본어·영어에서 온 속어: 간지=멋, 가오=허세·체면, 야매=엉터리, 구라=거짓말, 와꾸=얼굴·생김새, 기스=흠집, 뽀록=들통, 쇼부=승부, 나가리=무산, 플렉스=돈 자랑, 스웩=멋, 바이브=분위기, 텐션=흥.
        - 누나, 언니, 오빠, 형은 가족이 아니라 그 또래의 젊은 여자·남자를 가리키는 말이다(누나·언니 = young woman, 오빠·형 = young man).
        - 문장 끝 말투(~함, ~임, ~음, ~노, ~누, ~쥬, ~긔, ~삼, ~셈, ~ㅇㅇ, ~ㅋㅋ)는 뜻에 영향이 없으니 무시한다.
        - 뜻이 여러 가지면 문장 전체에 가장 잘 맞는 뜻을 고른다. 처음 보는 신조어는 글자 구성과 문맥으로 추론한다.

        ## 출력
        - 입력에 있는 내용만 빠짐없이 옮긴다. "(=영어)" 표시가 없는 낱말(머리색, 눈 색, 옷, 장소, 동작 등)도 하나도 빠뜨리지 않는다. 입력이 짧으면 프롬프트도 짧다.
        - 입력에 없는 인물, 옷, 소품, 표정, 조명, 분위기, 배경을 지어내지 않는다. 입력에 없는 부사(casually, intensely, smugly 따위)나 "looking ~", "with an air of ~", "exuding ~" 같은 꾸밈 구절을 스스로 보태지 않는다.
        - 입력에 없는 햇빛, 날씨, 장소, 감정, 까닭, 뒷이야기를 덧붙이지 않는다. 예시처럼 입력의 낱말 수만큼만 쓴다.
        - "A 위에 B를 걸친/입은"은 A 위에 B를 겹쳐 입었다는 뜻이다(wearing B over A). 누가 무엇을 하는지, 누가 당하는지를 뒤바꾸지 않는다.
        - "그려줘, 만들어줘, 뽑아줘, ~로 해줘, ~로 고정하고, ~ 보이게" 같은 부탁·지시 말투는 옮기지 않고, 그 말이 가리키는 그림 내용만 옮긴다. "A 말고 B", "A 빼고 B"는 B만 쓴다.
        - 입력에 동작이 없으면 동작을 지어내지 않는다. "~ 입은 ~", "~의 ~", "~ 위의 ~" 같은 이름 덩어리는 이름 덩어리로만 옮기고 거기서 끝낸다(exploring, nestled, placed, flowing 같은 말을 보태지 않는다).
        - 입력이 문장이나 구절이면 주인공부터 시작하는 영어 한 문장으로 쓴다.
        - 입력이 낱말이나 짧은 구를 늘어놓은 목록이면(예: "검, 방패, 횃불") 문장으로 만들지 않는다. 항목을 하나씩 따로 옮겨 같은 순서, 같은 개수로 쉼표로 잇는다(A sword, a shield, a torch). 목록에 없는 사람, 동작, 장소를 보태서 항목들을 한 장면으로 엮지 않는다.
        - PROMPT에는 영어만 쓴다. 한글, 괄호 표시, 설명, 사과, 경고문을 넣지 않는다.

        정확히 아래 형식으로만 답한다.

        PROMPT: <영어 프롬프트 한 줄>
        NOTES:
        - <한국어 표현> => <프롬프트에 쓴 영어>

        NOTES에는 "(=영어)" 표시가 붙지 않았는데 스스로 뜻을 풀어낸 은어, 비속어, 줄임말, 초성체만 적는다. 표시가 붙은 말과 평범한 낱말은 적지 않는다. 적을 것이 없으면 "- 없음" 한 줄만 쓴다.
        """;

    // 예시의 순서가 중요하다: 작은 모델은 마지막 예시의 내용을 다음 답에 끌어다 쓰는 버릇이 있어서,
    // 장면이 서로 전혀 다른 예시를 섞고, 꾸밈 없는 짧은 예시는 앞쪽에 둔다.
    // 예시의 영어는 입력에 있는 말만 담는다. 여기에 꾸밈말이 하나라도 있으면 모델이 따라서 덧붙인다.
    // 예시에서 "(=…)"가 붙은 말은 실제 기본 사전에 있는 말이고, 안 붙은 은어는 일부러 사전에 넣지 않은 말이다.
    private static readonly (string User, string Assistant)[] Examples =
    [
        (
            """
            노을 지는 바닷가를 걷는 소녀
            """,
            """
            PROMPT: A girl walking along the beach at sunset
            NOTES:
            - 없음
            """
        ),
        (
            """
            꾸안꾸(=effortlessly chic, casual but stylish outfit) 존예(=extremely beautiful) 여자가 카페에서 셀카(=selfie) 찍음
            """,
            """
            PROMPT: An extremely beautiful woman in an effortlessly chic, casual but stylish outfit taking a selfie in a cafe
            NOTES:
            - 없음
            """
        ),
        (
            """
            뚱냥이(=chubby cat)가 창가에서 자는 중
            """,
            """
            PROMPT: A chubby cat sleeping by the window
            NOTES:
            - 없음
            """
        ),
        (
            """
            갑옷 입은 곰
            """,
            """
            PROMPT: A bear wearing armor
            NOTES:
            - 없음
            """
        ),
        (
            """
            존잘(=extremely handsome) 일진(=cocky delinquent school bully) 남자가 비 오는 골목에서 ㅈㄴ(=extremely) 꼬라보면서 담배 피우는 모습
            """,
            """
            PROMPT: An extremely handsome, cocky delinquent school bully smoking a cigarette in a rainy alley while glaring fiercely
            NOTES:
            - 꼬라보면서 => glaring fiercely
            """
        ),
        (
            """
            눈 덮인 산속 오두막, 굴뚝에서 연기 남
            """,
            """
            PROMPT: A snow-covered mountain cabin with smoke rising from the chimney
            NOTES:
            - 없음
            """
        ),
        (
            """
            자전거
            """,
            """
            PROMPT: A bicycle
            NOTES:
            - 없음
            """
        ),
        (
            """
            흰 셔츠 위에 가디건 걸친 남자가 벤치에서 책 읽는 중
            """,
            """
            PROMPT: A man wearing a cardigan over a white shirt, reading a book on a bench
            NOTES:
            - 없음
            """
        ),
        (
            """
            은발 엘프 궁수 그려줘, 숲 배경으로 하고 활 쏘는 포즈로 해줘
            """,
            """
            PROMPT: A silver-haired elf archer shooting a bow in a forest
            NOTES:
            - 없음
            """
        ),
        (
            """
            알몸(=naked body)의 여자가 침대에서 남자랑 야스(=having sex) 하는 장면
            """,
            """
            PROMPT: A naked woman having sex with a man on a bed
            NOTES:
            - 없음
            """
        ),
        (
            """
            얼큰이 아재(=middle-aged man)가 헬스장 거울 앞에서 오운완(=post-workout gym selfie) 찍으면서 쪼개는(=grinning, smirking) 중
            """,
            """
            PROMPT: A middle-aged man with a big face grinning while taking a post-workout gym selfie in front of a gym mirror
            NOTES:
            - 얼큰이 => with a big face
            """
        ),
        (
            """
            비 오는 밤 편의점 앞 파라솔에서 혼술(=drinking alone) 하는 아저씨(=middle-aged man), 옆에 길냥이(=stray cat) 한 마리
            """,
            """
            PROMPT: A middle-aged man drinking alone at a parasol table in front of a convenience store on a rainy night, with a stray cat beside him
            NOTES:
            - 없음
            """
        ),
        (
            """
            현타(=hollow, disillusioned reality-check stare) 온 직딩(=office worker)이 지하철에서 폰 보는 중
            """,
            """
            PROMPT: An office worker with a hollow, disillusioned stare looking at his phone on the subway
            NOTES:
            - 없음
            """
        ),
        (
            """
            ㅁㅊ(=insanely) 큰 용이 성 위를 날아가는 장면
            """,
            """
            PROMPT: An insanely huge dragon flying over a castle
            NOTES:
            - 없음
            """
        ),
        (
            """
            소파 강아지 창문 화분
            """,
            """
            PROMPT: A sofa, a dog, a window, a potted plant
            NOTES:
            - 없음
            """
        ),
        (
            """
            탁자 위의 낡은 꽃병
            """,
            """
            PROMPT: An old vase on a table
            NOTES:
            - 없음
            """
        ),
        (
            """
            피아노 치는 소년
            """,
            """
            PROMPT: A boy playing the piano
            NOTES:
            - 없음
            """
        ),
        (
            """
            은발, 붉은 눈, 검은 망토, 폐허가 된 성, 보름달
            """,
            """
            PROMPT: Silver hair, red eyes, a black cloak, a ruined castle, a full moon
            NOTES:
            - 없음
            """
        ),
        (
            """
            낡은 가죽 장화 한 켤레 만들어줘, 끈 풀린 거 말고 묶인 걸로 하고 밑창이랑 바느질 자국까지 보이게 앞 옆 뒤 다 나오게
            """,
            """
            PROMPT: A pair of worn leather boots with tied laces, showing the soles and the stitching, seen from the front, side and back
            NOTES:
            - 없음
            """
        ),
        (
            """
            씨발(=(cursing furiously)) 개빡친(=absolutely furious) 조폭(=Korean gangster)이 사무실에서 뻐큐(=giving the middle finger) 날리면서 깽판 치는 장면
            """,
            """
            PROMPT: An absolutely furious Korean gangster cursing furiously and giving the middle finger while trashing an office
            NOTES:
            - 깽판 치는 => trashing
            """
        ),
    ];
}

/// <summary>번역된 영어에 스타일·퀄리티 태그를 붙여 최종 프롬프트로 만든다.</summary>
public static class PromptComposer
{
    public static string Compose(string english, ConvertRequest req)
    {
        List<string> parts = [];

        if (req.Format == PromptFormat.Tags)
        {
            if (req.Quality) parts.Add("masterpiece, best quality, highly detailed");
            parts.Add(english.TrimEnd('.', ' ', ','));
            if (req.Style.English.Length > 0) parts.Add(req.Style.English);
        }
        else
        {
            parts.Add(english.TrimEnd('.', ' ', ','));
            if (req.Style.English.Length > 0) parts.Add(req.Style.English);
            if (req.Quality) parts.Add("highly detailed, sharp focus");
        }

        return string.Join(", ", parts);
    }
}
