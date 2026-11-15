# KoPrompt

한국어로 적은 그림 설명을 이미지 생성 AI(Stable Diffusion, NovelAI, Midjourney, Flux 등)에 넣을 **영어 프롬프트**로 바꿔 주는 Windows 프로그램입니다.
은어·비속어·줄임말·초성체가 섞인 말도 알아듣고, **인터넷 없이 PC 안에서만** 동작합니다(llama.cpp로 로컬 AI 모델 실행).

```
입력:  존예 누나가 한강에서 치맥 먹으면서 웃는 사진 뽑아줘 배경은 야경으로 하고 머리는 단발로 해줘
태그형: extremely beautiful young woman, laughing, enjoying fried chicken and beer by the han river at night,
        nighttime background view, bob cut hair
```

## 주요 기능

- **문장형 / 태그형** 출력 (Midjourney·Flux용 문장, Stable Diffusion·NovelAI용 쉼표 태그)
- **은어 사전**: 약 3,000개의 은어·비속어·신조어와 그림·사진 전문용어(구도, 렌즈, 조명, 화풍, 설정화 등)
- **기본 낱말 사전**: 흔한 낱말 약 600개의 영어를 AI에게 함께 알려 주어 작은 모델도 엉뚱한 낱말을 고르지 않게 함
- **뜻이 여럿인 말은 문맥으로**: "눈 오는 밤" → snow, "눈 감은 소녀" → eyes, "차 마시는" → tea, "차 타고" → car
- **입력에 없는 것은 덧붙이지 않음**: 꾸밈말·분위기·조명 같은 군더더기 태그를 걸러 냄
- **요청 말투 정리**: "그려줘", "배경은 ~로 해줘" 같은 말은 걷어 내고 그림 내용만 옮김
- 스타일 프리셋, 퀄리티 태그, 네거티브 프롬프트, 내 사전 편집, 입력 멈추면 자동 변환
- GPU(Vulkan) 사용 가능, 빠른 모드(작은 모델) 지원
- **KoPrompt 3D**: VARCO 3D 이미지 스튜디오용 3D 에셋 프롬프트 전용 창(아래 참고)

## KoPrompt 3D (VARCO 3D 전용)

[VARCO 3D](https://3d.varco.ai/) 이미지 스튜디오에 넣을 프롬프트를 만드는 별도 창입니다. VARCO 3D는 이미지를 먼저 만들고 그 이미지를 3D 모델로 바꾸는데, KoPrompt 3D는 **3D로 잘 바뀌는 이미지**가 나오게 프롬프트를 짭니다.

- `KoPrompt3D.exe`로 실행합니다(같은 프로그램을 이름만 바꾼 것이라, `KoPrompt.exe --3d`로 실행해도 됩니다). 설정은 일반 창과 따로 저장됩니다.
- 대상(캐릭터·생물·소품·탈것·배경 조각)을 알아서 가려 그에 맞는 틀을 씁니다. 캐릭터는 T-포즈나 A-포즈로 세워 3D로 바꾼 뒤 뼈대를 넣기 쉽게 합니다.
- 화풍(스타일라이즈드 게임, 실사 PBR, 애니메이션풍, 카툰, 로우폴리, 클레이), 시점, 배경색을 고를 수 있습니다.

```
입력:  검은 후드티 입은 닌자 소녀가 숲속에서 칼 들고 있음
출력:  Full body stylized 3D game character: a black hoodie-clad ninja girl with a blade. Standing in a strict symmetrical
       T-pose, arms straight out horizontally, legs slightly apart, front view, entire body from head to feet visible and
       centered, vertical framing, one character only, plain pure white background, soft even lighting, no cast shadow,
       no text, no logo
안내:  비율 3:4 추천 · '숲속에서'는 흰 배경과 부딪혀서 뺐음 · '칼 들고' → with a blade
```

이 출력을 VARCO 3D 이미지 스튜디오(GPT Image 2.5, 3:4, 1장)에 그대로 넣어 보면, 머리부터 발끝까지 흰 배경 가운데에 T-포즈로 선 이미지가 나왔습니다. 나무 보물상자(1:1)와 빨간 스포츠카(16:9)도 물체 전체가 3/4 시점으로 흰 배경에 나왔습니다.

### VARCO 화면에서 직접 고를 것

KoPrompt 3D는 VARCO에 직접 접속하지 않습니다. 아래는 VARCO 화면에서 고르고, 무엇을 고르면 좋은지는 프로그램이 알려 줍니다.

| VARCO 설정 | KoPrompt 3D가 하는 일 |
|---|---|
| 비율(1:1, 4:3, 16:9, 3:4, 9:16) | 대상 모양에 맞는 비율을 추천(서 있는 캐릭터 3:4, 탈것·긴 무기 16:9, 가로등처럼 세로로 긴 것 3:4, 배경 조각 4:3, 작은 소품 1:1). 직접 고른 비율도 반영 |
| 모델(GPT Image, Nano Banana) | 둘 다 문장형 프롬프트를 그대로 받으므로 따로 바꾸지 않음(GPT Image 2.5로 시험) |
| 장수(1~4장) | 1장으로 먼저 확인하라고 안내(GPT Image 2.5는 1장에 50포인트였음) |
| 3D 프롬프트 스위치 | 3D용 조건이 이미 프롬프트에 들어 있어 켜든 끄든 상관없음. 켜도 VARCO가 화면의 프롬프트를 바꾸지는 않았음 |

### VARCO에 칸이 없어서 프롬프트로 대신 넣는 것 (우회)

| 하고 싶은 것 | VARCO 화면 | KoPrompt 3D가 대신 하는 방법 |
|---|---|---|
| 네거티브 프롬프트(그림자·글자·로고 빼기) | 칸이 없음 | `no cast shadow, no text, no logo`를 프롬프트 끝에 넣음 |
| 비율에 맞는 구도 | 비율만 고름 | `vertical framing` / `horizontal framing`을 넣어 대상이 그 비율 안에 꽉 차게 함 |
| 3D로 바꾸기 좋은 이미지 | 3D 프롬프트 스위치뿐 | 대상 하나, 전체가 보이게 가운데, 흰(또는 연회색) 배경, 고른 조명, 그림자 없음을 문장으로 넣음 |
| 캐릭터 자세 | 없음 | T-포즈 / A-포즈 문장을 넣고, 자세와 부딪히는 동작은 바꾸거나 뺌(`shooting a bow` → `with a bow`, `smoking`은 뺌) |
| 배경 없는 단독 에셋 | 없음 | `in a forest`, `on a rooftop` 같은 장소·때 구절을 빼고, 뺀 것을 안내에 적음 |
| 시점 | 없음 | 정면 / 3/4 앞 / 옆을 문장으로 넣음(캐릭터는 정면, 물체는 3/4 앞이 기본) |

### 막힘 검사 (왜 막혔는지 확인)

VARCO는 막을 때 "이미지 생성이 콘텐츠 정책에 걸려 중단되었습니다"라고만 하고 이유를 알려 주지 않습니다(막힌 요청에는 포인트가 빠지지 않았습니다). KoPrompt 3D는 변환할 때마다 프롬프트에서 걸릴 만한 낱말을 찾아 안내 칸 맨 위에 적고, **막힘 검사** 버튼을 누르면 입력칸의 글(한국어든, VARCO에서 막힌 영어 프롬프트를 붙여 넣은 것이든)을 AI 없이 바로 검사합니다.

| 등급 | 분류 | 예 |
|---|---|---|
| 높음 (거의 확실히 막힘) | 성적 노출·성행위, 미성년자로 보이는 인물 + 노출, 잔혹·고어, 혐오 상징 | 알몸, 교복 + 섹시, 시체·고문, 나치 |
| 중간 (막힐 수 있음) | 저작권 캐릭터·작품, 실존 인물, 상표·로고, 마약·자해, 유혈·부상, 폭력 행위, 폭발물 | 피카츄, 트럼프, 나이키, 피투성이 |
| 낮음 (보통 괜찮지만 장면에 따라) | 노출 있는 옷, 무기, 담배·술 | 비키니, 총·칼, 맥주 |

- VARCO가 공개한 금지 목록은 없어서, VARCO 그림을 만드는 GPT Image·Nano Banana의 일반적인 제한을 기준으로 가늠합니다. 피카츄 프롬프트는 실제로 VARCO에서 막히는 것을 확인했습니다.
- 이 검사는 **원인을 알려 주기만** 합니다. 막히는 내용을 돌려 말해 통과시키는 기능은 없습니다(VARCO 약관은 서비스의 보호 장치를 우회하는 행위를 금지합니다). 저작권 캐릭터라면 새 캐릭터로 설명하는 식으로 내용을 바꾸면 됩니다.
- 점검용: `KoPrompt.exe --check 입력.txt 출력.txt` (줄마다 검사 결과를 적음)

## 자유롭게 가져다 쓰세요

필요하면 누구든 자유롭게 가져다 쓰셔도 됩니다. 개인·상업 용도 상관없이 쓰고, 고치고, 다른 프로그램에 넣고, 다시 배포해도 됩니다([MIT 라이선스](LICENSE)). 출처만 남겨 주시면 됩니다.
단, AI 모델 파일은 이 저장소에 들어 있지 않고 모델마다 라이선스가 따로 있습니다(아래 [모델](#모델) 참고).

## 바로 쓰기 (빌드 없이)

1. [Releases](https://github.com/alslxhrl7-web/KoPrompt/releases)에서 `KoPrompt-v1.1.0-win-x64.zip`을 받아 원하는 곳에 압축을 풉니다.
2. 압축을 푼 폴더 안의 `models` 폴더에 GGUF 모델 파일을 넣습니다(아래 [모델](#모델) 참고).
3. `KoPrompt.exe`(일반) 또는 `KoPrompt3D.exe`(VARCO 3D용)를 실행합니다. .NET을 따로 설치할 필요는 없습니다.

처음 실행하면 "AI가 은어 지침을 익히는 중…"으로 20~40초쯤 걸리고, 다음부터는 바로 뜹니다.

## 준비물 (직접 빌드할 때)

- Windows 10/11 64비트
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- GGUF 형식의 AI 모델 파일 (아래 참고)

## 빌드

```bash
dotnet publish -c Release
```

`bin/Release/net10.0-windows/win-x64/publish/` 에 `KoPrompt.exe`(단일 실행 파일)와 `runtimes` 폴더(llama.cpp DLL)가 생깁니다.

## 설치

프로그램이 쓰는 파일은 모두 사용자 폴더의 `KoPrompt` 폴더(`C:\Users\<이름>\KoPrompt`)에 둡니다.

```
C:\Users\<이름>\KoPrompt\
├─ models\      ← .gguf 모델 파일을 여기에 넣는다
├─ runtimes\    ← publish 폴더의 runtimes 폴더를 그대로 복사
├─ 은어사전.txt   ← 내 사전 (처음 실행하면 생김, 메모장으로 고칠 수 있음)
└─ settings.json
```

- `KoPrompt.exe` 옆에 `models`, `runtimes` 폴더가 있으면 그쪽을 먼저 씁니다(폴더째 들고 다닐 때).
- `models` 폴더에 모델이 둘 이상 있으면 **가장 큰 모델을 기본**으로, **빠른 모드에서는 가장 작은 모델**을 씁니다.

### 모델

한국어를 잘하는 Instruct 모델의 GGUF 파일이면 됩니다. 개발할 때 쓴 모델:

| 용도 | 모델 |
|---|---|
| 기본(정확) | EXAONE-3.5-7.8B-Instruct Q4_K_M (약 4.8GB) |
| 빠른 모드 | EXAONE-3.5-2.4B-Instruct Q4_K_M (약 1.6GB) |

Hugging Face의 `LGAI-EXAONE` 저장소에서 GGUF 파일을 받을 수 있습니다. 모델마다 라이선스가 다르니(EXAONE은 비상업용) 쓰기 전에 확인하세요.

## 사전 고치기

- 프로그램 아래쪽 **사전에 단어 추가** 칸이나 **내 사전 편집** 버튼으로 `은어사전.txt`를 고칩니다. 형식은 `한국어 = 영어 뜻`.
- 내 사전의 뜻이 기본 사전과 AI의 해석보다 우선합니다.
- 뜻 앞에 `!`를 붙이면 AI가 돌려 말해도 그 영어를 프롬프트에 반드시 넣습니다.
- 기본 은어 사전은 `slang.txt`, 기본 낱말 사전은 `basic.txt`이며 빌드할 때 exe 안에 들어갑니다.

## 점검용 명령

```bash
KoPrompt.exe --cli 입력.txt 출력.txt [tags] [3d] [fast] [noexplain]
```

창 없이 `입력.txt`의 줄마다 변환해서 `출력.txt`에 적습니다(`tags`: 태그형, `3d`: VARCO 3D용, `fast`: 빠른 모드, `noexplain`: 풀이 생략).

## 알려진 한계

- 빠른 모드(작은 모델)는 긴 문장에서 일부 내용을 빠뜨리기도 합니다.
- 큰 모델도 가끔 입력에 없는 말을 조금 덧붙일 수 있습니다(자세·표정·장소·때처럼 흔한 군말은 걸러 냅니다). 같은 입력이면 늘 같은 결과가 나옵니다.
- 처음 실행하거나 지침이 바뀐 뒤 첫 변환은 "AI가 은어 지침을 익히는 중…"으로 20~40초쯤 걸립니다(이후에는 저장해 둔 기억을 씁니다).
