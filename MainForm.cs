using System.Diagnostics;
using System.Text;

namespace KoPrompt;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SlangDictionary _dict = new();
    private CancellationTokenSource? _cts;
    private Task? _running;            // 지금 돌고 있는 변환
    private bool _promptReady;         // 그 변환의 프롬프트는 다 나왔고 풀이만 남았는지
    private string _lastEnglish = "";  // 마지막 번역문(스타일·퀄리티 태그를 붙이기 전)
    private PromptFormat _lastFormat;
    private string _startedKey = "";   // 마지막으로 변환을 시작한 (형식·풀이·문장). 같은 것을 두 번 돌리지 않는다
    private readonly System.Windows.Forms.Timer _typingTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _prereadTimer = new() { Interval = 250 };
    private CancellationTokenSource? _prereadCts;
    private readonly CheckBox _chkAuto = new() { Text = "입력 멈추면 자동 변환", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };

    private readonly ComboBox _cboFormat = NewCombo(260);
    private readonly ComboBox _cboStyle = NewCombo(160);
    private readonly CheckBox _chkQuality = new() { Text = "퀄리티 태그 추가", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
    private readonly CheckBox _chkNegative = new() { Text = "네거티브 프롬프트", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
    private readonly CheckBox _chkGpu = new() { Text = "GPU 사용", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
    private readonly CheckBox _chkFast = new() { Text = "빠른 모드", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
    private bool _loadedWithGpu;   // 지금 올라와 있는 모델을 불러올 때의 'GPU 사용' 설정
    private bool _reloading;
    private readonly CheckBox _chkExplain = new() { Text = "AI 풀이 쓰기", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };

    // KoPrompt3D(VARCO 3D용) 창에서만 쓰는 선택
    private readonly bool _is3D = AppSettings.Is3D;
    private readonly ComboBox _cbo3Subject = NewCombo(130);
    private readonly ComboBox _cbo3Pose = NewCombo(90);
    private readonly ComboBox _cbo3View = NewCombo(100);
    private readonly ComboBox _cbo3Style = NewCombo(150);
    private readonly ComboBox _cbo3Background = NewCombo(110);
    private readonly ComboBox _cbo3Ratio = NewCombo(130);
    private string _lastKorean = "";
    private readonly Button _btnBuiltIn = NewButton("기본 사전 보기");

    private readonly TextBox _txtInput = NewTextBox(readOnly: false);
    private readonly TextBox _txtPrompt = NewTextBox(readOnly: true);
    private readonly TextBox _txtNegative = NewTextBox(readOnly: true);
    private readonly TextBox _txtNotes = NewTextBox(readOnly: true);

    private readonly Button _btnConvert = new() { Text = "변환  (Ctrl+Enter)", AutoSize = true, Padding = new Padding(14, 4, 14, 4) };
    private readonly Button _btnClear = NewButton("지우기");
    private readonly Button _btnCopyPrompt = NewButton("복사");
    private readonly Button _btnCopyNegative = NewButton("복사");
    private readonly Button _btnDict = NewButton("내 사전 편집");
    private readonly Button _btnAdd = NewButton("추가");
    private readonly TextBox _txtNewTerm = new() { Width = 150, PlaceholderText = "은어 (예: 갓벽)", Margin = new Padding(3, 5, 3, 3) };
    private readonly TextBox _txtNewMeaning = new() { Width = 200, PlaceholderText = "영어 뜻 (예: flawless, perfect)", Margin = new Padding(3, 5, 3, 3) };
    private readonly Label _lblStatus = new() { AutoSize = true, Margin = new Padding(12, 10, 3, 3), ForeColor = SystemColors.GrayText };

    public MainForm()
    {
        Text = "KoPrompt · 한국어 → 이미지 생성 프롬프트";
        Font = new Font("맑은 고딕", 10f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(940, 860);
        MinimumSize = new Size(700, 640);
        KeyPreview = true;

        _cboFormat.Items.AddRange(["문장형  (Midjourney · DALL·E · Flux)", "태그형  (Stable Diffusion · NovelAI)"]);
        _cboStyle.Items.AddRange(StylePreset.All);
        if (_is3D)
        {
            Text = "KoPrompt 3D · VARCO 3D용 이미지 프롬프트";
            _cbo3Subject.Items.AddRange(Varco3D.SubjectLabels);
            _cbo3Pose.Items.AddRange(Varco3D.PoseLabels);
            _cbo3View.Items.AddRange(Varco3D.ViewLabels);
            _cbo3Style.Items.AddRange([.. Varco3D.Styles.Select(s => s.Label)]);
            _cbo3Background.Items.AddRange(Varco3D.BackgroundLabels);
            _cbo3Ratio.Items.AddRange(Varco3D.RatioLabels);
            new ToolTip().SetToolTip(_cbo3Pose, "캐릭터를 3D로 바꾼 뒤 뼈대를 넣기 쉽게 T-포즈나 A-포즈로 세웁니다.\r\n입력한 동작을 살리려면 '입력대로'를 고르세요.");
            new ToolTip().SetToolTip(_cbo3Ratio, "VARCO 화면의 비율 칸에서 고를 값입니다. '자동 추천'이면 대상 모양에 맞춰 추천합니다.");
        }

        BuildLayout();
        ApplySettings();

        _btnConvert.Click += async (_, _) => await ConvertAsync();
        _btnClear.Click += (_, _) => { _txtInput.Clear(); _txtInput.Focus(); };
        _btnCopyPrompt.Click += (_, _) => CopyText(_txtPrompt.Text, "프롬프트를 복사했습니다.");
        _btnCopyNegative.Click += (_, _) => CopyText(_txtNegative.Text, "네거티브 프롬프트를 복사했습니다.");
        _btnDict.Click += (_, _) => OpenInNotepad(_dict.FilePath, "메모장에서 고친 뒤 저장하면 다음 변환부터 적용됩니다.");
        _btnBuiltIn.Click += (_, _) => OpenInNotepad(
            AppPaths.BuiltInReference, "기본 사전은 보기용입니다. 뜻을 바꾸려면 '내 사전'에 같은 단어를 적으세요.");
        new ToolTip().SetToolTip(_chkExplain,
            "켜면 사전에 없는 은어를 AI가 어떻게 읽었는지도 적습니다.\r\n끄면 프롬프트만 쓰고 끝나서 조금 더 빠릅니다.");
        _btnAdd.Click += (_, _) => AddTerm();
        _txtNewMeaning.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Control)
            {
                e.SuppressKeyPress = true;
                AddTerm();
            }
        };
        KeyDown += async (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await ConvertAsync();
            }
        };
        // 타이핑이 0.7초 멈추면 자동으로 변환을 시작한다. 형식이나 풀이 설정을 바꿔도 마찬가지.
        _typingTimer.Tick += async (_, _) => await AutoConvertAsync();
        _prereadTimer.Tick += async (_, _) => await PrereadAsync();
        _txtInput.TextChanged += (_, _) => { RestartTypingTimer(); RestartPreread(); };
        _cboFormat.SelectedIndexChanged += (_, _) => { RestartTypingTimer(); RestartPreread(); };
        _chkExplain.CheckedChanged += (_, _) => RestartTypingTimer();
        _chkAuto.CheckedChanged += (_, _) => RestartTypingTimer();
        new ToolTip().SetToolTip(_chkAuto,
            "켜면 타이핑을 잠깐 멈출 때마다 변환을 미리 시작합니다.\r\n문장을 고치면 하던 변환을 끊고 다시 합니다.");

        FormClosing += (_, _) =>
        {
            _typingTimer.Stop();
            _prereadTimer.Stop();
            _prereadCts?.Cancel();
            _cts?.Cancel();
            SaveSettings();
        };
        Shown += async (_, _) => await PreloadModelAsync();

        SetStatus($"은어 사전 {_dict.Count}개 불러옴");
    }

    /// <summary>창이 뜨면 바로 모델을 불러 두어 첫 변환이 기다리지 않게 한다.</summary>
    private async Task PreloadModelAsync()
    {
        if (LocalEngine.FindModelFile() is null)
        {
            SetStatus("AI 모델 파일이 없습니다 · " + LocalEngine.ModelsFolder + " 에 .gguf 파일을 넣어 주세요");
            return;
        }

        _btnConvert.Enabled = false;
        try
        {
            bool useGpu = _chkGpu.Checked;
            await LocalEngine.EnsureLoadedAsync(useGpu, new Progress<string>(SetStatus), CancellationToken.None);
            _loadedWithGpu = useGpu;
            string note = useGpu && LocalEngine.Backend == "CPU" ? " (GPU 메모리가 모자라 CPU로 실행)" : "";
            string mode = LocalEngine.FastMode ? "빠른 모드" : "정확 모드";
            SetStatus($"준비 완료 · 완전 오프라인 · {mode} · {LocalEngine.Backend}{note} · 사전 {_dict.Count}개");
        }
        catch (UserFacingException e)
        {
            SetStatus("AI 모델을 불러오지 못했습니다.");
            MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception e)
        {
            SetStatus("AI 모델을 불러오지 못했습니다.");
            MessageBox.Show(this, "AI 엔진을 시작하지 못했습니다.\r\n\r\n" + e.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed)
            {
                _btnConvert.Enabled = true;
                RestartTypingTimer();   // 불러오는 동안 이미 입력해 둔 문장이 있으면 바로 변환한다
                RestartPreread();
            }
        }
    }

    /// <summary>화면 점검(--screenshot)용 예시 내용.</summary>
    public void LoadSample()
    {
        _txtInput.Text = "존잘 일진 남자가 비 오는 골목에서 ㅈㄴ 빡친 표정으로 담배 피우는 모습, 갬성 쩌는 야경";
        _txtPrompt.Text = "(변환 결과가 여기에 표시됩니다)";
        _txtNotes.Text = "• 존잘  →  extremely handsome";
    }

    /// <summary>화면 점검(--uitest)용: 모델이 준비되면 실제로 한 번 변환해 본다.</summary>
    /// <param name="auto">버튼을 누르지 않고, 사람이 치듯이 조금씩 입력한 뒤 자동 변환이 끝나기를 기다린다.</param>
    /// <param name="toggleFast">시작할 때 '빠른 모드' 체크를 한 번 뒤집어서 모델이 바뀌어 올라오는지도 본다.</param>
    public async Task RunSelfTestAsync(string korean, bool auto, bool toggleFast)
    {
        while (!_btnConvert.Enabled || !LocalEngine.IsLoaded) await Task.Delay(200);
        if (toggleFast && _chkFast.Enabled)
        {
            _chkFast.Checked = !_chkFast.Checked;
            await Task.Delay(300);
            while (_reloading || !_btnConvert.Enabled || !LocalEngine.IsLoaded) await Task.Delay(200);
        }
        if (!auto)
        {
            _txtInput.Text = korean;
            await ConvertAsync();
            return;
        }

        for (int i = 0; i < korean.Length; i += 6)
        {
            _txtInput.Text = korean[..Math.Min(korean.Length, i + 6)];
            await Task.Delay(350);
        }
        Stopwatch wait = Stopwatch.StartNew();
        while (wait.Elapsed.TotalSeconds < 120 &&
               !(_startedKey == CurrentKey() && _running is null && _lastEnglish.Length > 0))
        {
            await Task.Delay(200);
        }
    }

    // ── 화면 구성 ─────────────────────────────────────────────
    private void BuildLayout()
    {
        TableLayoutPanel root = new() { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        FlowLayoutPanel options = NewFlow();
        if (_is3D)
        {
            options.Controls.AddRange([
                _cbo3Subject, NewInlineLabel("자세"), _cbo3Pose, _cbo3View, NewInlineLabel("화풍"), _cbo3Style, _cbo3Background, _cbo3Ratio,
                _chkFast, _chkExplain, _chkAuto, _chkGpu,
            ]);
        }
        else
        {
            options.Controls.AddRange([
                NewInlineLabel("형식"), _cboFormat,
                NewInlineLabel("스타일"), _cboStyle,
                _chkQuality, _chkNegative, _chkFast, _chkExplain, _chkAuto, _chkGpu,
            ]);
        }

        FlowLayoutPanel actions = NewFlow();
        actions.Controls.AddRange([_btnConvert, _btnClear, _lblStatus]);

        FlowLayoutPanel bottom = NewFlow();
        bottom.Controls.AddRange([
            NewInlineLabel("사전에 단어 추가"), _txtNewTerm, NewInlineLabel("="), _txtNewMeaning, _btnAdd, _btnDict, _btnBuiltIn,
        ]);

        AddRow(root, options, SizeType.AutoSize);
        AddRow(root, NewHeader("한국어 입력  ·  비속어·은어·줄임말 그대로 써도 됩니다", null), SizeType.AutoSize);
        AddRow(root, _txtInput, SizeType.Percent, 28);
        AddRow(root, actions, SizeType.AutoSize);
        if (_is3D)
        {
            AddRow(root, NewHeader("VARCO 3D 프롬프트  ·  이미지 스튜디오 입력칸에 붙여 넣으세요", _btnCopyPrompt), SizeType.AutoSize);
            AddRow(root, _txtPrompt, SizeType.Percent, 24);
            AddRow(root, NewHeader("VARCO에서 고를 설정  ·  프롬프트로 대신 넣은 것", null), SizeType.AutoSize);
            AddRow(root, _txtNegative, SizeType.Percent, 32);
        }
        else
        {
            AddRow(root, NewHeader("영어 프롬프트", _btnCopyPrompt), SizeType.AutoSize);
            AddRow(root, _txtPrompt, SizeType.Percent, 34);
            AddRow(root, NewHeader("네거티브 프롬프트", _btnCopyNegative), SizeType.AutoSize);
            AddRow(root, _txtNegative, SizeType.Percent, 12);
        }
        AddRow(root, NewHeader("해석  ·  은어·비속어를 이렇게 읽었습니다", null), SizeType.AutoSize);
        AddRow(root, _txtNotes, SizeType.Percent, _is3D ? 14 : 26);
        AddRow(root, bottom, SizeType.AutoSize);

        Controls.Add(root);
    }

    private static void AddRow(TableLayoutPanel root, Control control, SizeType type, float size = 0)
    {
        root.RowStyles.Add(new RowStyle(type, size));
        root.Controls.Add(control, 0, root.RowCount);
        root.RowCount++;
    }

    private static Control NewHeader(string title, Button? button)
    {
        TableLayoutPanel row = new()
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 8, 0, 0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new Label
        {
            Text = title, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            Font = new Font("맑은 고딕", 10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4),
        }, 0, 0);
        if (button is not null) row.Controls.Add(button, 1, 0);
        return row;
    }

    private static FlowLayoutPanel NewFlow() =>
        new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0) };

    private static Label NewInlineLabel(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(6, 7, 0, 3) };

    private static ComboBox NewCombo(int width) =>
        new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };

    private static Button NewButton(string text) =>
        new() { Text = text, AutoSize = true, Padding = new Padding(8, 2, 8, 2) };

    private static TextBox NewTextBox(bool readOnly) => new()
    {
        Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, ReadOnly = readOnly,
        Font = new Font("맑은 고딕", 11f), BackColor = SystemColors.Window, AcceptsReturn = true,
    };

    // ── 설정 ─────────────────────────────────────────────────
    private void ApplySettings()
    {
        _cboFormat.SelectedIndex = Math.Clamp(_settings.Format, 0, _cboFormat.Items.Count - 1);
        _cboStyle.SelectedIndex = Math.Clamp(_settings.Style, 0, _cboStyle.Items.Count - 1);
        _chkQuality.Checked = _settings.Quality;
        _chkNegative.Checked = _settings.Negative;
        _chkGpu.Checked = _settings.UseGpu;
        _chkExplain.Checked = _settings.Explain;
        _chkAuto.Checked = _settings.Auto;

        // 빠른 모드는 모델이 둘 이상 있을 때만 고를 수 있다.
        bool canChoose = LocalEngine.FindModels().Count >= 2;
        _chkFast.Enabled = canChoose;
        _chkFast.Checked = canChoose && _settings.Fast;
        LocalEngine.FastMode = _chkFast.Checked;
        new ToolTip().SetToolTip(_chkFast, canChoose
            ? "켜면 작은 AI 모델로 바꿔서 2~3배 빨리 변환합니다.\r\n사전에 있는 말은 그대로 정확하지만, 사전에 없는 은어는 덜 정확하게 읽을 수 있습니다."
            : "빠른 모드용 작은 모델 파일이 없습니다.");

        // GPU 사용이나 빠른 모드를 바꾸면 모델을 내렸다가 새 설정으로 다시 올린다.
        _chkGpu.CheckedChanged += async (_, _) => await ReloadModelAsync();
        _chkFast.CheckedChanged += async (_, _) => await ReloadModelAsync();

        // 스타일·퀄리티·네거티브는 번역과 상관없으니 AI를 다시 돌리지 않고 바로 다시 조합한다.
        _cboStyle.SelectedIndexChanged += (_, _) => Recompose();
        _chkQuality.CheckedChanged += (_, _) => Recompose();
        _chkNegative.CheckedChanged += (_, _) => Recompose();

        if (_is3D)
        {
            _cbo3Subject.SelectedIndex = Math.Clamp(_settings.Subject3D, 0, _cbo3Subject.Items.Count - 1);
            _cbo3Pose.SelectedIndex = Math.Clamp(_settings.Pose3D, 0, _cbo3Pose.Items.Count - 1);
            _cbo3View.SelectedIndex = Math.Clamp(_settings.View3D, 0, _cbo3View.Items.Count - 1);
            _cbo3Style.SelectedIndex = Math.Clamp(_settings.Style3D, 0, _cbo3Style.Items.Count - 1);
            _cbo3Background.SelectedIndex = Math.Clamp(_settings.Background3D, 0, _cbo3Background.Items.Count - 1);
            _cbo3Ratio.SelectedIndex = Math.Clamp(_settings.Ratio3D, 0, _cbo3Ratio.Items.Count - 1);
            // 3D 선택은 번역과 상관없으니 AI를 다시 돌리지 않고 바로 다시 조합한다.
            foreach (ComboBox c in new[] { _cbo3Subject, _cbo3Pose, _cbo3View, _cbo3Style, _cbo3Background, _cbo3Ratio })
                c.SelectedIndexChanged += (_, _) => Recompose();
        }
    }

    private Varco3DOptions Current3DOptions() => new(
        (Subject3D)Math.Max(0, _cbo3Subject.SelectedIndex),
        (Pose3D)Math.Max(0, _cbo3Pose.SelectedIndex),
        (View3D)Math.Max(0, _cbo3View.SelectedIndex),
        Math.Max(0, _cbo3Style.SelectedIndex),
        Math.Max(0, _cbo3Background.SelectedIndex),
        _cbo3Ratio.SelectedIndex <= 0 ? "자동" : (string)_cbo3Ratio.SelectedItem!);

    private async Task ReloadModelAsync()
    {
        // 불러오는 중에 바꾼 설정은 그 불러오기가 끝난 뒤 한 번 더 확인해서 반영한다.
        if (_reloading) return;
        _reloading = true;
        try
        {
            while (!IsDisposed &&
                   (LocalEngine.FastMode != _chkFast.Checked || _loadedWithGpu != _chkGpu.Checked || !LocalEngine.IsLoaded))
            {
                _typingTimer.Stop();
                _prereadTimer.Stop();
                _prereadCts?.Cancel();
                _cts?.Cancel();
                while (!_btnConvert.Enabled && !IsDisposed) await Task.Delay(100);   // 진행 중인 불러오기가 끝나기를 기다린다

                _btnConvert.Enabled = false;
                await LocalEngine.UnloadAsync();
                LocalEngine.FastMode = _chkFast.Checked;
                _startedKey = "";
                await PreloadModelAsync();
                if (!LocalEngine.IsLoaded) break;   // 불러오기에 실패했으면 되풀이하지 않는다
            }
        }
        finally
        {
            _reloading = false;
        }
    }

    private void RestartTypingTimer()
    {
        _typingTimer.Stop();
        if (_chkAuto.Checked && Visible) _typingTimer.Start();
    }

    private void RestartPreread()
    {
        _prereadCts?.Cancel();
        _prereadTimer.Stop();
        if (Visible && LocalEngine.IsLoaded) _prereadTimer.Start();
    }

    /// <summary>
    /// 문장을 치는 동안 지금까지 친 부분을 AI에게 미리 읽혀 둔다. 변환을 시작하면 문장 끝부분만 읽으면 되므로
    /// 프롬프트가 그만큼 빨리 나온다.
    /// </summary>
    private async Task PrereadAsync()
    {
        _prereadTimer.Stop();
        if (_running is not null || IsDisposed) return;

        CancellationTokenSource cts = new();
        _prereadCts = cts;
        try
        {
            await LocalEngine.PrereadAsync(_txtInput.Text, _dict, cts.Token);
        }
        finally
        {
            if (_prereadCts == cts) _prereadCts = null;
            cts.Dispose();
        }
    }

    private void Recompose()
    {
        if (_is3D)
        {
            if (_lastEnglish.Length == 0) return;
            (string prompt, string guide) = Varco3D.Compose(_lastEnglish, _lastKorean, Current3DOptions());
            _txtPrompt.Text = prompt;
            _txtNegative.Text = guide;
            return;
        }
        if (_lastEnglish.Length == 0 || _cboStyle.SelectedItem is not StylePreset style) return;
        ConvertRequest req = new("", _lastFormat, style, _chkQuality.Checked, _chkNegative.Checked);
        _txtPrompt.Text = PromptComposer.Compose(_lastEnglish, req);
        _txtNegative.Text = req.Negative ? LocalEngine.DefaultNegative : "";
    }

    private void SaveSettings()
    {
        _settings.Format = _cboFormat.SelectedIndex;
        _settings.Style = _cboStyle.SelectedIndex;
        _settings.Quality = _chkQuality.Checked;
        _settings.Negative = _chkNegative.Checked;
        _settings.UseGpu = _chkGpu.Checked;
        if (_chkFast.Enabled) _settings.Fast = _chkFast.Checked;   // 모델이 하나뿐이라 꺼진 경우엔 저장된 값을 건드리지 않는다
        _settings.Explain = _chkExplain.Checked;
        _settings.Auto = _chkAuto.Checked;
        if (_is3D)
        {
            _settings.Subject3D = _cbo3Subject.SelectedIndex;
            _settings.Pose3D = _cbo3Pose.SelectedIndex;
            _settings.View3D = _cbo3View.SelectedIndex;
            _settings.Style3D = _cbo3Style.SelectedIndex;
            _settings.Background3D = _cbo3Background.SelectedIndex;
            _settings.Ratio3D = _cbo3Ratio.SelectedIndex;
        }
        _settings.Save();
    }

    // ── 변환 ─────────────────────────────────────────────────
    /// <summary>변환 결과를 좌우하는 것(형식·풀이 여부·입력 문장). 같으면 다시 변환할 필요가 없다.</summary>
    private string CurrentKey() =>
        $"{(_is3D ? "3d" : _cboFormat.SelectedIndex.ToString())}|{_chkExplain.Checked}|{_chkFast.Checked}|{_txtInput.Text.Trim()}";

    /// <summary>타이핑을 잠깐 멈추면 버튼을 누르지 않아도 변환을 시작한다.</summary>
    private async Task AutoConvertAsync()
    {
        _typingTimer.Stop();
        if (!_chkAuto.Checked || _txtInput.Text.Trim().Length < 2 || CurrentKey() == _startedKey) return;
        await ConvertAsync(auto: true);
    }

    private async Task ConvertAsync(bool auto = false)
    {
        _typingTimer.Stop();
        _prereadTimer.Stop();
        _prereadCts?.Cancel();   // 미리 읽던 중이면 비켜 주게 한다
        string key = CurrentKey();

        if (_running is { } previous)
        {
            if (key == _startedKey) return;   // 같은 문장을 이미 변환하는 중
            _cts?.Cancel();                   // 문장이 바뀌었으면 하던 것을 끊고 새로 시작한다
            try { await previous; } catch (Exception) { /* 이전 변환의 오류는 그쪽에서 이미 처리했다 */ }
            if (key != CurrentKey()) return;  // 기다리는 사이 또 바뀌었으면 다음 차례에 맡긴다
        }
        if (!_btnConvert.Enabled || IsDisposed) return;   // 모델을 불러오는 중

        Task mine = ConvertCoreAsync(key, auto);
        _running = mine;
        try
        {
            await mine;
        }
        finally
        {
            if (_running == mine) _running = null;
        }
    }

    private async Task ConvertCoreAsync(string key, bool auto)
    {
        string korean = _txtInput.Text.Trim();
        if (korean.Length == 0)
        {
            SetStatus("변환할 한국어를 먼저 입력하세요.");
            _txtInput.Focus();
            return;
        }
        _startedKey = key;

        ConvertRequest req = new(
            korean,
            _is3D ? PromptFormat.Varco3D : _cboFormat.SelectedIndex == 1 ? PromptFormat.Tags : PromptFormat.Sentence,
            (StylePreset)_cboStyle.SelectedItem!,
            _chkQuality.Checked,
            _chkNegative.Checked,
            _chkExplain.Checked,
            _is3D ? Current3DOptions() : null);
        _lastKorean = korean;

        CancellationTokenSource cts = new();
        _cts = cts;
        _promptReady = false;
        SetStatus("변환하는 중…");
        Stopwatch watch = Stopwatch.StartNew();
        bool finished = false;

        try
        {
            _dict.ReloadIfChanged();
            _lastEnglish = "";
            _txtPrompt.Clear();
            _txtNegative.Clear();
            _txtNotes.Clear();

            // 글자가 만들어지는 대로 화면에 보여 준다. 프롬프트가 다 나오면 풀이를 기다리지 않고 바로 쓸 수 있게 한다.
            Progress<PromptResult> partial = new(r =>
            {
                if (finished || IsDisposed) return;
                if (r.Prompt.Length > 0) _txtPrompt.Text = r.Prompt;
                if (r.Notes.Count > 0) _txtNotes.Text = FormatNotes(r.Notes);

                if (r.PromptDone && !_promptReady)
                {
                    _promptReady = true;
                    _lastEnglish = r.English;
                    _lastFormat = req.Format;
                    _txtNegative.Text = _is3D ? r.Guide : r.Negative;
                    SetStatus($"프롬프트 완료 ({watch.Elapsed.TotalSeconds:0.0}초) · 바로 복사해도 됩니다 · 풀이를 쓰는 중…");
                }
            });
            Progress<string> status = new(s => { if (!finished && !_promptReady) SetStatus(s); });

            PromptResult result = await LocalEngine.ConvertAsync(req, _dict, _chkGpu.Checked, status, partial, cts.Token);
            finished = true;

            _lastEnglish = result.English;
            _lastFormat = req.Format;
            Recompose();   // 변환하는 동안 스타일·퀄리티를 바꿨으면 그 설정대로 보여 준다
            _txtNotes.Text = FormatNotes(result.Notes);
            SetStatus($"완료 ({watch.Elapsed.TotalSeconds:0.0}초) · {result.Info}");
        }
        catch (UserFacingException e)
        {
            _startedKey = "";
            // 자동 변환 중에는 타이핑을 방해하지 않도록 창을 띄우지 않고 상태 줄에만 알린다.
            if (auto) SetStatus(e.Message.Split('\r', '\n')[0]);
            else
            {
                SetStatus("변환하지 못했습니다.");
                MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            _startedKey = "";
            SetStatus(_promptReady ? "풀이 쓰기를 멈췄습니다." : "문장이 바뀌어 다시 변환합니다…");
        }
        catch (Exception e)
        {
            _startedKey = "";
            SetStatus("변환하지 못했습니다.");
            if (!auto)
            {
                MessageBox.Show(this, "예상하지 못한 오류가 발생했습니다.\r\n\r\n" + e.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            finished = true;
            cts.Dispose();
            if (_cts == cts) _cts = null;
        }
    }

    private static string FormatNotes(IReadOnlyList<Note> notes)
    {
        if (notes.Count == 0)
        {
            return "따로 풀이할 은어·비속어가 없었습니다.\r\n" +
                   "AI가 잘못 읽은 단어가 있으면 아래 '사전에 단어 추가'에 뜻을 넣으세요. 사전의 뜻이 AI의 해석보다 우선합니다.";
        }

        StringBuilder sb = new();
        foreach (Note n in notes)
        {
            sb.Append("• ").Append(n.Term).Append("  →  ").Append(n.English);
            if (n.Explanation.Length > 0) sb.Append("    (").Append(n.Explanation).Append(')');
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ── 사전 ─────────────────────────────────────────────────
    private void AddTerm()
    {
        string term = _txtNewTerm.Text.Trim();
        string meaning = _txtNewMeaning.Text.Trim();

        if (term.Length == 0 || meaning.Length == 0)
        {
            SetStatus("은어와 영어 뜻을 둘 다 입력하세요.");
            (term.Length == 0 ? _txtNewTerm : _txtNewMeaning).Focus();
            return;
        }
        if (term.Contains('=') || term.StartsWith('#') || term.Contains('\n') || meaning.Contains('\n'))
        {
            SetStatus("은어에는 '='를 쓸 수 없고 '#'으로 시작할 수 없습니다.");
            return;
        }

        try
        {
            _dict.ReloadIfChanged();
            bool replaced = _dict.Contains(term);
            _dict.Add(term, meaning);
            _txtNewTerm.Clear();
            _txtNewMeaning.Clear();
            _txtNewTerm.Focus();
            _startedKey = "";          // 사전이 바뀌었으니 같은 문장도 다시 변환할 수 있게 한다
            RestartTypingTimer();
            SetStatus(replaced
                ? $"'{term}'의 뜻을 바꿨습니다 · 사전 {_dict.Count}개"
                : $"'{term}'을(를) 추가했습니다 · 사전 {_dict.Count}개");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "사전 파일에 쓰지 못했습니다.\r\n" + _dict.FilePath + "\r\n\r\n" + e.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenInNotepad(string path, string doneMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
            SetStatus(doneMessage);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "사전 파일을 열지 못했습니다.\r\n" + path + "\r\n\r\n" + e.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── 기타 동작 ────────────────────────────────────────────
    private void CopyText(string text, string doneMessage)
    {
        if (text.Length == 0)
        {
            SetStatus("복사할 내용이 없습니다.");
            return;
        }
        try
        {
            Clipboard.SetText(text);
            SetStatus(doneMessage);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            SetStatus("클립보드를 다른 프로그램이 쓰고 있습니다. 다시 눌러 주세요.");
        }
    }

    private void SetStatus(string text) => _lblStatus.Text = text;
}
