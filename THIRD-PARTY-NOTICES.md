# Third-party notices

Esco EV OBD Scanner is MIT licensed (see LICENSE). It includes or redistributes
the following third-party material, each under the license shown.

## .NET runtime

The self-contained app and its installer include the .NET runtime, with the
System.IO.Ports and System.Management packages, and the
Microsoft.Extensions.Logging.Abstractions,
Microsoft.Extensions.DependencyInjection.Abstractions and
System.Security.Cryptography.Pkcs packages that PDFsharp uses.

Copyright (c) .NET Foundation and Contributors. MIT License (text below).
https://github.com/dotnet/runtime. The runtime's own notices for the components
it bundles: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
The Windows desktop parts (WPF, https://github.com/dotnet/wpf, and
https://github.com/dotnet/windowsdesktop) are under the same license; their notices:
https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT

## PDFsharp and MigraDoc

Used to write the battery report (PDF).

Copyright (c) 2001-2026 empira Software GmbH, Troisdorf (Cologne Area), Germany.
MIT License (text below). https://github.com/empira/PDFsharp

## WiX Toolset

The installer's dialogs and their images come from the WiX Toolset
(WixToolset.UI.wixext). Microsoft Reciprocal License (MS-RL).
https://github.com/wixtoolset/wix

## evDash sample replies

`EscoEvObdScanner.Tests/KiaENiroTests.cs` contains sample replies published with
evDash's Kia e-Niro decoder, used as test data.

Copyright (c) 2020 NiCK.n17. MIT License (text below). https://github.com/nickn17/evDash

## Facts from other projects

The car files contain facts (ECU addresses, identifiers, byte positions and
scales) worked out by the projects credited in README.md and in each car file's
`sources`. No code, definitions or data files from those projects are included.

## MIT License

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
