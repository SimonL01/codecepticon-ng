# NOTICE

`codecepticon-ng` is a **port** of Accenture/Pavel Tsakalidis' Codecepticon to a
current .NET toolchain. It is a derivative work and contains code from three
separately-authored MIT-licensed projects. All three are reproduced below
because MIT requires the copyright notice to travel with the code.

---

## 1. Codecepticon

The bulk of `src/Codecepticon.Ng/{Modules,Utils,Templates,Help}` and
`Program.cs` derives from Codecepticon v1.2.3.

- **Upstream:** https://github.com/sadreck/Codecepticon
- **Original home:** https://github.com/Accenture/Codecepticon (unmaintained since 2024-02)
- **Ported from commit:** `c0b3e7a`
- **Licence:** MIT
- **Copyright:** (c) 2022 - 02/2024 Accenture Security; (c) 03/2024 - Present Pavel Tsakalidis

## 2. ProLeap VB6 ANTLR grammar

`src/Codecepticon.Ng/Modules/VB6/ANTLR/VisualBasic6.g4` and the six parser,
lexer, listener and visitor files generated from it.

- **Upstream:** https://github.com/uwol/proleap-vb6-parser
- **Licence:** MIT
- **Copyright:** (c) 2017 Ulrich Wolffgang <ulrich.wolffgang@proleap.io>

## 3. SigningServer

`src/Codecepticon.Ng/Modules/Sign/MsSign/` — Authenticode signing, taken and
customised by Codecepticon upstream, as recorded in its own source header and
CHANGELOG v1.2.0.

- **Upstream:** https://github.com/Danielku15/SigningServer
- **Licence:** MIT
- **Copyright:** (c) Daniel Kuschny (Danielku15)

---

## MIT License

Applies to all four copyright holders above, including this project's own.

```
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
```
