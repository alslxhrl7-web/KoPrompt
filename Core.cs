using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KoPrompt;

public enum PromptFormat { Sentence, Tags }

public sealed record StylePreset(string Label, string English)
{
    public static readonly StylePreset[] All =
    [
        new("스타일 없음", ""),
        new("실사 사진", "photorealistic photograph, natural lighting, shallow depth of field, 35mm lens"),
        new("시네마틱", "cinematic film still, dramatic lighting, anamorphic lens, film grain"),
        new("애니메이션", "anime style, clean line art, vibrant colors, cel shading"),
        new("웹툰·만화", "Korean webtoon style, clean digital line art, flat colors"),
        new("디지털 일러스트", "digital illustration, painterly, richly detailed"),
        new("3D 렌더", "3D render, soft global illumination, physically based materials"),
        new("유화", "oil painting, visible brushstrokes, rich colors"),
        new("수채화", "watercolor painting, soft washes, paper texture"),
        new("픽셀 아트", "pixel art, 16-bit, limited color palette"),
        new("사이버펑크", "cyberpunk, neon lights, rainy night city"),
        new("흑백 사진", "black and white photograph, high contrast, film grain"),
    ];

    public override string ToString() => Label;
}

/// <param name="Explain">사전에 없는 은어를 AI가 어떻게 읽었는지 풀이까지 쓰게 할지. 끄면 프롬프트만 쓰고 끝낸다.</param>
public sealed record ConvertRequest(
    string Korean, PromptFormat Format, StylePreset Style, bool Quality, bool Negative, bool Explain = true);

public sealed record Note(string Term, string English, string Explanation);

/// <param name="English">스타일·퀄리티 태그를 붙이기 전의 번역문. 옵션만 바꿨을 때 AI 없이 다시 조합하는 데 쓴다.</param>
/// <param name="PromptDone">프롬프트는 다 써졌는지(풀이만 남았거나 전부 끝났는지).</param>
public sealed record PromptResult(
    string Prompt, string Negative, IReadOnlyList<Note> Notes, string Info, string English = "", bool PromptDone = false);

/// <summary>사용자에게 그대로 보여줄 수 있는 오류.</summary>
public sealed class UserFacingException(string message) : Exception(message);

/// <summary>
/// 프로그램이 쓰는 파일은 전부 사용자 폴더(C:\Users\이름\KoPrompt) 한 곳에 둔다.
/// 그래서 KoPrompt.exe는 바탕화면이든 어디든 혼자 놓여 있어도 된다.
/// AppData 아래에는 두지 않는다. 스토어 앱처럼 포장된(MSIX) 프로그램이 AppData에 파일을 넣으면 Windows가
/// 그 프로그램 전용 사본 폴더로 돌려 버려서, 거기서 넣은 모델 파일이 KoPrompt.exe에는 보이지 않는다.
/// </summary>
public static class AppPaths
{
    public static string Data { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "KoPrompt");

    public static string Models => Path.Combine(Data, "models");
    public static string Cache => Path.Combine(Data, "cache");
    public static string UserDictionary => Path.Combine(Data, "은어사전.txt");
    public static string BuiltInReference => Path.Combine(Data, "기본사전_참고용.txt");
    public static string Settings => Path.Combine(Data, "settings.json");
}

// ─────────────────────────────────────────────────────────────
//  은어 사전: exe에 든 기본 사전 위에 사용자의 '내 사전' 파일을 덮어 쓴다
// ─────────────────────────────────────────────────────────────
public sealed class SlangDictionary
{
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    private const string UserHeader =
        "# ─────────────────────────────────────────────────────────────\r\n" +
        "#  KoPrompt 내 은어 사전\r\n" +
        "#\r\n" +
        "#  형식:   한국어 = 영어 뜻          ('#'으로 시작하는 줄은 무시됩니다)\r\n" +
        "#  - 여기 적은 뜻이 프로그램에 들어 있는 기본 사전과 AI의 해석보다 우선합니다.\r\n" +
        "#  - 기본 사전의 단어를 끄고 싶으면 뜻을 비워서   단어 =   라고 적으세요.\r\n" +
        "#  - 뜻 앞에 '!'를 붙이면 AI가 돌려 말하더라도 그 영어를 프롬프트에 반드시 넣습니다. (예:  야스 = !having sex)\r\n" +
        "#  - 저장하면 다음 변환부터 바로 적용됩니다.\r\n" +
        "#  - 기본 사전 내용은 같은 폴더의 '기본사전_참고용.txt'에서 볼 수 있습니다.\r\n" +
        "#    (그 파일은 보기용이라 고쳐도 반영되지 않습니다. 고칠 단어는 이 파일에 적으세요.)\r\n" +
        "# ─────────────────────────────────────────────────────────────\r\n\r\n";

    /// <summary>
    /// 사전의 뜻 맨 앞에 붙이는 표시. AI가 그 말을 빼거나 돌려 말하더라도 그 영어를 프롬프트에 반드시 넣는다.
    /// Annotate가 돌려주는 Note에서는 Explanation에 이 값이 담긴다.
    /// </summary>
    public const string MustAppear = "!";

    private List<KeyValuePair<string, string>> _entries = [];   // 긴 단어 우선
    private DateTime _loadedStamp;

    /// <summary>사용자가 고치는 '내 사전' 파일. 기본 사전은 exe 안에 들어 있다.</summary>
    public string FilePath { get; } = AppPaths.UserDictionary;
    public int Count => _entries.Count;

    public SlangDictionary()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            if (!File.Exists(FilePath)) File.WriteAllText(FilePath, UserHeader, Utf8Bom);

            // 기본 사전을 사용자가 들여다볼 수 있게 보기용 사본을 둔다. 새 버전이면 덮어쓴다.
            byte[] reference = [.. Utf8Bom.GetPreamble(), .. Utf8Bom.GetBytes(ReadEmbeddedDefault())];
            if (!File.Exists(AppPaths.BuiltInReference) || new FileInfo(AppPaths.BuiltInReference).Length != reference.Length)
                File.WriteAllBytes(AppPaths.BuiltInReference, reference);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 파일을 못 만들어도 기본 사전만으로 동작한다.
        }
        Reload();
    }

    private static string ReadEmbeddedDefault()
    {
        using Stream s = typeof(SlangDictionary).Assembly.GetManifestResourceStream("slang.txt")
            ?? throw new InvalidOperationException("기본 사전이 exe에 포함되어 있지 않습니다.");
        using StreamReader r = new(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>내 사전 파일이 바뀌었으면 다시 읽는다.</summary>
    public void ReloadIfChanged()
    {
        DateTime stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
        if (stamp != _loadedStamp) Reload();
    }

    public void Reload()
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        ReadInto(map, ReadEmbeddedDefault(), allowRemove: false);

        _loadedStamp = DateTime.MinValue;
        try
        {
            if (File.Exists(FilePath))
            {
                ReadInto(map, File.ReadAllText(FilePath, Encoding.UTF8), allowRemove: true);
                _loadedStamp = File.GetLastWriteTimeUtc(FilePath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 내 사전을 못 읽으면 기본 사전만 쓴다.
        }

        _entries = [.. map.OrderByDescending(p => p.Key.Length).ThenBy(p => p.Key, StringComparer.Ordinal)];
    }

    private static void ReadInto(Dictionary<string, string> map, string text, bool allowRemove)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (key.Length == 0) continue;

            if (value.Length > 0) map[key] = value;   // 같은 단어가 여러 번 나오면 마지막 줄이 이김
            else if (allowRemove) map.Remove(key);     // "단어 =" 는 기본 사전의 그 단어를 끈다
        }
    }

    /// <summary>내 사전 파일 끝에 단어를 추가한다. 기본 사전에 있는 단어면 새 뜻이 우선한다.</summary>
    public void Add(string key, string value)
    {
        Directory.CreateDirectory(AppPaths.Data);
        string existing = File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8) : UserHeader;
        string separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\r\n";
        File.WriteAllText(FilePath, existing + separator + key + " = " + value + "\r\n", Utf8Bom);
        Reload();
    }

    public bool Contains(string key) => _entries.Exists(e => e.Key == key);

    /// <summary>
    /// 문장 속 은어 바로 뒤에 사전의 뜻을 "은어(=영어 뜻)" 꼴로 끼워 넣는다. 긴 단어부터 맞춰 보며,
    /// 찾은 단어는 matches에 기록한다. 같은 단어가 또 나오면 처음 한 번만 뜻을 붙인다.
    /// 문장을 이어서 치면 결과도 뒤에만 덧붙으므로, 치는 동안 앞부분을 AI에게 미리 읽혀 둘 수 있다.
    /// </summary>
    public string Annotate(string text, out List<Note> matches)
    {
        matches = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        StringBuilder sb = new(text.Length + 64);

        int i = 0;
        while (i < text.Length)
        {
            KeyValuePair<string, string>? hit = null;
            foreach (var entry in _entries)
            {
                if (string.CompareOrdinal(text, i, entry.Key, 0, entry.Key.Length) == 0)
                {
                    hit = entry;
                    break;
                }
            }

            if (hit is { } h)
            {
                sb.Append(h.Key);
                if (seen.Add(h.Key))
                {
                    // 뜻이 '!'로 시작하면 "프롬프트에 반드시 넣을 것" 표시다. 모델에게는 뜻만 보여 준다.
                    bool must = h.Value.StartsWith(MustAppear, StringComparison.Ordinal);
                    string value = must ? h.Value[MustAppear.Length..].TrimStart() : h.Value;
                    sb.Append("(=").Append(value).Append(')');
                    matches.Add(new Note(h.Key, value, must ? MustAppear : ""));
                }
                i += h.Key.Length;
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }

        return sb.ToString();
    }
}

// ─────────────────────────────────────────────────────────────
//  기본 낱말 사전: 흔한 낱말의 영어를 입력에 붙여 작은 모델이 엉뚱한 낱말을 고르지 않게 한다
// ─────────────────────────────────────────────────────────────
public static class BasicLexicon
{
    /// <summary>기본 낱말 사전에서 찾은 뜻을 표시하는 Note.Explanation 값. 화면의 해석 칸에는 보이지 않는다.</summary>
    public const string Marker = "기본";

    /// <summary>뜻이 하나인 낱말.</summary>
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal);

    /// <summary>뜻이 여럿인 낱말. 가까이 있는 단서 낱말로 뜻을 고른다(basic.txt 맨 아래의 형식 설명 참고).</summary>
    private static readonly Dictionary<string, Sense[]> Senses = new(StringComparer.Ordinal);

    /// <param name="English">빈 문자열이면 "뜻을 붙이지 않음".</param>
    private sealed record Sense(string English, bool IsDefault, string[] Cues);

    static BasicLexicon() => Load();

    /// <summary>낱말 끝에서 떼어 볼 조사(긴 것부터).</summary>
    private static readonly string[] Particles =
    [
        "에서는", "에서도", "으로는", "에게는", "이랑은",
        "에서", "에게", "한테", "으로", "처럼", "보다", "까지", "부터", "하고", "이랑", "이나",
        "랑", "은", "는", "이", "가", "을", "를", "의", "에", "로", "와", "과", "도", "만",
    ];

    /// <summary>
    /// 한 글자 낱말에서 떼어도 되는 조사. "강도"(robber)·"별로"·"문의"(inquiry)·"강의"(lecture)처럼
    /// 한 글자 낱말 + 도/로/의/만 이 다른 낱말이 되는 경우가 많아서 이것들은 뺐다.
    /// 은/는도 뺐다: "낮은"(low)·"쥐는"(holding)·"비는"(praying)처럼 꾸밈말 어미와 모양이 같다.
    /// </summary>
    private static readonly HashSet<string> OneSyllableParticles =
        ["이", "가", "을", "를", "에", "에서", "와", "과", "처럼", "이랑", "랑"];

    /// <summary>뜻을 고를 때 단서로 보는 최대 거리(글자 수). 멀리 떨어진 말이 엉뚱한 뜻을 고르지 않게 한다.</summary>
    private const int MaxCueDistance = 8;

    private static void Load()
    {
        using Stream? s = typeof(BasicLexicon).Assembly.GetManifestResourceStream("basic.txt");
        if (s is null) return;
        using StreamReader r = new(s, Encoding.UTF8);
        foreach (string raw in r.ReadToEnd().Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0) continue;

            if (value.Contains('|') || value.Contains('['))
                Senses[key] = [.. value.Split('|').Select(ParseSense)];
            else
                Map[key] = value;
        }
    }

    /// <summary>"snow* [오는, 내리]" 꼴의 뜻 하나를 읽는다.</summary>
    private static Sense ParseSense(string part)
    {
        string text = part.Trim();
        string[] cues = [];
        int open = text.IndexOf('[');
        if (open >= 0)
        {
            int close = text.IndexOf(']', open);
            cues = [.. text[(open + 1)..(close < 0 ? text.Length : close)].Split(',').Select(c => c.Trim()).Where(c => c.Length > 0)];
            text = text[..open].Trim();
        }
        bool isDefault = text.EndsWith('*');
        if (isDefault) text = text[..^1].Trim();
        return new Sense(text == "-" ? "" : text, isDefault, cues);
    }

    private static bool Known(string key) => Map.ContainsKey(key) || Senses.ContainsKey(key);

    /// <summary>조사 없이 그대로 기본 낱말 사전에 있는 낱말인지.</summary>
    public static bool IsWord(string word) => Known(word);

    /// <summary>낱말 하나(조사가 붙어 있어도 됨)가 사전의 어느 말인지. 돌려주는 값은 조사·"들"을 뗀 낱말.</summary>
    private static string? MatchKey(string word)
    {
        if (Known(word)) return word;
        if (word.EndsWith('들') && word.Length > 1 && Known(word[..^1])) return word[..^1];

        foreach (string p in Particles)
        {
            if (word.Length <= p.Length || !word.EndsWith(p, StringComparison.Ordinal)) continue;
            string stem = word[..^p.Length];
            if (stem.EndsWith('들') && stem.Length > 1) stem = stem[..^1];
            if (stem.Length == 1 && !OneSyllableParticles.Contains(p)) continue;
            if (Known(stem)) return stem;
        }
        return null;
    }

    /// <summary>
    /// 문장 text의 index 자리에 있는 낱말 key의 뜻. 뜻이 여럿이면 가장 가까운 단서로 고르고, 단서가 없으면 * 뜻을,
    /// 그것도 없으면 "뜻1 / 뜻2"(AI가 문맥으로 고르게)를 돌려준다. 빈 문자열이면 뜻을 붙이지 않는다.
    /// </summary>
    private static string Resolve(string key, string text, int index)
    {
        if (Map.TryGetValue(key, out string? single)) return single;

        Sense[] senses = Senses[key];
        int end = index + key.Length;
        Sense? best = null;
        int bestDistance = int.MaxValue;
        bool tie = false;
        foreach (Sense sense in senses)
        {
            int distance = sense.Cues.Select(c => CueDistance(c, text, index, end)).DefaultIfEmpty(int.MaxValue).Min();
            if (distance < bestDistance)
            {
                (best, bestDistance, tie) = (sense, distance, false);
            }
            else if (distance == bestDistance && distance != int.MaxValue && best?.English != sense.English)
            {
                tie = true;
            }
        }
        if (best is not null && bestDistance != int.MaxValue && !tie) return best.English;

        if (senses.FirstOrDefault(s => s.IsDefault) is { } fallback) return fallback.English;
        if (senses.Any(s => s.English.Length == 0)) return "";   // "뜻 없음"이 될 수도 있는 말은 함부로 뜻을 붙이지 않는다
        return string.Join(" / ", senses.Select(s => s.English));
    }

    /// <summary>단서가 낱말(start..end)에서 얼마나 떨어져 있는지(글자 수). 없으면 int.MaxValue.</summary>
    private static int CueDistance(string cue, string text, int start, int end)
    {
        if (cue.StartsWith('>'))   // 바로 뒤에 붙어 있을 때만 (조사 등)
        {
            string c = cue[1..];
            return end + c.Length <= text.Length && string.CompareOrdinal(text, end, c, 0, c.Length) == 0 ? 0 : int.MaxValue;
        }

        bool beforeOnly = cue.StartsWith('<');
        string word = beforeOnly ? cue[1..] : cue;
        int best = int.MaxValue;
        for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
        {
            if (i >= start && i < end) continue;   // 낱말 자신
            int distance = i >= end ? i - end
                : i + word.Length > start ? word.Length - (end - start)   // 단서가 낱말을 품은 경우("큰 눈"): 붙은 앞말 길이만큼
                : start - (i + word.Length);
            if (beforeOnly && (i >= end || distance > 3)) continue;
            if (distance > MaxCueDistance) continue;
            best = Math.Min(best, distance);
        }
        return best;
    }

    /// <summary>
    /// 목록의 한 항목처럼 따로 떨어진 낱말의 뜻. context는 그 낱말이 들어 있는 전체 입력(뜻이 여럿일 때 단서를 찾는 곳).
    /// 사전에 없거나, 뜻을 붙이지 않는 경우거나, 문맥으로도 뜻을 정하지 못했으면 null.
    /// </summary>
    /// <param name="allowParticles">false면 조사가 붙은 낱말("고양이랑")은 null. 목록 항목에서 관계를 잃지 않게 한다.</param>
    public static string? Meaning(string word, string context, bool allowParticles = true)
    {
        if (MatchKey(word) is not { } key) return null;
        if (!allowParticles && key != word && key + "들" != word) return null;
        int index = Math.Max(0, context.IndexOf(word, StringComparison.Ordinal));
        string english = Resolve(key, context.Contains(word, StringComparison.Ordinal) ? context : word, index);
        return english.Length == 0 || english.Contains(" / ", StringComparison.Ordinal) ? null : english;
    }

    /// <summary>
    /// 띄어쓰기로 나눈 낱말마다 기본 뜻을 찾아 "낱말(=영어)조사" 꼴로 끼워 넣는다. 은어 사전의 뜻 "(=…)"이 이미 붙은
    /// 낱말은 건드리지 않는다. 찾은 낱말은 Explanation = Marker 로 matches에 덧붙인다(같은 낱말은 한 번만).
    /// </summary>
    public static string Annotate(string text, List<Note> matches)
    {
        if (Map.Count == 0 && Senses.Count == 0) return text;
        HashSet<string> seen = [.. matches.Select(m => m.Term)];

        return Regex.Replace(text, @"[^\s,.!?~""'()]+(?:\(=[^)]*\)[^\s,.!?~""'()]*)?", m =>
        {
            string word = m.Value;
            if (word.Contains("(=", StringComparison.Ordinal)) return word;   // 은어 사전이 먼저 뜻을 붙였다
            if (MatchKey(word) is not { } key) return word;

            string english = Resolve(key, text, m.Index);
            if (english.Length == 0) return word;

            if (seen.Add(key)) matches.Add(new Note(key, english, Marker));
            return key + "(=" + english + ")" + word[key.Length..];
        });
    }
}

// ─────────────────────────────────────────────────────────────
//  설정 (화면에서 고른 옵션만 기억한다)
// ─────────────────────────────────────────────────────────────
public sealed class AppSettings
{
    private static string OldFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KoPrompt", "settings.json");

    public int Format { get; set; }
    public int Style { get; set; }
    public bool Quality { get; set; } = true;
    public bool Negative { get; set; }
    public bool UseGpu { get; set; } = true;
    public bool Explain { get; set; } = true;
    public bool Auto { get; set; } = true;
    public bool Fast { get; set; }

    public static AppSettings Load()
    {
        foreach (string path in new[] { AppPaths.Settings, OldFilePath })   // 예전 버전의 설정도 이어받는다
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // 설정 파일이 깨졌으면 기본값으로 시작
            }
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 설정 저장 실패는 변환 기능에 영향이 없으므로 무시
        }
    }
}
