# Third-party notices

## libsai — Wunkolo

The SAI2 tile decoder (`DpcmCodec`) and the jssf→JPEG conversion (`JssfCodec`)
are C# ports of code from https://github.com/Wunkolo/libsai
(`source/sai2.cpp`). The test fixtures were verified against the original
decoder (see `tools/crosscheck`).

```
MIT License

Copyright (c) 2017-2023 Wunkolo

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

## SAI2 file format specification — Photopea

The layout of the header, the chunk table and the `layr` chunk follows
https://github.com/photopea/SAI2-specification (unofficial specification).
