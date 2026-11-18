# Third-party notices / Üçüncü taraf bileşenler

GameTranslator is licensed under the GNU AGPL-3.0 (see [LICENSE](LICENSE)). It is derived from
Sinan Yorulmaz's CouchSubs project (https://github.com/yorulmazsinan/couchsubs) and has been modified
since November 10, 2026. It includes or downloads the following third-party components. Full source
code for GameTranslator, including the build scripts that produce the bundled libraries, is available at
https://gitlab.com/sefflex/GameTranslator.

## Bundled in the Mac and Windows apps

| Component | License | Source |
|---|---|---|
| chiaki-ng (`libchiaki`, Remote Play) | AGPL-3.0 with OpenSSL exception | https://github.com/chiaki-ng/chiaki-ng |
| nanopb | zlib | https://github.com/nanopb/nanopb |
| Jerasure | BSD-3-Clause | https://github.com/ceph/jerasure |
| GF-Complete | BSD-3-Clause | https://github.com/ceph/gf-complete |
| curl | curl license (MIT/X derivative) | https://curl.se |
| OpenSSL 3 | Apache-2.0 | https://www.openssl.org |
| json-c | MIT | https://github.com/json-c/json-c |
| libevent | BSD-3-Clause | https://libevent.org |
| miniupnpc | BSD-3-Clause | https://miniupnp.tuxfamily.org |
| TizenSdb.Core (Samsung TV connection and package signing) | MIT, Copyright (c) Patrick Stel | https://github.com/PatrickSt1991/tizen-sdb |
| Apps2Samsung (Samsung sign-in and certificate request flow, adapted) | MIT, Copyright (c) 2025 Patrick Stel | https://github.com/Jellyfin2Samsung/Samsung-Jellyfin-Installer |
| .NET runtime (Mac TV install helper, Native AOT) | MIT | https://github.com/dotnet/runtime |
| pocket-tts (Turkish dubbing voice engine, unmodified release binary; includes xn-ptts, Apache-2.0, and SentencePiece, Apache-2.0) | MIT, Copyright (c) Kyutai | https://github.com/kyutai-labs/pocket-tts |

Samsung developer CA certificates (`vd_tizen_dev_author_ca`, `author_ca`, `vd_tizen_dev_public2`, `public2`) are
redistributed unmodified from Tizen Studio, solely so that the TV app can be signed for the user's own Samsung TV. They
are the property of Samsung Electronics Co., Ltd. and are not covered by the GameTranslator license.

MIT License text (TizenSdb.Core, Apps2Samsung): Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED
TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Windows app only

| Component | License | Source |
|---|---|---|
| FFmpeg 7.1.2 (`libavcodec` H.264 decoder, `libswscale`; statically linked) | LGPL-2.1-or-later | https://ffmpeg.org |
| .NET runtime and WPF | MIT | https://github.com/dotnet |
| ONNX Runtime | MIT | https://github.com/microsoft/onnxruntime |
| Microsoft.ML.Tokenizers | MIT | https://github.com/dotnet/machinelearning |
| System.Security.Cryptography.ProtectedData | MIT | https://github.com/dotnet/runtime |
| Microsoft WebView2 SDK | BSD-3-Clause | https://www.nuget.org/packages/Microsoft.Web.WebView2 |
| SSH.NET (LG TV connection) | MIT | https://github.com/sshnet/SSH.NET |
| BouncyCastle.Cryptography (used by SSH.NET) | MIT | https://github.com/bcgit/bc-csharp |
| Microsoft.Extensions.Logging.Abstractions (used by SSH.NET) | MIT | https://github.com/dotnet/runtime |

The Windows app also includes a list of common English words (`Assets/english-words.txt.gz`, lower-cased, generated
from the English Speller Database, size 60, at https://app.aspell.net/create), used to tell real words from text
recognition errors:

> Copyright 2000-2026 by Kevin Atkinson
>
> Permission to use, copy, modify, distribute, and sell any part of the English Speller Database (ESDB, previously
> known as SCOWLv2), or word lists created from it, is hereby granted without fee, provided that the above copyright
> notice appears in all copies and that both the above copyright notice and this notice appear in supporting
> documentation. Kevin Atkinson makes no representations about the suitability of this database for any purpose. It is
> provided "as is" without express or implied warranty.

FFmpeg is built from the unmodified upstream release with the configuration in
`.github/workflows/windows.yml`. You can rebuild `gametranslator_native.dll` with your own FFmpeg build using
`windows/native/CMakeLists.txt` and place it next to `GameTranslator.exe`.

## Downloaded on first use (Windows)

| Component | License | Source |
|---|---|---|
| OPUS-MT English→Turkish translation model (`opus-mt-tc-big-en-tr`, ONNX export) | CC BY 4.0 | Language Technology Research Group, University of Helsinki — https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-en-tr, ONNX conversion https://huggingface.co/onnx-community/opus-mt-tc-big-en-tr |

The model is used unmodified (int8 quantised ONNX files as published by onnx-community).

## Optional text recognition model (Windows)

| Component | License | Source |
|---|---|---|
| PP-OCRv4 text detection and recognition models (`ch_PP-OCRv4_det_infer.onnx`, `ch_PP-OCRv4_rec_infer.onnx`) and PP-OCRv3 English recognition model (`en_PP-OCRv3_rec_infer.onnx`) | Apache-2.0 | PaddleOCR, PaddlePaddle Authors — https://github.com/PaddlePaddle/PaddleOCR; ONNX conversion by RapidOCR — https://github.com/RapidAI/RapidOCR, files from https://huggingface.co/SWHL/RapidOCR |

The models are not part of the installer; the Windows app downloads them in the background on first start. When the first two files are present in
`%LOCALAPPDATA%\GameTranslator\models\ppocr`, the Windows app re-reads subtitle lines that Windows' own text recognition
got wrong with them; when the English recognition model is also present and the game language is English, each line is
read with both recognition models and the better reading is kept. They are used unmodified; the app checks their size
and SHA-256 (`windows/src/GameTranslator.Translation/PpOcrReader.cs`).

## Downloaded on first use of Turkish dubbing (Mac)

| Component | License | Source |
|---|---|---|
| Pocket TTS Turkish (model weights, tokenizer and six synthetic voices) | CC BY 4.0, by wite-tech, based on Kyutai's Pocket TTS (CC BY 4.0) | https://huggingface.co/wite-tech/pocket-tts-turkish |

The model and voices are used unmodified. GameTranslator shortens long pauses in the generated audio. The voices are
synthetic and are not recordings of real people.

## Mac app

Text recognition (Vision) and translation (Translation framework) are provided by macOS.

## TV app font (Arabic edition)

The TV app bundles **Noto Sans Arabic** (Regular and Bold, Arabic subset, from the `@fontsource/noto-sans-arabic`
package, version 5.3.0) for right-to-left subtitles. Copyright 2022 The Noto Project Authors
(https://github.com/notofonts/arabic). Licensed under the SIL Open Font License, Version 1.1; the full text is in
`tv/webos/fonts/OFL.txt` and ships inside the TV app. The font files are used unmodified.

## Trademarks

GameTranslator is not affiliated with or endorsed by Sony Interactive Entertainment. "PlayStation" and "PS5"
are trademarks of Sony Interactive Entertainment Inc. LG and webOS are trademarks of LG Electronics Inc.
