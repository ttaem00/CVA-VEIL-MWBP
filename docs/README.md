# 제품 사이트 유지보수

`index.html`과 `styles.css`를 그대로 제공하는 정적 사이트입니다. Node·번들러·JavaScript·외부 글꼴·사용자 추적을 사용하지 않습니다. GitHub Pages의 `main` 브랜치 `/docs` 게시를 대상으로 하며, 실제 Pages 설정·배포는 저장소 관리자가 별도로 수행합니다.

- 다운로드와 릴리스 링크는 `mwb-v1.2.0-rc.17`에 명시적으로 고정합니다. prerelease를 건너뛰는 `latest` 링크로 바꾸지 않습니다.
- VEIL은 공통 V를 쓰며, MWBP의 오른쪽 아래 마우스 표식은 여러 PC에서 공유하는 입력을 뜻합니다. `assets/veil-mwbp-color.svg`와 `assets/veil-mwbp-micro.svg`는 CVA 브랜드 정본 `docs/design/cva-brand-v1/media/icons/veil/`의 정확한 사본입니다. 원본 이미지를 크롭하거나 V를 제품마다 다시 그리지 않습니다.
- `assets/veil.png`와 `App/Veil.png`는 같은 512×512 RGBA 파생본입니다. `App/Veil.ico`는 16·20·24·32·40·48·64·128·256px를 포함하며 16·20px에는 micro를 사용합니다. 생성 원본·파생 파일 SHA256은 [veil-family-source.json](assets/veil-family-source.json)에 보존합니다. 앱·설치 EXE와 사이트 favicon(`assets/veil.ico`)은 같은 ICO를 소비합니다. renderer는 authoring 전용이며 앱 실행 의존성이 아닙니다.
- `assets/mwbp-settings-anonymized.png`는 사용자가 제공한 RC10 실사용 캡처를 기반으로 PC 이름을 일반 이름으로 바꾸고 창 바깥 흔적을 제거한 **익명화 편집본**입니다. 내장 이미지 편집 도구로 확대·보정됐으며 원본 픽셀을 그대로 보존한 캡처로 설명하지 않습니다. 배치·모니터 번호·연결 상태·버튼 구성을 원본과 대조했고, 원본 캡처와 개인 PC 이름·경로는 배포하지 않습니다. README와 사이트는 같은 이미지를 소비하며, 크게 보기로 전체 화면을 확인할 수 있습니다.
- 의미 구조·본문 건너뛰기·키보드 포커스·native FAQ를 유지합니다. 좁은 화면과 200% 확대에서 실제 렌더를 확인하고, source 검사만으로 시각 품질을 PASS 처리하지 않습니다.
- RC·미서명·Windows x64·Microsoft 비공식·MIT 및 제3자 고지를 유지합니다. 자동 시험과 실제 PC 사용을 구분하며 지연 해소를 보장하는 문구를 추가하지 않습니다.

## 참고한 공개 자료

2026-11-06 공개 README를 GitHub API로 읽고, 별도 Chrome 연구 탭에서 다음 실제 화면을 확인했습니다. 최초 IAB 요청은 provider 미제공으로 열리지 않았으며, 허용된 Chrome 경로로 확인했습니다.

| 자료 | 실제 확인한 화면 | 적용한 원리 |
| --- | --- | --- |
| [CVA-Elio](https://github.com/ttaem00/CVA-Elio) · [사이트](https://ttaem00.github.io/CVA-Elio/) | paper/navy의 큰 제목, 행동 링크, 명시적으로 표기한 구성 예시 | 제목·설치 행동의 위계, 큰 여백, 사실과 개념도의 구분 |
| [CVA-Loki README](https://github.com/ttaem00/CVA-Loki) | GitHub에서 렌더된 hero, 중앙 정렬 브랜드·설치 목차, 별도 지원 한계 | 짧은 상단과 직접 설치 링크, 개발 정보의 접힌 배치 |
| [CVA-탬패드 문서](https://ttaem00.github.io/cva-ttaempad-timeline-spec/) | 안정적인 목차, 읽기 폭, 지원 범위와 실제 공개 상태의 구분 | 읽기 쉬운 본문과 지원 경계; 단일 소개 페이지에는 과한 다중 목차를 도입하지 않음 |

`modern-web-guidance`의 `accessibility`, `typography`, `responsive-design` 자료를 참고했습니다. 외부 제품 코드·라이선스를 이 프로젝트에 복사하지 않았습니다.

2026-11-06 추가로 [Raycast](https://www.raycast.com/), [Linear Features](https://linear.app/features), [Logitech Flow](https://www.logitech.com/en-us/software/flow)의 실제 데스크톱 화면을 비교했습니다. 각각 짧은 제품 정의와 다운로드, 제목·보조 설명의 위계, 여러 PC를 쓰는 장면으로 기능을 설명하는 원리를 참고했습니다. 대형 장식·애니메이션·CSS 모니터 모형은 채택하지 않고, 이번 수정에서는 익명화한 실사용 화면·한 가지 설치 행동·native 도움말에 집중했습니다. CVA의 기존 paper/navy/brass와 확정된 VEIL 마우스 아이콘은 유지합니다.

### 전체 페이지 정돈 (issue22)

2026-11-06에 별도 연구 탭에서 다음 세 사이트의 **실제 데스크톱 뷰포트 스크린샷**을 다시 확인하고 비교했습니다. 사이트 전체나 모바일을 확인했다는 의미는 아닙니다.

| 레퍼런스 | 화면에서 확인한 점 | 적용 / 미채택 |
| --- | --- | --- |
| [Raycast](https://www.raycast.com/) | 중앙의 큰 두 줄 제목, 짧은 보조 문구와 분리된 다운로드 | 문구와 행동의 위계만 참고. 붉은 대형 배경 효과·다수 메뉴는 도입하지 않음 |
| [Linear Features](https://linear.app/features) | 충분한 여백, 큰 제목과 작은 보조 설명, 제품 그림의 독립 영역 | 제품 화면을 히어로의 작은 옆 그림에서 독립된 넓은 구역으로 이동. 아이콘 격자·어두운 테마는 복사하지 않음 |
| [Apple Mac mini](https://www.apple.com/mac-mini/) | 밝은 배경의 큰 제품 사진, 짧은 제목과 명확한 구매 행동 | 기존 CVA paper 색에서 실제 제품을 넓게 제시. 외부 이미지·제품 외형·애니메이션은 사용하지 않음 |

제목·설명·다운로드 → 실제 앱 화면 → 세 가지 기능 → 설치 → 접힌 도움말 순서입니다. 기능은 세 열, 설치는 별도 표면, 도움말은 읽기 폭을 제한해 같은 구획의 반복을 줄였습니다. 화면은 잘라내거나 재생성하지 않았으며 익명화 편집 공개와 전체 이미지 링크를 유지합니다. 본문 크기·키보드 초점·native details·좁은 화면·reduced motion을 확인합니다. README의 도움말 링크도 실제 `#help` 앵커와 맞춥니다. JS·외부 글꼴·새 의존성·앱/설정 변경은 없습니다.
