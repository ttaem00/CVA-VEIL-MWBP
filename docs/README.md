# 제품 사이트 유지보수

`index.html`과 `styles.css`를 그대로 제공하는 정적 사이트입니다. Node·번들러·JavaScript·외부 글꼴·사용자 추적을 사용하지 않습니다. GitHub Pages의 `main` 브랜치 `/docs` 게시를 대상으로 하며, 실제 Pages 설정·배포는 저장소 관리자가 별도로 수행합니다.

- 다운로드와 릴리스 링크는 `mwb-v1.2.0-rc.7`에 명시적으로 고정합니다. prerelease를 건너뛰는 `latest` 링크로 바꾸지 않습니다.
- `assets/veil.png`는 `App/Veil.png`의 byte-for-byte 복사본입니다. SHA256은 `BF63BE1B0CDC734AC3ACD2A7FA7820A8FB00188A0A2D3F045094920B0CE46A1E`입니다. 확정 로고를 다시 그리거나 trace·recolor하지 않습니다.
- 화면 배치 그림은 HTML/CSS로 만든 **개념도**이며 앱 캡처가 아닙니다. 개인 경로가 노출된 로컬 시험 PNG를 공개 자료로 사용하지 않습니다.
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
