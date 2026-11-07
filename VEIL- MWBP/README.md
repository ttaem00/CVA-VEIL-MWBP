# CVA-VEIL-Mouse_Without_Border_Plus

**CVA → VEIL → MWBP**: CVA 모브랜드 아래 VEIL 라이브 도구 제품군에 속하며, 앱 표시 이름은 **MWBP**입니다. VEIL 공통 V의 오른쪽 아래 연결 고리로 MWBP를 구분합니다. 벡터 도면은 CVA 브랜드 정본의 `build_veil_identity.py`이며, 사본은 `docs/assets/veil-mwbp-{color,micro}.svg`입니다. `App/Veil.png`는 512px 파생본이고 `App/Veil.ico`는 같은 도면의 16·20·24·32·40·48·64·128·256px 출력입니다. 16·20px에는 획을 보강한 micro를 씁니다. 앱·설치 EXE·사이트는 이 출력의 사본을 소비하며, 이미지를 크롭하거나 제품별로 V를 다시 그리지 않습니다. 원본 Microsoft 그림을 재색칠하지 않습니다.

Windows PC 사이에서 마우스·키보드·클립보드·파일을 공유하는 Mouse Without Borders Plus의 배포 프로젝트입니다.

사용자에게 전달할 파일은 **`VEIL-MWBP-Setup.exe` 하나**입니다. 실행 → **MWBP 설치** → Windows 승인으로 설치합니다. PowerToys, .NET 런타임, ZIP 해제나 명령 입력이 필요하지 않습니다. Windows 권한 승인은 생략하지 않습니다.

기존 연결·모니터 배치·단축키·전송 기록은 앱의 기존 설치 경로에서 보존합니다. 실행 파일·서비스·설정의 내부 이름을 바꿔 별도 설치본을 만드는 방식으로 리브랜딩하지 않습니다.

## 소스 정본과 작업 위치

소스 정본은 독립 저장소 [ttaem00/CVA-VEIL-Mouse_Without_Border_Plus](https://github.com/ttaem00/CVA-VEIL-Mouse_Without_Border_Plus)입니다. 기존 원본 폴더와 그 안의 ignored 빌드·검증 자료는 보존하고, 개발은 관리 도구가 발급한 깨끗한 이슈 checkout에서 진행합니다. 아래 명령은 그 checkout 루트에서 실행합니다.

배포 후보와 검증 자료는 소유가 확인된 별도 작업 경로에 보존합니다. `-Destination`에는 비어 있는 새 출력 경로를 넘깁니다. 설치본·사용자 설정 경로를 빌드 출력으로 사용하지 않습니다. 로컬 후보 생성, 공개 릴리스, 실제 PC 설치는 각각 확인합니다.

RC8부터 바탕화면과 시작 메뉴에 **VEIL MWBP** 실행 바로가기를 제공합니다. 시작 메뉴에서 같은 이름으로 검색할 수 있습니다. 새 설치는 Windows 로그인 시 자동 시작이 기본으로 켜지고, 업데이트는 기존 자동 시작 선택을 유지합니다.

## 재빌드

개발 컴퓨터에 Windows, PowerShell 7, .NET SDK 10.0.403이 필요합니다. `global.json`의 고정 SDK로 빌드하며 설치 EXE에는 .NET 10.0.14 보안 패치를 포함합니다. 사용하는 PC에는 SDK가 필요하지 않습니다.

```powershell
$destination = '<발급받은 소유 작업 경로>/release-mwb-v1.2.0-rc.12'
pwsh -NoProfile -File ./build.ps1 -RunTests
pwsh -NoProfile -File './VEIL- MWBP/Build-Release.ps1' -Destination $destination
```

출력 폴더는 비어 있어야 합니다. 설치 EXE와 SHA256, 라이선스·안내가 생성됩니다. 별도 MSI나 설치 스크립트를 사용자에게 실행시키지 않습니다. 설치 EXE와 앱은 동일 바이너리이며, 설치 후에는 보호된 기존 경로의 앱을 일반 권한으로 실행합니다.

고정 SDK가 없으면 [Microsoft 공식 메타데이터](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json)의 **10.0.403 / win-x64 ZIP**을 소유 작업 경로에 받고, 메타데이터의 SHA512와 대조한 뒤 같은 소유 경로에 풉니다. 선택적으로 아래 process 환경을 먼저 설정하면 전역 SDK나 PATH를 교체하지 않고 사용할 수 있습니다. `<...>`는 실제 발급 경로로 바꾸며, `$ownedRoot` 아래 `tmp`는 관리 도구가 발급한 임시 경로입니다.

```powershell
$sdkRoot = '<공식 ZIP을 푼 SDK 10.0.403 경로>'
$ownedRoot = '<발급받은 소유 작업 경로>'
$env:DOTNET_ROOT = $sdkRoot
$env:DOTNET_ROOT_X64 = $sdkRoot
$env:PATH = "$sdkRoot;$env:PATH"
$env:DOTNET_CLI_HOME = Join-Path $ownedRoot 'tmp/dotnet-cli-home'
$env:NUGET_PACKAGES = Join-Path $ownedRoot 'tmp/nuget-packages'
$env:TEMP = Join-Path $ownedRoot 'tmp'
$env:TMP = $env:TEMP
$env:TMPDIR = $env:TEMP
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
dotnet --version # 10.0.403 확인 후 위 빌드·패키지 명령 실행
```

## 배포 범위

현재 배포 후보는 **1.2.0-rc.12**입니다. 모니터별 자유 배치·직접 녹화하는 전환 키·파일 보내기·전송 재개와 자동 클립보드 파일의 300MB 제한 옵션을 포함합니다. 보내는 PC와 받는 PC 모두 같은 버전을 설치하세요. 명시 송신은 상대의 설정된 수신 폴더를 사용하며, 이전 버전과의 혼용을 같은 동작으로 보장하지 않습니다.

RC9는 같은 연결·목적지에서 이미 수신되어 쌓인 연속된 순수 절대좌표 마우스 이동만 마지막 좌표로 줄입니다. 키·버튼·휠·상대 이동·제어 메시지의 순서와 기존 SendInput·UAC·자체 주입 구분은 유지합니다. 새 타이머·worker·입력 큐를 추가하지 않으며 복구 버튼과 무관하게 항상 적용합니다. 원본의 비동기 WinRT MOVE 경로와 소스를 비교했지만 이번 변경으로 현재 입력 API를 교체하지 않습니다.

앞선 RC7의 `Awake` 수신 중 임의 커서 이동·최소 100ms 고정 대기 제거와 기존 0 이동 입력을 통한 유휴 상태 갱신을 유지합니다. 일반 앱과 입력 TCP의 client/server worker 우선순위는 `AboveNormal`입니다. 이 worker는 설정·구버전 클립보드도 처리하므로 마우스 이동 전용 스레드가 아니며, `High`·`Realtime`이나 새 설정을 도입하지 않습니다.

앞선 RC6의 일반 마우스 이동과 클립보드·파일 작업 사이 대기 경로 분리도 유지합니다.

- 키·버튼 상태를 바꾸지 않는 순수 MOVE는 일반 데스크톱에서 기존 native 입력 경로를 먼저 사용합니다. 보호 입력이 필요한 경우의 경로와 키·버튼의 소유권은 유지합니다.
- 텍스트·이미지 본문은 기존 클립보드 전용 전송 경로를 사용하며 입력 TCP에 싣지 않습니다. 같은 실제 클립보드 내용은 캐시로 중복 전송을 줄입니다.
- 파일 전송 잠금과 텍스트·이미지 전송을 분리합니다. 이전 PC의 커서 숨김·클립보드 알림도 비동기로 보내며, 대기 중 연결 대상이 바뀌면 오래된 연결로 보내지 않도록 확인합니다.

앞선 후보의 설정 저장·대상별 송신·파일 취소 잠금 분리와 클립보드 이벤트의 강제 전체 GC 제거도 유지합니다. 이는 코드상 대기 경로의 변경이며 실제 체감 지연 해소를 보장하는 수치는 아닙니다.

클릭할 수 없을 때는 장치가 직접 연결된 PC의 물리 키보드에서 **Ctrl+Alt+Backspace**를 눌러 그 PC로 긴급 복귀할 수 있습니다. 단축키를 꺼도 사용할 수 있고 재연결을 기다리지 않으며, 이전 PC에 전달한 키·버튼은 수신자별로 별도 해제합니다. Windows·장치가 입력 자체를 전달하지 않는 장애의 복구와 실제 게임에서의 장시간 재발 해소는 아직 확인하지 않았습니다.

**PC 연결**과 **옵션**의 **마우스 입력 복구**는 클릭할 수 있는 상태에서 이 PC의 재연결·입력 훅 등록을 요청하는 기능입니다. 공유가 잠시 끊기며 연결·배치·단축키·커서·파일 공유 설정은 유지합니다. **전체 화면 앱에서 화면 전환 차단**은 별도 공유 설정이며, 끄면 전체 화면에서도 전환 키로 이동할 수 있습니다. Windows 마우스 키·숫자패드·서비스 권한은 변경하지 않습니다. 복구를 눌러야 위 변경이 활성화되는 것은 아닙니다.

소스 ZIP에는 빌드에 필요한 공개 코드·시험·라이선스만 포함하고 `.git`, 작업 기록, 실제 사용자 설정, 보안 키, 전송 기록, 개인 PC 업데이트 스크립트는 포함하지 않습니다.

공개 소스와 설치 EXE의 배포 대상은 [ttaem00/CVA-VEIL-Mouse_Without_Border_Plus](https://github.com/ttaem00/CVA-VEIL-Mouse_Without_Border_Plus)입니다. 공개 코드 서명 인증서는 이 프로젝트에 포함하지 않습니다.

## 검증 상태

RC12의 최종 시험 결과와 배포 파일은 [해당 릴리스](https://github.com/ttaem00/CVA-VEIL-Mouse_Without_Border_Plus/releases/tag/mwb-v1.2.0-rc.12)를 확인하세요. 이전 후보의 시험 수치를 RC12의 검증으로 세지 않습니다.

ChatGPT 실행 중 나타나는 지연의 원인과 지속적인 해소는 아직 확인하지 않았습니다. 실제 세 PC의 동일 버전 적용·지속 사용·체감 지연 해소도 확인하지 않았습니다. 소스 수정과 로컬 시험을 기존 설치본의 적용 완료로 간주하지 않습니다. Windows 화면 합성 시험에서 전경 확보가 불가능하면 해당 검사는 `Inconclusive`이며 PASS로 세지 않습니다.

실제 RC7 설치 EXE는 약 85MB이며, 별도 설치가 필요 없도록 .NET 10·Windows Desktop 런타임을 포함합니다. 압축된 제품 자체는 약 1.7MB입니다. RC12의 정확한 배포 크기는 릴리스를 확인하세요. 파일 크기로 실행 중 지연의 원인을 단정하지 않습니다.

현재 EXE는 서명되지 않았으므로 Windows 승인 창에 게시자가 ‘알 수 없음’으로 표시될 수 있습니다. 실제 확인한 사용 환경은 Windows 11 x64이며, UAC 마우스 클릭은 별도 확인이 필요합니다. 이 후보를 모든 요구사항의 최종 수락이나 안정 버전 보증으로 표시하지 않습니다.

## PR merge-ref 로컬 검증

소유 이슈 브랜치를 commit·push하고 같은 브랜치의 PR이 열려 있으면 다음 검사를 실행합니다. 위의 process-local SDK 환경을 그대로 사용합니다.

```powershell
pwsh.exe -NoProfile -File ./scripts/Test-PrValidationGate.ps1 -Root $PWD -MaxTotalSeconds 600 -ChildTimeoutSeconds 120
```

검사는 현재 PR의 head·main과 정확한 merge ref 부모를 확인하고, 현재 HEAD tree가 merge tree와 같을 때만 이 checkout에서 기존 전체 시험과 단일 설치 파일 생성을 실행합니다. 차이가 있으면 정상 additive merge·push 뒤 다시 검증합니다. 종료 후 source·PR head/base도 다시 확인합니다. 이 스크립트의 Git 쓰기는 해당 PR merge ref를 FETCH_HEAD로 받는 fetch뿐입니다. branch·index·worktree를 바꾸지 않으며 설치 EXE나 실제 앱을 실행하지 않습니다. 출력은 ignored `artifacts/pr-validation` 아래에 남습니다. 통과는 공개 배포나 설치된 PC의 적용을 뜻하지 않습니다.
