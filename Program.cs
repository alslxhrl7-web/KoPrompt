using System.Drawing.Imaging;
using System.Text;

namespace KoPrompt;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 점검용: KoPrompt.exe --cli <입력.txt> <출력.txt> [tags]   (창 없이 변환만 실행)
        // 점검용: KoPrompt.exe --check <입력.txt> <출력.txt>   (AI 없이 막힐 수 있는 부분만 검사)
        if (args.Length >= 3 && args[0] == "--check")
        {
            StringBuilder sb = new();
            SlangDictionary dict = new();
            foreach (string line in File.ReadAllLines(args[1], Encoding.UTF8).Where(l => l.Trim().Length > 0))
            {
                dict.Annotate(line, out List<Note> slang);
                List<PolicyHit> hits = PolicyCheck.Check(line, line + " " + string.Join(" ", slang.Select(n => n.English)));
                sb.AppendLine("입력   : " + line);
                sb.AppendLine(PolicyCheck.Format(hits)).AppendLine();
            }
            File.WriteAllText(args[2], sb.ToString(), Encoding.UTF8);
            return 0;
        }

        if (args.Length >= 3 && args[0] == "--cli")
        {
            LocalEngine.FastMode = args.Contains("fast");
            return RunCli(args[1], args[2], args.Contains("tags"), !args.Contains("noexplain"), args.Contains("preread"), args.Contains("3d"));
        }

        // VARCO 3D 전용 창: exe 이름에 "3D"나 "바르코"가 들어 있거나("KoPrompt 바르코3D.exe") --3d로 실행하면 연다.
        string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        AppSettings.Is3D = args.Contains("--3d") ||
                           exeName.Contains("3D", StringComparison.OrdinalIgnoreCase) ||
                           exeName.Contains("바르코", StringComparison.Ordinal) ||
                           exeName.Contains("VARCO", StringComparison.OrdinalIgnoreCase);

        ApplicationConfiguration.Initialize();

        // 점검용: KoPrompt.exe --screenshot <파일.png>   (창을 그림 파일로 저장하고 종료)
        if (args.Length >= 2 && args[0] == "--screenshot")
        {
            using MainForm shot = new();
            HideOffScreen(shot);
            shot.LoadSample();
            shot.Show();
            Application.DoEvents();
            using Bitmap bmp = new(shot.Width, shot.Height);
            shot.DrawToBitmap(bmp, new Rectangle(Point.Empty, shot.Size));
            bmp.Save(args[1], ImageFormat.Png);
            return 0;
        }

        // 점검용: KoPrompt.exe --uitest <파일.png> <입력.txt>   (실제 창에서 변환까지 해 보고 그림으로 저장)
        if (args.Length >= 3 && args[0] == "--uitest")
        {
            MainForm test = new();
            HideOffScreen(test);
            test.Shown += async (_, _) =>
            {
                await test.RunSelfTestAsync(
                    File.ReadAllText(args[2], Encoding.UTF8).Trim(), args.Contains("auto"), args.Contains("togglefast"));
                File.WriteAllText(args[1] + ".log", LocalEngine.GetNativeLog(), Encoding.UTF8);
                if (test.WindowState == FormWindowState.Minimized)   // 최소화된 채로 시작됐어도 창 전체를 찍는다
                {
                    test.WindowState = FormWindowState.Normal;
                    Application.DoEvents();
                }
                using Bitmap bmp = new(test.Width, test.Height);
                test.DrawToBitmap(bmp, new Rectangle(Point.Empty, test.Size));
                bmp.Save(args[1], ImageFormat.Png);
                test.Close();
            };
            Application.Run(test);
            return 0;
        }

        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>점검용 창은 화면 밖에 띄워서, 쓰고 있는 화면에 창이 튀어나오지 않게 한다.</summary>
    private static void HideOffScreen(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.ShowInTaskbar = false;
    }

    /// <summary>프롬프트 줄이 다 써진 시각을 잰다.</summary>
    private sealed class PromptTimer(System.Diagnostics.Stopwatch watch) : IProgress<PromptResult>
    {
        public double Seconds { get; private set; }

        public void Report(PromptResult value)
        {
            if (value.PromptDone && Seconds == 0) Seconds = watch.Elapsed.TotalSeconds;
        }
    }

    private static int RunCli(string inPath, string outPath, bool tags, bool explain, bool preread, bool varco3d = false)
    {
        StringBuilder sb = new();
        try
        {
            SlangDictionary dict = new();
            sb.AppendLine($"사전: {dict.FilePath} ({dict.Count}개)");

            System.Diagnostics.Stopwatch load = System.Diagnostics.Stopwatch.StartNew();
            Task.Run(() => LocalEngine.EnsureLoadedAsync(true, null, CancellationToken.None)).GetAwaiter().GetResult();
            sb.AppendLine($"모델 준비: {load.Elapsed.TotalSeconds:0.0}초 · {LocalEngine.Backend}");
            foreach (string line in File.ReadAllLines(inPath, Encoding.UTF8))
            {
                if (line.Trim().Length == 0) continue;
                if (line.StartsWith('+') && line.IndexOf('=') is > 1 and int eq)   // "+은어 = 뜻" → 사전에 추가
                {
                    dict.Add(line[1..eq].Trim(), line[(eq + 1)..].Trim());
                    sb.AppendLine().AppendLine($"추가   : {line[1..].Trim()} (사전 {dict.Count}개)");
                    continue;
                }
                ConvertRequest req = new(
                    line, varco3d ? PromptFormat.Varco3D : tags ? PromptFormat.Tags : PromptFormat.Sentence, StylePreset.All[0],
                    Quality: false, Negative: false, Explain: explain);
                sb.AppendLine();
                sb.AppendLine("입력   : " + line);
                try
                {
                    if (preread)   // 사용자가 문장을 치는 동안 미리 읽어 둔 상황을 흉내 낸다
                    {
                        Task.Run(() => LocalEngine.PrereadAsync(line, dict, CancellationToken.None))
                            .GetAwaiter().GetResult();
                    }
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    PromptTimer timer = new(watch);
                    PromptResult r = Task.Run(() => LocalEngine.ConvertAsync(req, dict, true, null, timer, CancellationToken.None))
                        .GetAwaiter().GetResult();
                    double total = watch.Elapsed.TotalSeconds;
                    sb.AppendLine($"시간   : 프롬프트 {(timer.Seconds > 0 ? timer.Seconds : total):0.0}초 / 전체 {total:0.0}초");
                    sb.AppendLine("프롬프트: " + r.Prompt);
                    if (r.Guide.Length > 0) sb.AppendLine("안내   : " + r.Guide.Replace("\r\n", "\n").Replace("\n", "\n         "));
                    foreach (Note n in r.Notes) sb.AppendLine($"풀이   : {n.Term} -> {n.English}  ({n.Explanation})");
                    sb.AppendLine("정보   : " + r.Info);
                }
                catch (UserFacingException e)
                {
                    sb.AppendLine("오류   : " + e.Message);
                }
            }
            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
            File.WriteAllText(outPath + ".log", LocalEngine.GetNativeLog(), Encoding.UTF8);
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(outPath, sb + "\r\n치명적 오류: " + e, Encoding.UTF8);
            File.WriteAllText(outPath + ".log", LocalEngine.GetNativeLog(), Encoding.UTF8);
            return 1;
        }
    }
}
