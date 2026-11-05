<p align="center"><img src="docs/assets/veil.png" width="112" alt="VEIL MWBP"></p>

<h1 align="center">VEIL · MWBP</h1>
<p align="center"><strong>마우스 하나로, 여러 PC를 하나처럼.</strong></p>
<p align="center">Windows PC 사이에서 마우스·키보드·클립보드·파일을 공유하는 독립형 앱</p>

<p align="center">
  <a href="https://github.com/ttaem00/CVA-VEIL-Mouse_Without_Border_Plus/releases/tag/mwb-v1.2.0-rc.6"><img src="https://img.shields.io/badge/version-1.2.0--rc.6-172231?style=flat-square" alt="1.2.0-rc.6 배포 후보"></a>
  <img src="https://img.shields.io/badge/Windows-x64-172231?style=flat-square" alt="Windows x64">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-8b6b35?style=flat-square" alt="MIT 라이선스"></a>
</p>

<p align="center">
  <a href="https://github.com/ttaem00/CVA-VEIL-Mouse_Without_Border_Plus/releases/download/mwb-v1.2.0-rc.6/VEIL-MWBP-Setup.exe"><strong>Windows x64 설치 다운로드 →</strong></a><br>
  <a href="https://ttaem00.github.io/CVA-VEIL-Mouse_Without_Border_Plus/">제품 사이트</a> · <a href="#시작하기">시작하기</a> · <a href="#할-수-있는-일">기능</a> · <a href="#알아둘-점">지원 범위</a> · <a href="https://ttaem.com">ttaem.com</a>
</p>

**PowerToys나 .NET을 따로 설치할 필요가 없습니다.** `VEIL-MWBP-Setup.exe` 하나로 설치하며, 기존 연결·모니터 배치·단축키·전송 기록을 유지합니다. 앱에서는 **Mouse Without Borders Plus / MWBP**로 표시합니다.

> **서명되지 않은 배포 후보(RC)입니다.** Windows 승인 창에 게시자가 ‘알 수 없음’으로 표시될 수 있습니다. [해당 버전의 릴리스](https://github.com/ttaem00/CVA-VEIL-Mouse_Without_Border_Plus/releases/tag/mwb-v1.2.0-rc.6)에서 파일 출처와 SHA256을 확인하세요. 실제 확인한 환경은 Windows 11 x64이며, 모든 PC 구성의 동작이나 지연 해소를 보장하지 않습니다.

## 시작하기

1. **각 PC에 같은 버전을 설치하세요.** 설치 EXE 실행 → **MWBP 설치** → Windows 승인으로 진행합니다.
2. **PC 찾기에서 연결하세요.** 양쪽 화면의 확인 코드가 같으면 양쪽에서 승인합니다. 목록에 없으면 상대 PC의 이름이나 IPv4 주소를 입력합니다.
3. **모니터를 끌어 배치하고 적용하세요.** 화면별로 실제 위치에 맞춰 가장자리를 붙입니다. 표시된 접경을 통해 다른 화면으로 이동합니다.

시작 메뉴의 **Mouse Without Borders Plus**에서 앱을 엽니다. 설치 방식이나 별도 런타임을 선택할 필요가 없습니다.

## 할 수 있는 일

| 기능 | 사용하는 방법 |
| --- | --- |
| **모니터별 자유 배치** | PC 안의 각 모니터를 따로 배치합니다. 스냅과 이동 가능한 접경 표시로 이웃 화면을 확인합니다. |
| **직접 지정하는 키** | 전환 조건과 PC 전환 단축키를 녹화합니다. Caps Lock과 여러 키 조합도 지정할 수 있습니다. |
| **파일 보내기** | **파일 보내기…**에서 받는 PC와 파일을 고릅니다. 상대 PC에 설정된 수신 폴더로 전송합니다. |
| **전송 중단·재개** | 전송 센터에서 진행 상태를 확인하고 중단된 파일 전송을 재개합니다. |
| **클립보드 공유** | 복사한 텍스트·이미지·파일을 연결된 PC에서 사용합니다. 파일 전송 허용은 별도 설정입니다. |

기본으로 켜진 **자동 전송 300MB 제한**은 클립보드로 복사한 단일 파일에 적용합니다. 정확히 300MB는 허용하며, 큰 파일은 **파일 보내기**나 드래그로 전송할 수 있습니다. 명시 송신의 수신 폴더 동작을 함께 사용하려면 보내는 PC와 받는 PC 모두 같은 버전을 설치하세요.

## 알아둘 점

- **관리자 앱·Windows 승인 화면:** 설치 시 Windows 승인을 받아 SYSTEM 입력 서비스를 설치합니다. 연결된 PC에 관리자 화면의 입력 능력을 위임하며, 이 Windows 계정의 다른 앱이 권한을 악용할 수 있습니다. Windows 정책으로 서비스가 금지된 경우 일반 입력·연결·파일 공유는 별도로 사용할 수 있습니다.
- **입력 복구:** 옵션의 **마우스 입력 복구**는 이 PC의 대체 커서를 끄고 입력 훅·연결을 다시 시작하도록 요청합니다. 공유가 잠시 끊기며 기존 설정은 유지합니다. Windows 마우스 키·숫자패드 설정은 바꾸지 않습니다. 특정 지연의 해결을 보증하는 버튼은 아닙니다.
- **Windows 커서 사용:** Windows 마우스 키를 켜고 대체 커서를 끄는 별도 표시 설정입니다. 숫자 키패드 동작에 영향을 줄 수 있습니다.
- **배포 범위:** Windows x64 설치 EXE를 제공합니다. ARM64 빌드 옵션을 ARM64 실사용 검증으로 해석하지 않습니다. 자동 시험 통과와 실제 세 PC 사용 확인도 구분합니다.
- **제거·다시 설치:** Windows **설치된 앱** 또는 MWBP 설정에서 제거합니다. 연결·설정 보존 여부를 선택할 수 있습니다.

[제품 사이트의 FAQ](https://ttaem00.github.io/CVA-VEIL-Mouse_Without_Border_Plus/#faq) · [오류 제보](https://github.com/ttaem00/CVA-VEIL-Mouse_Without_Border_Plus/issues)

<details>
<summary><strong>개발·빌드·검증 안내</strong></summary>

개발 환경은 Windows, PowerShell 7, .NET SDK **10.0.403**입니다. `global.json`으로 SDK를 고정하며 설치 EXE에는 .NET **10.0.14** 런타임이 포함됩니다. 사용자는 SDK나 비공개 저장소 접근이 필요하지 않습니다.

```powershell
pwsh -NoProfile -File ./build.ps1 -RunTests
pwsh -NoProfile -File './VEIL- MWBP/Build-Release.ps1' -Destination 'C:/build/VEIL- MWBP'
```

상세 배포 진입점은 [배포 안내](<VEIL- MWBP/README.md>)입니다. 출력 폴더는 비어 있어야 하며, 단일 설치 EXE·SHA256·라이선스·설치 안내를 생성합니다. 실행 파일·서비스·설정의 내부 이름과 저장 경로는 기존 MWBP와 호환되게 유지합니다.

설정 저장·다른 PC 송신·파일 취소와 일반 마우스 이동 사이의 불필요한 잠금 결합을 줄이고, 클립보드 이벤트의 강제 전체 GC를 제거했습니다. 이는 코드 변경 내용이며 실제 체감 지연 해소를 입증하는 수치는 아닙니다.

Windows 화면 합성 시험은 시험 창이 전경을 확보해야 실행됩니다. 이 전제가 성립하지 않으면 포커스·합성 검사는 `Inconclusive`로 남으며 PASS로 세지 않습니다. 다른 파일·입력·설치 시험은 독립적으로 실행합니다.

공개 소스에는 실제 사용자 설정·보안 키·전송 기록·개인 PC 진단 자료를 포함하지 않습니다. 과거 로컬 시험 화면을 현재 배포본의 실제 화면으로 게시하지 않습니다. 제품 사이트는 [정적 HTML과 CSS](docs/README.md)이며 별도 빌드나 JavaScript 없이 읽을 수 있습니다.

</details>

## 출처와 라이선스

Microsoft PowerToys의 Mouse Without Borders를 기반으로 한 **비공식 프로젝트**입니다. Microsoft와 제휴하거나 보증받은 제품이 아닙니다.

MIT [aeae1/MouseWithoutBorders-Portable](https://github.com/aeae1/MouseWithoutBorders-Portable)의 `e9feba6da37b35a763a638a7e071c7189180cc14`를 기반으로 수정했습니다. 원본 저작권·[LICENSE](LICENSE)·[NOTICE](NOTICE.md)와 [제3자 고지](<VEIL- MWBP/THIRD-PARTY-NOTICES.txt>)를 보존합니다. Input Director는 공개 UX 자료만 참고했으며 독점 코드를 사용하지 않았습니다.

VEIL 표식은 사용자가 확정한 [원본 이미지](App/Veil.png)를 사용합니다. Microsoft의 원본 그림을 재색칠한 아이콘이 아닙니다.
