using System.Text;
using System.Text.RegularExpressions;

namespace KoPrompt;

// ─────────────────────────────────────────────────────────────
//  막힐 수 있는 부분 검사
//
//  VARCO 3D 이미지 스튜디오는 막을 때 "이미지 생성이 콘텐츠 정책에 걸려 중단되었습니다"라고만 하고
//  무엇이 문제인지 알려 주지 않는다. 그래서 프롬프트(와 한국어 입력)에서 콘텐츠 정책에 걸릴 만한 낱말을 찾아
//  어떤 낱말이 어떤 이유로 걸릴 수 있는지 보여 준다. VARCO의 그림은 GPT Image(OpenAI)·Nano Banana(Google)가
//  만들므로 두 회사의 일반적인 이미지 생성 제한을 기준으로 삼았다. VARCO가 따로 공개한 금지 목록은 없다.
//
//  이 검사는 원인을 알려 주기만 한다. 막히는 내용을 돌려 말해 통과시키는 기능은 넣지 않는다
//  (VARCO 약관은 서비스의 보호 장치를 우회하는 행위를 금지한다).
// ─────────────────────────────────────────────────────────────

/// <param name="Level">높음(거의 확실히 막힘) / 중간(막힐 수 있음) / 낮음(보통 괜찮지만 장면에 따라).</param>
public sealed record PolicyHit(string Level, string Category, string Term, string Why);

public static class PolicyCheck
{
    private sealed record Rule(string Level, string Category, string English, string Korean, string Why);

    private const string MinorEnglish =
        @"child|children|kid|kids|little girl|little boy|young girl|young boy|schoolgirl|schoolboy|teen|teenager|teenage|" +
        @"underage|minor|loli|lolita|shota|toddler|baby|elementary school|middle school|high school student|preteen";

    private const string MinorKorean = @"초등|중학생|중딩|고등학생|고딩|여고생|남고생|여중생|남중생|미성년|로리|쇼타|아기|꼬마|어린이|어린 소녀|어린 소년|교복";

    private const string SexualEnglish =
        @"nude|nudity|naked|topless|bottomless|nipples?|genitals?|penis|vagina|sex|sexual|sexually|intercourse|orgasm|masturbat\w*|" +
        @"erotic|porn\w*|hentai|nsfw|fetish|bondage|bdsm|stripper|striptease|lewd|aroused|having sex|doggy style|" +
        @"blowjob|explicit|x-rated|seductively undressing|spread legs";

    private const string RevealingEnglish =
        @"bikini|lingerie|underwear|panties|bra|swimsuit|see-through|cleavage|sexy|seductive|revealing|braless|" +
        @"thong|micro skirt|wet t-shirt|skimpy";

    private static readonly Rule[] Rules =
    [
        new("높음", "성적 노출·성행위", SexualEnglish, @"알몸|나체|누드|야스|섹스|자위|성기|19금|음란|에로|홀딱 벗|다 벗은|노출된 가슴|젖꼭지",
            "GPT Image·Nano Banana 모두 노출·성적 장면은 만들지 않습니다."),
        new("높음", "잔혹·고어", @"gore|gory|decapitat\w*|dismember\w*|severed (?:head|limb|arm|leg)|entrails|guts spilling|mutilat\w*|" +
                                 @"disembowel\w*|corpses?|dead bod(?:y|ies)|blood splatter|torture\w*|beheading|exposed bones?|flayed|cannibal\w*",
            @"고어|잘린 머리|토막|내장|시체|시신|고문|참수|사지 절단",
            "신체 훼손·시체·고문 같은 잔혹한 장면은 막힙니다."),
        new("높음", "혐오 상징·극단주의", @"nazis?|swastika|kkk|ku klux klan|hitler|isis|white power|hate symbol",
            @"나치|하켄크로이츠|히틀러|KKK|IS 깃발",
            "혐오 상징이나 극단주의 단체 표시는 막힙니다."),
        new("중간", "저작권 캐릭터·작품", @"pok[eé]mon|pikachu|mario|luigi|zelda|sonic the hedgehog|disney|mickey mouse|minnie mouse|elsa|" +
                                       @"marvel|spider-?man|iron man|captain america|batman|superman|avengers|star wars|darth vader|yoda|" +
                                       @"harry potter|hogwarts|naruto|one piece|luffy|dragon ball|goku|sailor moon|hello kitty|totoro|" +
                                       @"doraemon|pororo|kakao friends|minecraft|fortnite|league of legends|genshin|overwatch|shrek|minions?",
            @"피카츄|포켓몬|마리오|루이지|젤다|소닉|디즈니|미키|미니마우스|엘사|마블|스파이더맨|아이언맨|캡틴 아메리카|배트맨|슈퍼맨|어벤져스|" +
            @"스타워즈|다스 베이더|요다|해리포터|호그와트|나루토|원피스|루피|드래곤볼|손오공|세일러문|헬로키티|토토로|도라에몽|뽀로로|짱구|" +
            @"카카오프렌즈|라이언|마인크래프트|포트나이트|리그 오브 레전드|원신|오버워치|슈렉|미니언",
            "남의 캐릭터·작품을 그대로 그리는 것은 저작권 때문에 막히는 일이 많습니다. 새 캐릭터로 설명하세요."),
        new("중간", "실존 인물", @"trump|biden|obama|elon musk|taylor swift|putin|xi jinping|kim jong[- ]un|bts|blackpink|" +
                                @"son heung[- ]min|lee jae[- ]myung|yoon suk[- ]yeol|iu|celebrity|famous actor|real person|photo of (?:a )?real",
            @"트럼프|바이든|오바마|일론 머스크|머스크|테일러 스위프트|푸틴|시진핑|김정은|방탄소년단|BTS|블랙핑크|손흥민|이재명|윤석열|아이유|연예인|실존 인물",
            "실존 인물(특히 유명인·정치인)의 모습은 막히거나 다르게 나올 수 있습니다."),
        new("중간", "상표·로고", @"nike|adidas|gucci|chanel|louis vuitton|prada|coca-?cola|pepsi|starbucks|mcdonald'?s|apple logo|samsung logo|" +
                                @"ferrari|lamborghini|porsche|bmw|mercedes|tesla|logo of",
            @"나이키|아디다스|구찌|샤넬|루이비통|프라다|코카콜라|펩시|스타벅스|맥도날드|페라리|람보르기니|포르쉐|벤츠|테슬라",
            "실제 상표·로고가 그대로 들어가면 막히거나 지워질 수 있습니다."),
        new("중간", "마약·자해", @"suicide|self-harm|self harm|slit wrists?|hanging (?:himself|herself)|overdose|cocaine|heroin|meth|" +
                                @"syringe|drug use|snorting|marijuana joint|smoking weed",
            @"자살|자해|손목 긋|목매|마약|코카인|헤로인|필로폰|주사기|대마",
            "자해·자살·마약 사용 장면은 막힙니다."),
        new("중간", "유혈·부상", @"blood|bloody|bleeding|bloodied|wounds?|wounded|injured|murder\w*|killing|stabb\w*|shot dead|dead (?:man|woman|person|body|child)",
            @"피투성이|피 흘리|유혈|상처|살인|죽은 사람|찔린",
            "피·상처·죽음이 눈에 띄게 나오면 막힐 수 있습니다. 3D 에셋이라면 깨끗한 상태로 만드는 편이 안전합니다."),
        new("중간", "폭력 행위", @"fighting|punching|kicking someone|beating|choking|strangl\w*|shooting at|aiming (?:a gun )?at (?:a |the )?(?:person|man|woman|child)|execution",
            @"때리는|폭행|목 조르|총 겨누|처형",
            "사람을 때리거나 겨누는 장면은 막힐 수 있습니다(3D 에셋은 자세를 T-포즈로 두면 이런 장면이 생기지 않습니다)."),
        new("중간", "폭발물", @"bombs?|explosives?|grenades?|ied|molotov",
            @"폭탄|폭발물|수류탄|화염병",
            "폭발물은 막힐 수 있습니다(게임 소품이라도)."),
        new("낮음", "노출 있는 옷", RevealingEnglish, @"비키니|속옷|란제리|수영복|시스루|섹시|야한|노브라",
            "보통 만들어지지만, 노출이 크거나 자세가 선정적이면 막힐 수 있습니다."),
        new("낮음", "무기", @"guns?|rifles?|pistols?|shotguns?|snipers?|knives|knife|daggers?|machine guns?|firearms?",
            @"소총|권총|저격총|기관총|총기|단검|(?<![가-힣])총(?=[을이은는도\s,]|$)|(?<![가-힣])칼(?=[을이은는도로\s,]|$)",
            "게임 소품으로는 보통 괜찮습니다. 사람을 겨누거나 다치게 하는 장면, 실제 총기 모델명은 막힐 수 있습니다."),
        new("낮음", "담배·술", @"cigarettes?|smoking|cigars?|vap(?:e|ing)|beer|wine|alcohol|drunk|whiskey|soju",
            @"담배|흡연|맥주|소주|와인|취한|만취|(?<![가-힣])술(?=[을이은는도\s,]|$)",
            "보통 괜찮지만, 미성년자와 함께 나오면 막힐 수 있습니다."),
    ];

    /// <summary>한국어 입력과 영어 프롬프트에서 막힐 만한 낱말을 찾는다. 같은 분류는 한 번만 적는다.</summary>
    public static List<PolicyHit> Check(string korean, string english)
    {
        string en = " " + english.ToLowerInvariant() + " ";
        string ko = korean;
        List<PolicyHit> hits = [];

        foreach (Rule rule in Rules)
        {
            Match me = Regex.Match(en, @"\b(?:" + rule.English + @")\b", RegexOptions.IgnoreCase);
            Match mk = Regex.Match(ko, rule.Korean, RegexOptions.IgnoreCase);
            if (!me.Success && !mk.Success) continue;

            string term = me.Success ? $"'{me.Value.Trim()}'" : "";
            if (mk.Success) term += (term.Length > 0 ? " " : "") + $"(입력: {mk.Value})";
            hits.Add(new PolicyHit(rule.Level, rule.Category, term, rule.Why));
        }

        // 미성년자로 보이는 말과 노출·성적인 말이 함께 있으면 가장 확실히 막힌다.
        Match minorEn = Regex.Match(en, @"\b(?:" + MinorEnglish + @")\b", RegexOptions.IgnoreCase);
        Match minorKo = Regex.Match(ko, MinorKorean);
        bool risky = hits.Exists(h => h.Category is "성적 노출·성행위" or "노출 있는 옷");
        if ((minorEn.Success || minorKo.Success) && risky)
        {
            string term = minorEn.Success ? $"'{minorEn.Value}'" : $"(입력: {minorKo.Value})";
            hits.Insert(0, new PolicyHit("높음", "미성년자 + 노출", term,
                "어려 보이는 인물(아이·학생·교복 등)과 노출·성적인 표현이 함께 있으면 반드시 막힙니다. 이런 조합은 만들 수 없습니다."));
        }

        return [.. hits.OrderBy(h => h.Level switch { "높음" => 0, "중간" => 1, _ => 2 })];
    }

    /// <summary>검사 결과를 안내문으로. 걸린 것이 없으면 그렇다고 적는다.</summary>
    public static string Format(List<PolicyHit> hits)
    {
        StringBuilder sb = new();
        sb.AppendLine("[막힐 수 있는 부분 검사]");
        if (hits.Count == 0)
        {
            sb.AppendLine("• 막힐 만한 낱말은 찾지 못했습니다.");
            sb.AppendLine("  그래도 VARCO에서 막혔다면 참조 이미지(올린 그림)나 낱말들의 조합이 원인일 수 있습니다.");
            return sb.ToString().TrimEnd();
        }
        foreach (PolicyHit h in hits)
            sb.AppendLine($"• {h.Level} · {h.Category}: {h.Term}\r\n  {h.Why}");
        sb.Append("※ VARCO가 공개한 금지 목록은 없어서, GPT Image·Nano Banana의 일반적인 기준으로 가늠한 것입니다.");
        return sb.ToString().TrimEnd();
    }
}
