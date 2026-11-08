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
$destination = '<발급받은 소유 작업 경로>/release-mwb-v1.2.0-rc.18'
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

현재 배포 후보는 **1.2.0-rc.17**입니다. 모니터별 자유 배치·직접 녹화하는 전환 키·파일 보내기·전송 재개와 자동 클립보드 파일의 300MB 제한 옵션을 포함합니다. 보내는 PC와 받는 PC 모두 같은 버전을 설치하세요. 명시 송신은 상대의 설정된 수신 폴더를 사용하며, 이전 버전과의 혼용을 같은 동작으로 보장하지 않습니다.

RC15는 입력 훅에서 네트워크 전송·암호화·연결 목록 잠금을 기다리는 경로를 분리합니다. 연결별 제한된 송신 큐에서 연속 순수 절대좌표 MOVE의 꼬리만 최신 위치로 합치며 키·클릭·휠·상대 이동의 순서를 유지합니다. 일반 전환과 긴급 복귀의 키·버튼 해제도 같은 FIFO에 넣어 재진입 입력보다 먼저 처리합니다. 실제 실패한 연결은 훅 밖에서 정리하고 기존 재연결 절차를 요청합니다. 키 해제의 고정 대기를 제거했으며, CRD0 표식의 Chrome 원격 데스크톱 입력은 물리 전환 키 상태와 분리합니다. ‘Windows 커서 사용’은 앱의 대체 커서만 끄고 Windows 마우스 키를 켜지 않습니다.

RC17은 현재 앱보다 높은 권한의 전경 창 또는 권한을 확인할 수 없는 전경에서 기존 SYSTEM 입력 서비스를 먼저 선택합니다. 일반 전경의 순수 MOVE는 기존 직접 입력 경로를 유지하며, 정상적으로 확인한 프로세스 권한은 재사용합니다. 권한 조회 실패만 1초 간격으로 다시 확인합니다. 기존 키·버튼 해제 책임, 입력 서비스의 SYSTEM 소유자·실행 경로·세션 검증과 Windows 정책은 유지합니다.

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

RC17의 최종 시험 결과와 배포 파일은 [해당 릴리스](https://github.com/ttaem00/CVA-VEIL-Mouse_Without_Border_Plus/releases/tag/mwb-v1.2.0-rc.17)를 확인하세요. 이전 후보의 시험 수치를 RC17의 검증으로 세지 않습니다.

RC17 입력 코드의 로컬 전체 시험은 555개 중 551개 통과·4개 제외·실패 0개입니다. 보호 전경에서 입력 서비스를 사용할 수 없을 때 native 입력 성공으로 대신 처리하지 않는 회귀 시험을 포함합니다. 반복 전경 분류 10,000회 측정의 평균 0.593마이크로초는 분류 비용만 나타내며 서비스·네트워크·종단 입력 지연을 포함하지 않습니다. GAME에 같은 입력 코드의 RC17 후보를 설치한 뒤 파일 해시·입력 서비스 자동 실행·기존 설정 8개 보존을 확인했습니다. 작업 관리자·설치 프로그램·게임 전환 후 실제 입력 복구와 체감 지연, 냉부팅 로그인 화면의 수락은 아직 확인 대기입니다.

ChatGPT 실행 중 나타나는 지연의 원인과 지속적인 해소는 아직 확인하지 않았습니다. 실제 세 PC의 동일 버전 적용·지속 사용·체감 지연 해소도 확인하지 않았습니다. 소스 수정과 로컬 시험을 기존 설치본의 적용 완료로 간주하지 않습니다. Windows 화면 합성 시험에서 전경 확보가 불가능하면 해당 검사는 `Inconclusive`이며 PASS로 세지 않습니다.

설치 EXE는 별도 설치가 필요 없도록 .NET 10·Windows Desktop 런타임을 포함합니다. RC17의 정확한 배포 크기는 릴리스를 확인하세요. 파일 크기로 실행 중 지연의 원인을 단정하지 않습니다.

현재 EXE는 서명되지 않았으므로 Windows 승인 창에 게시자가 ‘알 수 없음’으로 표시될 수 있습니다. 실제 확인한 사용 환경은 Windows 11 x64이며, UAC 마우스 클릭은 별도 확인이 필요합니다. 이 후보를 모든 요구사항의 최종 수락이나 안정 버전 보증으로 표시하지 않습니다.

## Store용 무인 설치와 제거

등록용 설치 인수는 `--install --silent`입니다. 첫 실행 창과 설치 후 자동 실행을 생략하고, 기존 전체 설치 트랜잭션으로 서비스·현재 사용자 설정·바로가기·앱 등록을 처리합니다. Windows 관리자 승인은 생략하지 않습니다. 성공은 `0`, UAC 취소는 `1223`, 다른 실패는 `1`입니다.

대상은 **호출한 대화형 Windows 사용자**의 SID·HKCU·로컬 앱 데이터입니다. SYSTEM·서비스 계정·Session 0에서는 실패합니다. 이미 다른 관리자 계정으로 실행된 프로세스에서 원 사용자를 추측하지 않습니다. 실제 Store 실행 문맥이 이 계약에 맞는지 패키지 유효성 검사와 깨끗한 PC에서 확인하기 전에는 제출용 설치 검증 통과로 표시하지 않습니다.

Windows 앱 등록의 `QuietUninstallString`은 사용자 확인 창 없이 제거하고 현재 사용자 설정을 보존합니다. 실제 실행 파일과 설치 당시 사용자 SID의 앱 등록이 사라진 후 `0`을 반환하며, 30초 이내에 끝나지 않으면 `1460`을 반환합니다. 기존 `UninstallString`과 설정 창에서의 제거는 사용자 확인을 유지합니다.

설치된 EXE에 직접 `--uninstall --silent`를 넘기면 자체 파일을 삭제할 기존 정리 소유자에게 위임한 뒤 `997`(진행 중)을 반환합니다. 이를 완료 성공 코드로 취급하지 마세요. 완료를 기다려야 하는 배포 도구는 등록된 `QuietUninstallString`을 사용합니다. 공유·전송 기록·로그·받은 파일까지 모두 삭제하는 기능은 아닙니다.

## 수동 서명 workflow

`.github/workflows/store-signing.yml`은 관리자가 승인한 **서명 전용 예외**입니다. 평소 PR 검증은 아래의 로컬 merge-ref gate를 계속 사용합니다. workflow는 검토된 `main`에서 수동으로만 시작하며 공개 소스를 빌드·시험하고 SignPath의 사람 승인을 요청합니다. 서명과 타임스탬프를 검증한 최종 EXE에서만 SHA256을 생성하고 산출물을 보관합니다. 공개 릴리스 게시나 Store 제출은 자동으로 하지 않습니다.

SignPath 지원 선정과 정본 저장소의 trusted-build 연결이 먼저 필요합니다. 공급자가 발급한 organization/project/signing-policy 식별자는 해당 repository variables에, API token은 사용자가 GitHub의 해당 repository secret 입력 화면에 직접 설정합니다. 로컬 `.env`나 채팅으로 토큰을 전달하지 않습니다. 이 소스에 secret 참조가 있다는 이유로 계정·연결·서명이 활성화된 것은 아닙니다.

기존 RC17 공개 파일과 URL은 교체하지 않습니다. 서명된 RC18 이후 새 버전의 불변 HTTPS URL을 실제 검증한 후 Store에 등록합니다. [개인정보처리방침](../docs/privacy.html)과 [Code signing policy](../docs/code-signing-policy.html)는 현재 앱의 처리와 미서명 상태를 설명합니다.

## PR merge-ref 로컬 검증

소유 이슈 브랜치를 commit·push하고 같은 브랜치의 PR이 열려 있으면 다음 검사를 실행합니다. 위의 process-local SDK 환경을 그대로 사용합니다.

```powershell
pwsh.exe -NoProfile -File ./scripts/Test-PrValidationGate.ps1 -Root $PWD -MaxTotalSeconds 600 -ChildTimeoutSeconds 120
```

검사는 현재 PR의 head·main과 정확한 merge ref 부모를 확인하고, 현재 HEAD tree가 merge tree와 같을 때만 이 checkout에서 기존 전체 시험과 단일 설치 파일 생성을 실행합니다. 차이가 있으면 정상 additive merge·push 뒤 다시 검증합니다. 종료 후 source·PR head/base도 다시 확인합니다. 이 스크립트의 Git 쓰기는 해당 PR merge ref를 FETCH_HEAD로 받는 fetch뿐입니다. branch·index·worktree를 바꾸지 않으며 설치 EXE나 실제 앱을 실행하지 않습니다. 출력은 ignored `artifacts/pr-validation` 아래에 남습니다. 통과는 공개 배포나 설치된 PC의 적용을 뜻하지 않습니다.
