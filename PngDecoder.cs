using System;
using System.IO;
using System.IO.Compression;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace StrikeCoin
{
    /// <summary>
    /// 纯托管 PNG 解码器，逐行移植自 Lethe 的 PngDecoder
    /// （analysis/lethe-dll-decompiled/Lethe.decompiled.cs:25584）。
    ///
    /// 为什么需要它：本作把 UnityEngine.ImageConversion 剥得只剩 EncodeToPNG，
    /// Texture2D.LoadImage / ImageConversion.LoadImage 都没有原生实现了，调用会抛
    /// "Method unstripping failed"。而这里用的 System.IO.Compression.DeflateStream
    /// 是 BCL，跑在插件自己的 CoreCLR 上，不走 IL2CPP，所以完全不受剥离影响。
    ///
    /// 限制（与 Lethe 一致）：只支持 8-bit，不支持调色板（color type 3）。
    /// 用 Photoshop / GIMP 导出时选 RGBA32、不要勾选隔行扫描即可。
    /// </summary>
    internal static class PngDecoder
    {
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static Texture2D Load(byte[] pngData)
        {
            using (var ms = new MemoryStream(pngData))
            using (var br = new BinaryReader(ms))
            {
                byte[] sig = br.ReadBytes(8);
                if (sig.Length != 8) throw new InvalidDataException("PNG 太短");
                for (int i = 0; i < 8; i++)
                    if (sig[i] != PngSignature[i])
                        throw new InvalidDataException("不是合法的 PNG 文件");

                int width = 0, height = 0, bitDepth = 0, colorType = 0;
                byte[] idat = null;

                while (ms.Position < ms.Length)
                {
                    int len = ReadBigEndianInt32(br);
                    string type = new string(br.ReadChars(4));
                    byte[] body = br.ReadBytes(len);
                    br.ReadInt32(); // CRC，跳过

                    switch (type)
                    {
                        case "IHDR":
                            width = ReadBigEndianInt32(body, 0);
                            height = ReadBigEndianInt32(body, 4);
                            bitDepth = body[8];
                            colorType = body[9];
                            if (bitDepth != 8)
                                throw new NotSupportedException("PNG 位深 " + bitDepth + " 不支持（只支持 8-bit）");
                            if (colorType == 3)
                                throw new NotSupportedException("不支持调色板型 PNG");
                            continue;
                        case "IDAT":
                            if (idat == null)
                            {
                                idat = body;
                            }
                            else
                            {
                                var merged = new byte[idat.Length + body.Length];
                                Buffer.BlockCopy(idat, 0, merged, 0, idat.Length);
                                Buffer.BlockCopy(body, 0, merged, idat.Length, body.Length);
                                idat = merged;
                            }
                            continue;
                        case "IEND":
                            break;
                        default:
                            continue;
                    }
                    break;
                }

                if (idat == null || width == 0 || height == 0)
                    throw new InvalidDataException("PNG 无效或为空");

                byte[] raw;
                using (var zs = new MemoryStream(idat))
                {
                    zs.ReadByte();
                    zs.ReadByte(); // zlib 头 2 字节
                    using (var inflate = new DeflateStream(zs, CompressionMode.Decompress))
                    using (var outMs = new MemoryStream())
                    {
                        inflate.CopyTo(outMs);
                        raw = outMs.ToArray();
                    }
                }

                int channels;
                switch (colorType)
                {
                    case 0: channels = 1; break; // 灰
                    case 2: channels = 3; break; // RGB
                    case 4: channels = 2; break; // 灰+Alpha
                    case 6: channels = 4; break; // RGBA
                    default: throw new NotSupportedException("PNG 颜色类型 " + colorType + " 不支持");
                }

                int stride = width * channels;
                byte[] rgba = new byte[width * height * 4];
                byte[] prev = new byte[stride];
                byte[] cur = new byte[stride];

                for (int y = 0; y < height; y++)
                {
                    int src = y * (stride + 1);
                    byte filterType = raw[src];
                    Array.Copy(raw, src + 1, cur, 0, stride);
                    UnfilterScanline(cur, prev, stride, channels, filterType);

                    // Unity 纹理原点在左下，PNG 在左上 —— 顺带翻转 Y
                    int dstRow = (height - 1 - y) * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int s = x * channels;
                        int d = dstRow + x * 4;
                        switch (colorType)
                        {
                            case 0:
                                rgba[d] = rgba[d + 1] = rgba[d + 2] = cur[s];
                                rgba[d + 3] = byte.MaxValue;
                                break;
                            case 2:
                                rgba[d] = cur[s];
                                rgba[d + 1] = cur[s + 1];
                                rgba[d + 2] = cur[s + 2];
                                rgba[d + 3] = byte.MaxValue;
                                break;
                            case 4:
                                rgba[d] = rgba[d + 1] = rgba[d + 2] = cur[s];
                                rgba[d + 3] = cur[s + 1];
                                break;
                            case 6:
                                rgba[d] = cur[s];
                                rgba[d + 1] = cur[s + 1];
                                rgba[d + 2] = cur[s + 2];
                                rgba[d + 3] = cur[s + 3];
                                break;
                        }
                    }

                    var swap = prev;
                    prev = cur;
                    cur = swap;
                }

                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                Il2CppStructArray<byte> data = rgba;
                tex.LoadRawTextureData(data);
                tex.Apply();
                return tex;
            }
        }

        private static void UnfilterScanline(byte[] cur, byte[] prev, int stride, int bpp, byte filterType)
        {
            switch (filterType)
            {
                case 0:
                    break;
                case 1:
                    for (int i = bpp; i < stride; i++) cur[i] += cur[i - bpp];
                    break;
                case 2:
                    for (int i = 0; i < stride; i++) cur[i] += prev[i];
                    break;
                case 3:
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        int b = prev[i];
                        cur[i] = (byte)(cur[i] + (a + b) / 2);
                    }
                    break;
                case 4:
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        int b = prev[i];
                        int c = i >= bpp ? prev[i - bpp] : 0;
                        cur[i] = (byte)(cur[i] + PaethPredictor(a, b, c));
                    }
                    break;
                default:
                    throw new NotSupportedException("PNG filter type " + filterType + " 不支持");
            }
        }

        private static int PaethPredictor(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        private static int ReadBigEndianInt32(BinaryReader reader)
        {
            byte[] b = reader.ReadBytes(4);
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        private static int ReadBigEndianInt32(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }
    }
}
