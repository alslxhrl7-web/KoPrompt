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

## 준비물

- Windows 10/11 64비트
- 빌드하려면 [.NET 10 SDK](https://dotnet.microsoft.com/download)
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
KoPrompt.exe --cli 입력.txt 출력.txt [tags] [fast] [noexplain]
```

창 없이 `입력.txt`의 줄마다 변환해서 `출력.txt`에 적습니다(`tags`: 태그형, `fast`: 빠른 모드, `noexplain`: 풀이 생략).

## 알려진 한계

- 빠른 모드(작은 모델)는 긴 문장에서 일부 내용을 빠뜨리기도 합니다.
- 같은 입력도 실행할 때마다 표현이 조금씩 다를 수 있습니다.
- 처음 실행하거나 지침이 바뀐 뒤 첫 변환은 "AI가 은어 지침을 익히는 중…"으로 20~40초쯤 걸립니다(이후에는 저장해 둔 기억을 씁니다).
