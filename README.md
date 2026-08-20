# MovieConcatenater

同一コーデック・解像度の分割動画を、ドラッグ＆ドロップで連結する Windows アプリです。形式が一致する場合は FFmpeg の concat demuxer（`-c copy`）で高速連結し、不一致の場合は先頭ファイルの形式に合わせて再エンコードして連結します。

## 必要環境

- Windows x64
- PATH 上の `ffmpeg` / `ffprobe`（またはアプリ内でパス指定）
- ビルド時のみ [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

配布用 Exe は自己完結のため、実行側に .NET ランタイムのインストールは不要です。

## ビルド / 実行

```powershell
cd D:\Documents\GitHub\MovieConcatenater
dotnet build
dotnet run
```

## 配布用 Exe（単体）

```powershell
cd D:\Documents\GitHub\MovieConcatenater
dotnet publish -c Release -p:DistributionPublish=true -o .\bin\Distribution
```

出力: `bin\Distribution\MovieConcatenater.exe`  
この 1 ファイルを渡せば動きます（FFmpeg は別途 PATH またはアプリ内で指定）。

## 使い方

1. 分割された動画をウィンドウへドラッグ＆ドロップ
2. リストでフルパス・解像度・コーデック等を確認
3. 出力フォルダ / ファイル名（またはフルパス）を指定
4. **実行** で連結（実行中はステータスバーの **中止** 以外の操作は不可）
5. 形式が一致しない場合は確認ダイアログのあと、先頭ファイル基準で再エンコード連結

ファイル名末尾が連番（例: `movie_01.mp4`, `movie_02.mp4`）の場合、連番を除いた名前がデフォルト出力名になります。

解像度・映像コーデック・音声コーデック・fps が一致しないファイルは、確認なしでは連結しません。再エンコードを選ぶと先頭ファイルの形式に揃えます。
