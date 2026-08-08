Steam Workshop 업로드용 워크스페이스입니다 (MegaCrit 공식 [sts2-mod-uploader](https://github.com/megacrit/sts2-mod-uploader) 도구용).

## 준비물

1. [ModUploader-win-x64.zip](https://github.com/megacrit/sts2-mod-uploader/releases/download/v0.2.0/ModUploader-win-x64.zip) 다운로드 후 압축 해제 (아무 폴더에나).
2. 최신 빌드 배포: `dotnet build Sts2Matchmaker.csproj` (리포 루트에서) — `Sts2Matchmaker.csproj.user`에 설정된 `ModDeployDir`로 배포됩니다.

## 업로드 절차

1. 배포된 파일을 이 폴더의 `content/`로 복사 (예: `ModDeployDir`가 `G:\내 드라이브\sts2matchmaker`라면 그 안의 `sts2_matchmaker.dll`, `sts2_matchmaker.pdb`, `mod_manifest.json`, `assets/`를 전부 `workshop/content/`로 복사).
2. `workshop.json`에서 `title`/`description`/`changeNote` 확인 (첫 업로드는 `visibility: "private"`로 걸어두고 확인 후 웹에서 public 전환 권장).
3. `image.png` 확인 (현재 `assets/submenu_matching.png` 재사용, 1MB 미만).
4. `ModUploader.exe`가 있는 폴더에서 명령 프롬프트 열고: `ModUploader.exe upload -w <이 workshop 폴더 경로>`
5. 업로드 성공 시 이 폴더에 `mod_id.txt`가 생성됨 — **이후 업데이트 때마다 필요하므로 지우지 말고 커밋할 것** (다음 업로드부터 이 ID로 같은 항목을 갱신함, 없으면 새 항목이 중복 생성됨).
