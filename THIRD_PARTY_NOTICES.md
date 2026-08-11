# Third-party notices

## KiriKiri runtime-capture proxy

`src/KiriScope.Gui/Assets/KiriScope.RuntimeCapture.X86.dll` contains a modified build of the
[KirikiriTools](https://github.com/arcusmaximus/KirikiriTools) `KirikiriUnencryptedArchive` proxy,
Copyright (c) 2018 arcusmaximus, distributed under the MIT License. KiriScope's modifications add
archive-driven enumeration, opaque-index-safe capture naming, validation manifests, and completion markers
for verified batch capture. The bundled build copies decoded runtime streams without applying the extraction filter a
second time, and starts its one-shot archive traversal only from an engine-thread storage open; the proxy is bundled only for the GUI's
isolated KiriKiri runtime-capture fallback.

Copyright (c) 2018 arcusmaximus

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## CxEncryption filter

`src/KiriScope.Filters.BuiltIn/CxContentFilter.cs` is an independently adapted implementation informed by the CxEncryption reader in [GARbro](https://github.com/morkt/GARbro), Copyright (c) 2014-2016 morkt, distributed under the MIT License. The MIT license text below applies to this adapted implementation as well.

`plugins/schemes/cx-9nine-kokonotsu.scheme.json` contains a Cx parameter record derived from the [GARbro2](https://github.com/UserUnknownFactor/GARbro2) format database, Copyright (c) 2014-2025 morkt, UserUnknownFactor, crskycode and other contributors, distributed under the MIT License. KiriScope verifies this record against current-input XP3 Adler-32 values before it can be selected. The MIT license text below applies to this derived format record as well.

## Protected XP3 filename-list reader

`src/KiriScope.Xp3/Xp3ArchiveReader.cs` includes an independently implemented reader for the protected
YuzuSoft-family XP3 filename-list layout (including the `cbg:` section). It is informed by the
KiriKiri reader in [GARbro](https://github.com/morkt/GARbro), Copyright (c) 2014-2020 morkt,
distributed under the MIT License. The MIT license text below applies to this implementation as well.

## TLG5 decoder algorithm

`src/KiriScope.Resources/Tlg5Decoder.cs` is an independently adapted implementation informed by the TLG reader in [GARbro](https://github.com/morkt/GARbro), Copyright (c) 2014-2020 morkt, distributed under the MIT License.

Copyright (c) 2014-2020 morkt

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
