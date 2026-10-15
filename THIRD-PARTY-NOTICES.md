# 제3자 구성요소 고지 (THIRD-PARTY NOTICES)

몽작업표시줄은 아래 제3자 구성요소를 사용하거나 함께 배포한다. 각 구성요소는 해당 저작권자의 라이선스를 따르며, 그 조건은 몽작업표시줄의 GPL-3.0 라이선스(`LICENSE`)와 별개로 유지된다.

## 구성요소 목록

| 구성요소 | 버전 | 라이선스 | 출처 | 고지 파일 위치 |
|---|---|---|---|---|
| MinHook | 1.3.4 | BSD-2-Clause | https://github.com/TsudaKageyu/minhook | `licenses/minhook/LICENSE` (저작자 목록 `licenses/minhook/AUTHORS.txt`) |
| Hacker Disassembler Engine 32/64 C (MinHook에 포함) | MinHook 1.3.4에 포함된 것 | BSD-2-Clause | MinHook 저장소의 `src/hde/` | `licenses/minhook/LICENSE` (같은 파일에 함께 수록) |

- MinHook은 Tsuda Kageyu 등의 저작물이다(Copyright (C) 2009-2017 Tsuda Kageyu). 포함된 Hacker Disassembler Engine은 Copyright (c) 2008-2009 Vyacheslav Patkov이다.
- 소스는 수정하지 않고 `hook/third_party/minhook/` 에 그대로 두었다. 수정한 부분은 없다.
- MinHook 소스(`buffer.c`, `hook.c`, `trampoline.c`, `hde64.c`)는 `hook/placer.dll` 과 `hook/observer.dll` 에 정적으로 빌드되어 들어간다(`hook/build_placer.bat`, `hook/build_observer.bat`). 설치본에는 `hook/placer.dll` 이 포함된다.
- BSD-2-Clause는 바이너리로 재배포할 때 저작권 고지·조건·면책 문구를 문서에 넣도록 요구한다. 이 파일과 `licenses/minhook/LICENSE` 가 그 고지를 맡는다.

## 제3자 구성요소가 아닌 것

- 자동 업데이트 모듈(`src/MongUpdate.cs`, `src/MongUpdateUi.WinForms.cs`)은 몽 시리즈 공통 모듈로 Bh-Nakh 자체 저작물이며 GPL-3.0 대상이다.
- Windows에 포함된 .NET Framework 4.x와 Win32 API는 시스템 구성요소로, 함께 배포하지 않는다.
- 빌드 도구(C# 컴파일러, MSVC `cl`, Inno Setup)는 결과물을 만들 때만 쓰며 소스에 들어 있지 않다. 설치 프로그램에는 Inno Setup 런타임이 들어가며, 그 조건은 Inno Setup 라이선스를 따른다.
- Windows 셸(explorer.exe)의 내부 함수 이름·오프셋을 담은 조사 자료(`hook/research/explorer_*_symbols.txt`)는 마이크로소프트 심볼에서 추출한 데이터라서 저장소에 올리지 않는다(`.gitignore` 로 제외).

## 라이선스 원문 보관 위치

- 저장소 기준 `licenses/minhook/` 폴더에 MinHook의 `LICENSE`(Hacker Disassembler Engine 고지 포함)와 `AUTHORS.txt` 를 받은 그대로 보관한다.
- 원문은 수정하지 않는다.
- 설치본에도 같은 폴더를 포함해 사용자가 열람할 수 있게 하는 것이 목표이다. 현재 설치 스크립트(`installer/몽작업표시줄.iss`)는 아직 이 폴더를 설치본에 넣지 않으므로, 다음 코드 수정 때 추가해야 한다.