using System.Text;
using System.Text.RegularExpressions;

namespace KoPrompt;

// ─────────────────────────────────────────────────────────────
//  VARCO 3D 이미지 스튜디오용 프롬프트 (KoPrompt3D 창)
//
//  VARCO 3D는 이미지를 먼저 만들고 그 이미지를 3D 모델로 바꾼다. 3D로 잘 바뀌는 이미지는
//  대상이 하나, 전신·전체가 잘리지 않고 가운데, 흰 배경, 고른 조명, 그림자·글자 없음이어야 한다.
//  아래 틀은 이 계정의 VARCO 이미지 스튜디오 기록에서 3D 변환에 쓰였던 프롬프트 모양을 따랐다.
// ─────────────────────────────────────────────────────────────

public enum Subject3D { Auto, Character, Creature, Prop, Vehicle, Environment }

public enum Pose3D { TPose, APose, AsWritten }

public enum View3D { Auto, Front, ThreeQuarter, Side }

/// <param name="Ratio">"자동" 또는 VARCO 비율 칸의 값("1:1", "4:3", "16:9", "3:4", "9:16").</param>
public sealed record Varco3DOptions(Subject3D Subject, Pose3D Pose, View3D View, int Style, int Background, string Ratio);

public static class Varco3D
{
    public static readonly string[] SubjectLabels = ["대상: 자동", "캐릭터(사람)", "생물·동물", "소품·물건", "탈것", "배경 조각·건물"];
    public static readonly string[] PoseLabels = ["T-포즈", "A-포즈", "입력대로"];
    public static readonly string[] ViewLabels = ["시점: 자동", "정면", "3/4 앞", "옆"];
    public static readonly string[] RatioLabels = ["비율: 자동 추천", "1:1", "4:3", "16:9", "3:4", "9:16"];
    public static readonly string[] BackgroundLabels = ["흰 배경", "연회색 배경"];

    /// <summary>
    /// 화풍: (화면 이름, 캐릭터 앞에 붙는 말, 소품 뒤에 붙는 말, 덧붙이는 문장).
    /// VARCO에서 시험해 보니 "anime-style" 한 낱말로는 반실사에 가깝게 나와서, 화풍을 한 문장 더 적어 강하게 지시한다.
    /// </summary>
    public static readonly (string Label, string Adjective, string Tail, string Sentence)[] Styles =
    [
        ("스타일라이즈드 게임", "stylized", "stylized game asset", ""),
        ("실사 PBR", "realistic", "stylized realistic PBR", "Realistic PBR materials. "),
        ("애니메이션풍", "anime-style", "anime-style game asset", "Japanese anime art style with cel shading and clean outlines, not photorealistic. "),
        ("카툰", "cartoon", "cartoon style game asset", "Cartoon art style with simple bold shapes and flat colors, not photorealistic. "),
        ("로우폴리", "low poly", "low poly game asset", "Low poly art style with flat-shaded geometric facets. "),
        ("클레이·피규어", "clay figure style", "clay figure style", "Handmade clay figure look with soft sculpted forms. "),
    ];

    private static readonly string[] Backgrounds = ["plain pure white background", "plain light-gray background"];

    private const string CharacterWords =
        @"man|men|woman|women|girl|boy|child|children|kid|baby|person|people|lady|gentleman|grandmother|grandfather|mother|father|" +
        @"knight|warrior|soldier|samurai|ninja|pirate|wizard|witch|king|queen|prince|princess|elf|archer|hero|villain|zombie|vampire|" +
        @"angel|demon|fairy|mermaid|ghost|robot|android|cyborg|astronaut|student|schoolgirl|schoolboy|teacher|doctor|nurse|chef|" +
        @"worker|officer|police|firefighter|detective|agent|assassin|thief|gangster|delinquent|bully|idol|singer|dancer|bride|groom|" +
        @"priest|monk|nun|farmer|fisherman|hunter|character|figure|teenager|adult|guy|dude|beauty|scarecrow|snowman|mannequin|" +
        @"merchant|vendor|shopkeeper|goblin|orc|troll|dwarf|gnome|blacksmith|mage|sorcerer|sorceress|cleric|paladin|rogue|bard|" +
        @"viking|barbarian|mercenary|guard|captain|sailor|cowboy|explorer|adventurer|maid|butler|waiter|waitress|scientist|" +
        @"engineer|mechanic|pilot|athlete|boxer|gamer|student|clown|jester|monarch|emperor|empress|shaman|druid|necromancer";

    private const string CreatureWords =
        @"cat|kitten|dog|puppy|wolf|fox|bear|tiger|lion|horse|pony|unicorn|dragon|deer|rabbit|bunny|bird|eagle|owl|penguin|" +
        @"fish|shark|whale|dolphin|turtle|frog|snake|lizard|dinosaur|monster|creature|beast|slime|panda|monkey|elephant|giraffe|" +
        @"cow|pig|sheep|goat|chicken|duck|butterfly|spider|insect|crab|octopus|squirrel|hamster|mouse|bat|camel|crocodile";

    private const string VehicleWords =
        @"car|truck|bus|taxi|motorcycle|bike|bicycle|scooter|train|tram|airplane|plane|jet|helicopter|airship|ship|boat|yacht|" +
        @"motorboat|speedboat|sailboat|canoe|kayak|carriage car|train carriage|" +
        @"submarine|tank|spaceship|spacecraft|rocket|carriage|cart|wagon|van|jeep|tractor|forklift|vehicle|mecha|mech";

    private const string EnvironmentWords =
        @"wall|floor|tile|ground|terrain|building|house|tower|castle|bridge|fence|gate|door|stairs|staircase|roof|ruins|" +
        @"catwalk|pillar|column|arch|platform|wall panel|warehouse|shop|cabin|hut|tent|lighthouse|windmill|fountain|well";

    private const string TallWords =
        @"lamp|streetlight|lantern|pole|tower|tree|statue|bottle|vase|totem|pillar|column|door|lighthouse|candle|torch|" +
        @"mannequin|signpost|sign|cabinet|wardrobe|fridge|refrigerator|vending machine|coat rack|plant|cactus|trophy";

    private const string LongWords =
        @"sword|longsword|greatsword|broadsword|blade|halberd|scythe|polearm|" +
        @"spear|staff|wand|rifle|gun|bow|beam|truss|girder|bridge|bench|sofa|couch|bed|train|boat|ship|fence|catwalk|" +
        @"pipe|log|table|desk|counter|shelf|bookshelf|keyboard|guitar|broom|axe|hammer|katana|lance|oar|crocodile|snake|whale";

    /// <summary>이미지 속 장면(배경)을 가리키는 말. 3D용 이미지는 흰 배경이라 이런 장소·때 구절은 프롬프트에서 뺀다.</summary>
    private const string ScenePlaces =
        @"forest|woods|jungle|street|alley|road|cafe|beach|seaside|shore|room|bedroom|kitchen|office|city|town|village|park|garden|" +
        @"field|meadow|desert|cave|bar|pub|library|subway|station|rooftop|river|lake|sea|ocean|sky|stage|gym|school|classroom|" +
        @"market|mall|store|shop|church|cathedral|temple|hall|venue|battlefield|arena|mountains?|hill|valley|snowfield|space|" +
        @"background|backdrop|scene|setting|landscape|night|day|sunset|sunrise|dusk|dawn|rain|snow|moonlight|sunlight|weather";

    /// <summary>
    /// 번역된 영어 묘사를 VARCO 3D용 프롬프트로 만든다.
    /// 돌려주는 Guide는 VARCO 화면에서 직접 고를 것(비율 등)과, 프롬프트로 대신 넣은 것을 적은 안내문이다.
    /// </summary>
    public static (string Prompt, string Guide) Compose(string english, string korean, Varco3DOptions o)
    {
        List<string> removed = [];
        string desc = StripScene(english.Trim().TrimEnd('.', ','), removed);
        desc = LowerFirst(desc);

        Subject3D subject = o.Subject == Subject3D.Auto ? Classify(desc, korean) : o.Subject;

        // T-포즈·A-포즈로 세울 때는 묘사 속 동작이 자세와 부딪힌다. 손에 드는 동작은 "with 물건"으로 바꾸고("shooting a bow"
        // → "with a bow"), 바꿀 수 없는 동작("smoking", "dancing")은 뺀다.
        List<string> droppedActions = [];
        if (subject is Subject3D.Character or Subject3D.Creature && o.Pose != Pose3D.AsWritten)
            desc = NeutralizeActions(desc, droppedActions);
        string ratio = o.Ratio is "1:1" or "4:3" or "16:9" or "3:4" or "9:16" ? o.Ratio : RecommendRatio(subject, desc);
        bool ratioAuto = o.Ratio != ratio;
        View3D view = o.View != View3D.Auto ? o.View
            : subject == Subject3D.Character && o.Pose != Pose3D.AsWritten ? View3D.Front
            : View3D.ThreeQuarter;
        var style = Styles[Math.Clamp(o.Style, 0, Styles.Length - 1)];
        string background = Backgrounds[Math.Clamp(o.Background, 0, Backgrounds.Length - 1)];
        string viewText = view switch
        {
            View3D.Front => "front view",
            View3D.Side => "side view",
            _ => "front three-quarter view",
        };
        string framing = ratio switch
        {
            "3:4" or "9:16" => ", vertical framing",
            "16:9" or "4:3" => ", horizontal framing",
            _ => "",
        };

        string prompt = subject switch
        {
            Subject3D.Character =>
                $"Full body {style.Adjective} 3D game character: {desc}. {style.Sentence}" +
                (o.Pose switch
                {
                    Pose3D.TPose => "Standing in a strict symmetrical T-pose, arms straight out horizontally, legs slightly apart, ",
                    Pose3D.APose => "Standing in a neutral A-pose, arms slightly away from the torso, fingers gently separated, feet apart, ",
                    _ => "",
                }) +
                $"{viewText}, entire body from head to feet visible and centered{framing}, one character only, " +
                $"{background}, soft even lighting, no cast shadow, no text, no logo",
            Subject3D.Creature =>
                $"Full body {style.Adjective} 3D game creature: {desc}. {style.Sentence}" +
                (o.Pose == Pose3D.AsWritten ? "" : "Standing in a neutral idle pose with all limbs clearly visible, ") +
                $"{viewText}, entire body from head to tail visible and centered{framing}, one creature only, " +
                $"{background}, soft even lighting, no cast shadow, no text, no logo",
            _ =>
                $"Single {(subject == Subject3D.Environment ? "modular game environment piece" : subject == Subject3D.Vehicle ? "vehicle" : "prop")}: " +
                $"{desc}, {style.Tail}. {style.Sentence}Entire object fully visible and centered, {viewText}{framing}, " +
                $"{background}, soft even studio lighting, no cast shadow, no text",
        };

        StringBuilder guide = new();

        // 막힐 만한 낱말이 있으면 맨 앞에 알린다(VARCO는 막을 때 이유를 알려 주지 않는다).
        List<PolicyHit> hits = PolicyCheck.Check(korean, english + " " + prompt);
        if (hits.Count > 0)
        {
            guide.AppendLine(PolicyCheck.Format(hits));
            guide.AppendLine();
        }

        // 서로 다른 물건·생물을 여러 개 늘어놓으면 이미지 한 장에 여러 대상이 나오고, VARCO 3D는 그 이미지를 모델 하나로
        // 만들어서 한 덩어리가 된다. 하나씩 따로 만들라고 알린다.
        if (CountSeparateSubjects(korean, subject) is var many and >= 2)
        {
            guide.AppendLine("[주의: 대상이 여럿]");
            guide.AppendLine($"• 입력에 따로따로인 대상이 {many}개 있습니다. 한 번에 넣으면 그중 일부만 나옵니다");
            guide.AppendLine("  (VARCO 시험: '검, 방패, 투구, 횃불' → 횃불 하나만 나옴). 3D도 이미지 한 장을 모델 하나로 만드니,");
            guide.AppendLine("  하나씩 따로 변환하세요.");
            guide.AppendLine();
        }

        guide.AppendLine("[VARCO 화면에서 직접 고를 것]");
        guide.AppendLine($"• 비율: {ratio}" + (ratioAuto ? $"  ← {SubjectName(subject)}{ShapeReason(subject, desc)}라서 추천" : "  ← 직접 고른 비율"));
        guide.AppendLine("• 모델: GPT Image 2.5로 시험해 봤습니다. Nano Banana도 문장형이라 그대로 붙여 넣으면 됩니다");
        guide.AppendLine("• 장수: 1장으로 먼저 확인하고, 마음에 들면 늘리세요(시험할 때 GPT Image 2.5는 1장에 50포인트)");
        guide.AppendLine("• 3D 프롬프트 스위치: 3D용 조건은 이미 프롬프트에 들어 있습니다(켜 두어도 프롬프트는 바뀌지 않았습니다)");
        guide.AppendLine("• 이미지가 나오면 이미지를 눌러 '3D 생성하기'로 넘기세요(시험할 때 200포인트, 결과는 '내 에셋'에 생김)");
        guide.AppendLine();
        guide.AppendLine("[VARCO에 칸이 없어서 프롬프트로 대신 넣은 것]");
        guide.AppendLine("• 네거티브 프롬프트: 따로 칸이 없어서 'no cast shadow, no text, no logo'를 프롬프트 끝에 넣었습니다");
        guide.AppendLine($"• 비율에 맞는 구도: {(framing.Length > 0 ? framing.TrimStart(',', ' ') : "정사각형이라 따로 넣지 않음")}");
        guide.AppendLine("• 3D 변환 조건: 대상 하나, 전체가 보이게 가운데, " + (o.Background == 1 ? "연회색" : "흰") + " 배경, 고른 조명, 그림자 없음");
        if (subject == Subject3D.Character && o.Pose != Pose3D.AsWritten)
        {
            guide.AppendLine($"• 자세: {PoseLabels[(int)o.Pose]} (3D로 바꾼 뒤 뼈대를 넣기 쉬운 자세)");
            if (Regex.IsMatch(desc, @"\bwith (?:a|an|the|two|his|her)?\s*(?:[a-z-]+\s+){0,2}(?:staff|sword|blade|blades|axe|spear|bow|gun|rifle|shield|wand|hammer|torch|book|lantern)\b"))
                guide.AppendLine("  손에 든 물건이 있으면 팔을 내리고 들 수 있습니다(VARCO 시험: A-포즈 + 지팡이 → 팔을 내리고 지팡이를 듦). 정확한 자세가 필요하면 물건은 따로 만드세요.");
        }
        if (droppedActions.Count > 0)
            guide.AppendLine("• 자세 때문에 바꾸거나 뺀 동작: " + string.Join(", ", droppedActions.Select(r => $"'{r}'")) + " (동작을 살리려면 자세를 '입력대로'로 바꾸세요)");
        if (removed.Count > 0)
            guide.AppendLine("• 뺀 장면: " + string.Join(", ", removed.Select(r => $"'{r}'")) + " (흰 배경과 부딪혀서 뺐습니다. 배경은 VARCO에서 따로 만드세요)");
        return (prompt, guide.ToString().TrimEnd());
    }

    /// <summary>대상 종류를 가늠한다. 사람이 있으면 캐릭터, 없으면 동물 → 탈것 → 소품 → 배경 조각 순.</summary>
    public static Subject3D Classify(string english, string korean)
    {
        List<string> sections = BasicLexicon.SectionsIn(NormalizeKorean(korean));
        string text = english.ToLowerInvariant();
        bool Has(string words) => Regex.IsMatch(text, @"\b(?:" + words + @")s?\b");

        // 인형·피규어·조각상은 사람 모양이어도 소품이다("a Pikachu doll"). 단, 사람이 먼저 나오면("a girl holding a doll") 캐릭터.
        Match toy = Regex.Match(text, @"\b(?:doll|dolls|plush|plushie|stuffed toy|toy|figurine|action figure|statue|sculpture|bust|mannequin|" +
                                      @"armor set|set of armor|armor stand|costume set|outfit set)\b");
        Match person = Regex.Match(text, @"\b(?:" + CharacterWords + @")s?\b");
        bool personToy = Regex.IsMatch(text, @"\b(?:" + CharacterWords + @")s?\s+(?:doll|dolls|plush|plushie|toy|figurine|statue|sculpture|bust|" +
                                             @"armor set|set of armor|armor stand|costume set|outfit set)\b");   // "angel statue", "knight armor set"
        if (toy.Success && (!person.Success || toy.Index < person.Index || personToy || person.Value is "figure" or "mannequin"))
            return Subject3D.Prop;

        if (sections.Contains("사람") || Has(CharacterWords)) return Subject3D.Character;
        if (sections.Contains("동물") || Has(CreatureWords)) return Subject3D.Creature;
        if (sections.Contains("탈것") || Has(VehicleWords)) return Subject3D.Vehicle;
        // 사람만 하는 동작(옷을 입거나 물건을 메고 드는)이 묘사돼 있으면 사람 낱말을 몰라도 캐릭터다("goblin merchant carrying a backpack").
        if (Regex.IsMatch(text, @"\b(?:wearing|carrying|holding|wielding|dressed in|clad in)\b") && !toy.Success) return Subject3D.Character;
        if (sections.Contains("가구·물건") || sections.Contains("옷·꾸밈") || sections.Contains("음식")) return Subject3D.Prop;
        if (sections.Contains("장소·건물") || Has(EnvironmentWords)) return Subject3D.Environment;
        return Subject3D.Prop;
    }

    private static string NormalizeKorean(string korean) => LocalEngine.NormalizeRequest(korean);

    /// <summary>
    /// 쉼표나 띄어쓰기로 늘어놓은 항목 가운데 따로따로인 대상(사람·동물·물건·탈것·건물)이 몇 개인지.
    /// "은발 엘프 마법사, 보라색 로브, 지팡이 든"처럼 한 인물의 옷·소지품을 늘어놓은 것은 대상 하나로 본다.
    /// </summary>
    private static int CountSeparateSubjects(string korean, Subject3D subject)
    {
        string text = NormalizeKorean(korean);
        string[] items = text.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool bareList = false;
        if (items.Length < 2)
        {
            // 쉼표 없이 낱말만 늘어놓은 경우("소녀 고양이 나무 집")는 낱말 하나하나를 항목으로 본다.
            string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 3 && words.All(BasicLexicon.IsWord))
            {
                items = words;
                bareList = true;
            }
        }

        string[] entity = ["사람", "동물", "가구·물건", "탈것", "장소·건물", "음식"];
        int count = 0;
        bool hasPerson = subject == Subject3D.Character && !bareList;   // 캐릭터면 물건은 처음부터 그 인물의 장비로 본다
        string previousToken = "";
        foreach (string item in items)
        {
            if (Regex.IsMatch(item, @"(?:없이|없는|빼고|제외)$")) continue;   // "마네킹 없이"는 대상이 아니다
            bool carried = Regex.IsMatch(item, @"(?:^|\s)\S*(?:든|들고|메고|멘|쥔|쥐고|찬|차고|입은|입고|쓴|쓰고)(?:\s|$)");

            // 한 항목 안의 낱말도 하나씩 센다("소녀와 강아지" → 2). 바로 붙은 같은 종류의 낱말은 한 대상이다("엘프 마법사").
            string previous = "";
            foreach (string token in item.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                List<string> sections = BasicLexicon.SectionsIn(token);
                string kind = sections.FirstOrDefault(entity.Contains) ?? "";
                bool sameAsPrevious = kind.Length > 0 && kind == previous && !Regex.IsMatch(previousToken, "(?:와|과|랑|하고|이랑)$");
                previous = kind;
                previousToken = token;
                if (kind.Length == 0 || sameAsPrevious) continue;

                // 사람 뒤에 나오는 물건·옷·음식은 그 사람의 장비로 본다("도끼 들고 털모자", "기사, 검, 방패").
                bool equipment = !sections.Any(s => s is "사람" or "동물" or "탈것" or "장소·건물");
                if (hasPerson && (equipment || carried)) continue;
                if (sections.Contains("사람")) hasPerson = true;
                count++;
            }
        }

        // "고양이 세 마리", "남자 두 명"처럼 여럿을 세는 말. 물건 개수("사과 다섯 개")는 캐릭터가 아닐 때만 여럿으로 본다.
        Match many = Regex.Match(text, @"(?:두|세|네|다섯|여섯|일곱|여덟|아홉|열|여러|몇|[2-9]|\d{2,})\s*(?<unit>마리|명|개|대|채|그루|송이|자루)");
        if (many.Success && (many.Groups["unit"].Value is "마리" or "명" || subject != Subject3D.Character))
            count = Math.Max(count, 2);
        return count;
    }

    /// <summary>대상과 모양에 맞는 VARCO 비율. 이 계정의 기록에서 쓰던 비율(캐릭터 3:4, 긴 철골 16:9, 가로등 3:4, 벽판 4:3, 바닥판 1:1)을 따랐다.</summary>
    public static string RecommendRatio(Subject3D subject, string english)
    {
        string text = english.ToLowerInvariant();
        bool Has(string words) => Regex.IsMatch(text, @"\b(?:" + words + @")s?\b");
        return subject switch
        {
            Subject3D.Character => "3:4",
            Subject3D.Creature => Has("snake|whale|crocodile|dragon|horse|cat|dog|wolf|tiger|lion|fox|deer") ? "4:3" : "1:1",
            Subject3D.Vehicle => "16:9",
            Subject3D.Environment => Has(TallWords) ? "3:4" : Has(LongWords) ? "16:9" : Has("floor|tile|ground|slab|terrain") ? "1:1" : "4:3",
            _ => Has(TallWords) ? "3:4" : Has(LongWords) ? "16:9" : "1:1",
        };
    }

    private static string SubjectName(Subject3D s) => s switch
    {
        Subject3D.Character => "서 있는 캐릭터",
        Subject3D.Creature => "생물",
        Subject3D.Vehicle => "탈것",
        Subject3D.Environment => "배경 조각",
        _ => "소품",
    };

    private static string ShapeReason(Subject3D s, string english)
    {
        if (s is Subject3D.Character or Subject3D.Vehicle) return "";
        string text = english.ToLowerInvariant();
        if (Regex.IsMatch(text, @"\b(?:" + TallWords + @")s?\b")) return "(세로로 긴 모양)";
        if (Regex.IsMatch(text, @"\b(?:" + LongWords + @")s?\b")) return "(가로로 긴 모양)";
        return "";
    }

    /// <summary>"… in a forest", "… on a rainy street at night" 같은 장소·때 구절을 뗀다(3D용 이미지는 흰 배경).</summary>
    private static string StripScene(string text, List<string> removed)
    {
        Regex scene = new(
            @"(?:,\s*|\s+)(?:in|at|on|inside|along|by|near|under|within|through|across|against|during|amid|amidst|among|beneath|over)\s+" +
            @"(?:(?:a|an|the|his|her|their)\s+)?(?:[a-z'-]+\s+){0,3}?(?:" + ScenePlaces + @")\b(?:\s+(?:at|in|on|during)\s+(?:a|an|the)?\s*[a-z'-]+(?:\s+[a-z'-]+)?)?",
            RegexOptions.IgnoreCase);
        string result = scene.Replace(text, m =>
        {
            removed.Add(m.Value.Trim(' ', ','));
            return "";
        });
        result = Regex.Replace(result, @"\s*,\s*,", ",");
        result = Regex.Replace(result, @"\s+", " ").Trim(' ', ',');
        return result.Length == 0 ? text : result;
    }

    /// <summary>손에 드는 동작: "shooting a bow" → "with a bow".</summary>
    private const string HoldingVerbs =
        @"holding|wielding|carrying|shooting|aiming|swinging|brandishing|gripping|clutching|playing|reading|drinking|eating|" +
        @"sipping|using|raising|waving|pointing|drawing|throwing|hugging|cradling";

    /// <summary>자세와 부딪혀서 빼는 동작.</summary>
    private const string PoseVerbs =
        @"smoking|running|dancing|jumping|leaping|walking|sitting|kneeling|lying|crouching|sleeping|dozing|fighting|battling|" +
        @"flying|swimming|climbing|falling|kicking|punching|singing|laughing|crying|screaming|yawning|posing|chasing|stretching|" +
        @"lounging|leaning|riding|sunbathing|studying|working|cooking|serving|taking a selfie|glaring|staring|gazing|looking";

    private static string NeutralizeActions(string desc, List<string> dropped)
    {
        string text = Regex.Replace(
            desc,
            @"\b(?:" + HoldingVerbs + @")\s+(?=(?:a|an|the|his|her|their|two|three|\w+s)\b)",
            m =>
            {
                dropped.Add(m.Value.Trim() + " → with");
                return "with ";
            },
            RegexOptions.IgnoreCase);

        text = Regex.Replace(
            text,
            @"(?:,\s*|\s+)(?:while\s+|and\s+)?(?:" + PoseVerbs + @")\b(?:\s+(?:on|in|at|with|through|over|into|from|across|toward|towards|around|under|beside|by)?\s*(?:a|an|the|his|her|their)?\s*[a-z'-]+)?",
            m =>
            {
                dropped.Add(m.Value.Trim(' ', ','));
                return "";
            },
            RegexOptions.IgnoreCase);

        text = Regex.Replace(text, @"\s+", " ").Trim(' ', ',');
        return text.Length == 0 ? desc : text;
    }

    private static string LowerFirst(string text)
    {
        if (text.Length < 2 || !char.IsUpper(text[0]) || char.IsUpper(text[1])) return text;
        string firstWord = Regex.Match(text, "^[A-Za-z]+").Value;
        if (firstWord is "Korean" or "Japanese" or "Chinese" or "American" or "Han" or "Studio" or "Rembrandt") return text;
        return char.ToLowerInvariant(text[0]) + text[1..];
    }
}
