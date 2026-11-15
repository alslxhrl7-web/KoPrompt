using System.Diagnostics;
using System.Security.Cryptography;

namespace KoPrompt;

/// <summary>
/// 받을 수 있는 AI 모델. EXAONE 라이선스는 모델 파일을 다른 곳에 다시 올리는 것을 막고 있어서
/// 프로그램에 넣지 않고, 사용자가 LG AI연구원의 공식 Hugging Face 저장소에서 직접 받게 한다.
/// 받은 파일이 공식 파일과 똑같은지 크기와 SHA-256으로 확인한다(커밋을 고정해 두어 값이 바뀌지 않는다).
/// </summary>
internal sealed record ModelSource(string Label, string Note, string FileName, string Repo, string Commit, long Size, string Sha256)
{
    public static readonly ModelSource[] All =
    [
        new("빠른 모드용 · EXAONE 3.5 2.4B", "빠르고 가볍습니다. 사전에 없는 은어는 조금 덜 정확합니다.",
            "EXAONE-3.5-2.4B-Instruct-Q4_K_M.gguf", "LGAI-EXAONE/EXAONE-3.5-2.4B-Instruct-GGUF",
            "142acae803a41c206e8d0fa978c6102c748911bb", 1_644_918_272,
            "1660a3794acc33437137faab5cb3fb2b2c4191d0a4ec991d4c31a7197cb2cdbf"),
        new("정확 모드용 · EXAONE 3.5 7.8B", "기본 모델입니다. 더 정확하고, 메모리 8GB 이상을 권장합니다.",
            "EXAONE-3.5-7.8B-Instruct-Q4_K_M.gguf", "LGAI-EXAONE/EXAONE-3.5-7.8B-Instruct-GGUF",
            "c618bf67338171760c72c3f109f2900cb7d79855", 4_770_649_728,
            "2f4aff8d555b82500d4de2723810586e83e358003388fdd08bd52e6091e6ddd1"),
    ];

    public const string LicenseUrl = "https://huggingface.co/LGAI-EXAONE/EXAONE-3.5-7.8B-Instruct-GGUF/blob/main/LICENSE";

    public string Url => $"https://huggingface.co/{Repo}/resolve/{Commit}/{FileName}";
    public string Target => Path.Combine(AppPaths.Models, FileName);
    public string PartFile => Target + ".part";

    /// <summary>이미 받아 두었는지(사용자 폴더나 exe 옆 models 폴더 어디든).</summary>
    public bool Present => LocalEngine.ModelFolders.Any(d => File.Exists(Path.Combine(d, FileName)));

    public static bool AnyMissing => All.Any(m => !m.Present);

    public static string Bytes(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0}GB" : $"{bytes / (double)(1L << 20):0}MB";
}

/// <summary>받는 중 상태.</summary>
internal readonly record struct DownloadProgress(long Done, long Total, double BytesPerSecond, bool Verifying);

internal static class ModelDownloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(20) })
        {
            Timeout = Timeout.InfiniteTimeSpan,   // 큰 파일이라 전체 시간 제한은 두지 않고, 읽기마다 따로 잰다
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KoPrompt/1.2");
        return http;
    }

    /// <summary>
    /// 모델 하나를 받는다. 끊기면 .part 파일을 남겨 두고, 다음에 받을 때 이어서 받는다.
    /// 다 받으면 SHA-256을 확인한 뒤에만 .gguf 이름으로 바꾼다.
    /// </summary>
    public static async Task DownloadAsync(ModelSource m, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Models);
            long have = File.Exists(m.PartFile) ? new FileInfo(m.PartFile).Length : 0;
            if (have > m.Size) { File.Delete(m.PartFile); have = 0; }

            if (have < m.Size)
            {
                using HttpRequestMessage req = new(HttpMethod.Get, m.Url);
                if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using HttpResponseMessage res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if (res.StatusCode == System.Net.HttpStatusCode.OK) have = 0;   // 이어받기를 안 받아 주면 처음부터
                else if (res.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    throw new UserFacingException(
                        $"모델 저장소가 응답하지 않습니다(HTTP {(int)res.StatusCode}).\r\n잠시 뒤 다시 받아 보세요. 받은 부분은 남아 있어서 이어서 받습니다.");
                }

                await using Stream src = await res.Content.ReadAsStreamAsync(ct);
                await using FileStream dst = new(m.PartFile, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                byte[] buf = new byte[1 << 20];
                Stopwatch clock = Stopwatch.StartNew();
                long lastBytes = have;
                double lastTime = 0, speed = 0;
                while (true)
                {
                    // 30초 동안 한 바이트도 안 오면 끊긴 것으로 본다.
                    using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(TimeSpan.FromSeconds(30));
                    int n;
                    try { n = await src.ReadAsync(buf, stall.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new UserFacingException("인터넷 연결이 끊겼습니다.\r\n다시 받으면 받은 부분에 이어서 받습니다.");
                    }
                    if (n == 0) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    have += n;

                    double now = clock.Elapsed.TotalSeconds;
                    if (now - lastTime >= 0.5)
                    {
                        double s = (have - lastBytes) / (now - lastTime);
                        speed = speed == 0 ? s : speed * 0.7 + s * 0.3;   // 속도가 들쭉날쭉하지 않게 고르게
                        lastBytes = have;
                        lastTime = now;
                        progress.Report(new DownloadProgress(have, m.Size, speed, false));
                    }
                }
            }

            if (have != m.Size)
            {
                throw new UserFacingException(
                    $"받는 도중에 끊겼습니다({ModelSource.Bytes(have)} / {ModelSource.Bytes(m.Size)}).\r\n다시 받으면 이어서 받습니다.");
            }

            progress.Report(new DownloadProgress(have, m.Size, 0, true));
            string hash = await Task.Run(async () =>
            {
                await using FileStream f = new(m.PartFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                return Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
            }, ct);
            if (hash != m.Sha256)
            {
                File.Delete(m.PartFile);
                throw new UserFacingException("받은 파일이 공식 파일과 다릅니다(손상).\r\n지웠으니 다시 받아 주세요.");
            }
            File.Move(m.PartFile, m.Target, overwrite: true);
        }
        catch (HttpRequestException e)
        {
            throw new UserFacingException("모델 저장소에 연결하지 못했습니다. 인터넷 연결을 확인해 주세요.\r\n\r\n" + e.Message);
        }
        catch (IOException e) when (e is not FileNotFoundException)
        {
            throw new UserFacingException("모델 파일을 저장하지 못했습니다. 디스크 공간을 확인해 주세요.\r\n\r\n" + e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new UserFacingException("모델 폴더에 쓸 수 없습니다.\r\n\r\n" + e.Message);
        }
    }
}

/// <summary>모델이 없을 때(또는 '모델 받기'를 눌렀을 때) 뜨는 창.</summary>
internal sealed class ModelDownloadForm : Form
{
    private readonly (ModelSource Model, CheckBox Box)[] _items;
    private readonly Label _lblSpace = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Fill, Height = 22, Maximum = 1000, Margin = new Padding(3, 10, 3, 3) };
    private readonly Label _lblState = new() { AutoSize = true, Margin = new Padding(3, 6, 3, 3), MaximumSize = new Size(600, 0) };
    private readonly Button _btnStart = new() { Text = "받기", AutoSize = true, Padding = new Padding(16, 4, 16, 4) };
    private readonly Button _btnFolder = new() { Text = "폴더 열기", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    private readonly Button _btnClose = new() { Text = "닫기", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    private CancellationTokenSource? _cts;

    /// <summary>이번에 하나라도 받았는지.</summary>
    public bool Downloaded { get; private set; }

    public ModelDownloadForm(bool noModel)
    {
        Text = "AI 모델 받기 · KoPrompt";
        Font = new Font("맑은 고딕", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        TableLayoutPanel root = new() { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        root.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(3, 0, 3, 8),
            Text = (noModel ? "변환하려면 PC 안에서 도는 AI 모델이 있어야 합니다.\r\n" : "") +
                   "모델은 라이선스 때문에 프로그램에 넣지 못해서, LG AI연구원의 공식 저장소(Hugging Face)에서 직접 받습니다. " +
                   "한 번만 받으면 그 뒤로는 인터넷 없이 씁니다.",
        });

        _items = [.. ModelSource.All.Select(m =>
        {
            bool present = m.Present;
            bool partial = !present && File.Exists(m.PartFile);
            CheckBox box = new()
            {
                AutoSize = true, Margin = new Padding(3, 6, 3, 0), Font = new Font(Font, FontStyle.Bold),
                Text = $"{m.Label}  ({ModelSource.Bytes(m.Size)})" + (present ? "  · 이미 있음" : partial ? "  · 받다 만 것 이어받기" : ""),
                Checked = !present, Enabled = !present,
            };
            box.CheckedChanged += (_, _) => UpdateSpace();
            return (m, box);
        })];
        foreach ((ModelSource m, CheckBox box) in _items)
        {
            root.Controls.Add(box);
            root.Controls.Add(new Label { AutoSize = true, Text = m.Note, ForeColor = SystemColors.GrayText, Margin = new Padding(22, 0, 3, 2) });
        }

        LinkLabel license = new()
        {
            AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(3, 12, 3, 0),
            Text = "EXAONE 3.5는 비상업·연구용 라이선스(EXAONE AI Model License 1.1 - NC)입니다. 받으면 이 라이선스를 따르는 것으로 봅니다.  라이선스 보기",
        };
        license.LinkArea = new LinkArea(license.Text.Length - "라이선스 보기".Length, "라이선스 보기".Length);
        license.LinkClicked += (_, _) => OpenUrl(ModelSource.LicenseUrl);
        root.Controls.Add(license);
        root.Controls.Add(_lblSpace);
        root.Controls.Add(_bar);
        root.Controls.Add(_lblState);

        FlowLayoutPanel buttons = new() { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([_btnClose, _btnStart, _btnFolder]);
        root.Controls.Add(buttons);
        Controls.Add(root);

        AcceptButton = _btnStart;
        _btnStart.Click += async (_, _) => await StartAsync();
        _btnFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.Models);
            OpenUrl(AppPaths.Models);
        };
        _btnClose.Click += (_, _) => Close();
        FormClosing += OnClosing;

        _bar.Visible = false;
        _lblState.Text = "직접 받은 .gguf 파일이 있으면 '폴더 열기'로 연 곳에 넣어도 됩니다.";
        UpdateSpace();
    }

    private IEnumerable<ModelSource> Selected => _items.Where(i => i.Box.Checked && i.Box.Enabled).Select(i => i.Model);

    private void UpdateSpace()
    {
        long need = Selected.Sum(m => m.Size - (File.Exists(m.PartFile) ? new FileInfo(m.PartFile).Length : 0));
        long free = FreeSpace();
        _lblSpace.Text = $"저장 위치: {AppPaths.Models}\r\n받을 크기: {ModelSource.Bytes(need)} · 남은 디스크: {(free >= 0 ? ModelSource.Bytes(free) : "알 수 없음")}";
        _lblSpace.ForeColor = free >= 0 && free < need + (200L << 20) ? Color.Firebrick : SystemColors.GrayText;
        _btnStart.Enabled = need > 0 || Selected.Any();
    }

    private static long FreeSpace()
    {
        try { return new DriveInfo(Path.GetPathRoot(AppPaths.Data)!).AvailableFreeSpace; }
        catch { return -1; }
    }

    private async Task StartAsync()
    {
        ModelSource[] todo = [.. Selected];
        if (todo.Length == 0) { Close(); return; }

        long need = todo.Sum(m => m.Size - (File.Exists(m.PartFile) ? new FileInfo(m.PartFile).Length : 0));
        long free = FreeSpace();
        if (free >= 0 && free < need + (200L << 20))
        {
            MessageBox.Show(this, $"디스크 공간이 모자랍니다.\r\n필요: {ModelSource.Bytes(need)} · 남은 공간: {ModelSource.Bytes(free)}",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cts = new CancellationTokenSource();
        foreach ((_, CheckBox box) in _items) box.Enabled = false;
        _btnStart.Enabled = false;
        _btnClose.Text = "그만두기";
        _bar.Visible = true;
        try
        {
            for (int i = 0; i < todo.Length; i++)
            {
                ModelSource m = todo[i];
                string head = todo.Length > 1 ? $"[{i + 1}/{todo.Length}] {m.Label}" : m.Label;
                Progress<DownloadProgress> progress = new(p => ShowProgress(head, p));
                _lblState.Text = head + " · 연결하는 중…";
                await ModelDownloader.DownloadAsync(m, progress, _cts.Token);
                Downloaded = true;
            }
            _bar.Value = _bar.Maximum;
            _lblState.Text = "다 받았습니다.";
            _cts.Dispose();
            _cts = null;   // 닫을 때 '그만둘까요?'를 묻지 않게
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            _lblState.Text = "그만두었습니다. 받은 부분은 남겨 두어서, 다음에 받으면 이어서 받습니다.";
            ResetButtons();
        }
        catch (UserFacingException e)
        {
            _lblState.Text = e.Message;
            ResetButtons();
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ResetButtons()
    {
        if (IsDisposed) return;
        foreach ((ModelSource m, CheckBox box) in _items)
        {
            bool present = m.Present;
            box.Enabled = !present;
            if (present) { box.Checked = false; box.Text = $"{m.Label}  ({ModelSource.Bytes(m.Size)})  · 이미 있음"; }
        }
        _btnStart.Text = "다시 받기(이어서)";
        _btnClose.Text = "닫기";
        UpdateSpace();
    }

    private void ShowProgress(string head, DownloadProgress p)
    {
        if (IsDisposed) return;
        _bar.Value = (int)Math.Clamp(p.Done * 1000 / Math.Max(1, p.Total), 0, 1000);
        if (p.Verifying)
        {
            _lblState.Text = head + " · 받은 파일이 공식 파일과 같은지 확인하는 중…";
            return;
        }
        string eta = "";
        if (p.BytesPerSecond > 0)
        {
            TimeSpan left = TimeSpan.FromSeconds((p.Total - p.Done) / p.BytesPerSecond);
            eta = left.TotalMinutes >= 1 ? $" · 남은 시간 약 {Math.Ceiling(left.TotalMinutes):0}분" : $" · 남은 시간 약 {Math.Max(1, left.Seconds)}초";
        }
        _lblState.Text = $"{head} · {ModelSource.Bytes(p.Done)} / {ModelSource.Bytes(p.Total)} · {p.BytesPerSecond / (1 << 20):0.0}MB/s{eta}";
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_cts is null) return;
        if (MessageBox.Show(this, "받는 중입니다. 그만둘까요?\r\n받은 부분은 남겨 두었다가 다음에 이어서 받습니다.", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            _cts.Cancel();
        }
        e.Cancel = true;   // 취소가 처리되면 버튼이 '닫기'로 돌아온다
    }

    private static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* 브라우저나 탐색기를 못 열어도 받기에는 지장 없다 */ }
    }
}
