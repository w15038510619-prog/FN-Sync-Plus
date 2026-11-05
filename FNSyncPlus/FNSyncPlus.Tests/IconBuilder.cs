using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace FNSyncPlus.Tests
{
    public static class IconBuilder
    {
        public static void GenerateIcons(string artifactsDir, string assetsDir)
        {
            // 1. Generate 3-Concept Preview side-by-side
            using (var previewBmp = new Bitmap(800, 280))
            using (var g = Graphics.FromImage(previewBmp))
            {
                g.Clear(Color.FromArgb(248, 250, 252));
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using (var font = new Font("Segoe UI", 10, FontStyle.Bold))
                using (var textBrush = new SolidBrush(Color.FromArgb(71, 85, 105)))
                {
                    using (var bmpA = RenderConceptA(240))
                    {
                        g.DrawImage(bmpA, 20, 15);
                        g.DrawString("方案 A: 现代立体存储仓", font, textBrush, 45, 255);
                    }
                    using (var bmpB = RenderConceptB(240))
                    {
                        g.DrawImage(bmpB, 280, 15);
                        g.DrawString("方案 B: 极简盾牌安全同步标", font, textBrush, 305, 255);
                    }
                    using (var bmpC = RenderConceptC(240))
                    {
                        g.DrawImage(bmpC, 540, 15);
                        g.DrawString("方案 C: 飞牛 Fluent 存储魔方 (精选)", font, textBrush, 550, 255);
                    }
                }

                string previewPath = Path.Combine(artifactsDir, "gui_v0.4.0_icon_concepts.png");
                previewBmp.Save(previewPath, ImageFormat.Png);
                Console.WriteLine($"[ICON] Concept preview saved to: {previewPath}");
            }

            // 2. Generate Multi-Resolution ICO for Concept C
            int[] sizes = new[] { 256, 128, 64, 48, 32, 24, 16 };
            var pngBytesList = new List<byte[]>();

            foreach (int sz in sizes)
            {
                using var bmp = RenderConceptC(sz);
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                byte[] bytes = ms.ToArray();
                pngBytesList.Add(bytes);

                // Save square logos
                if (sz == 256)
                {
                    bmp.Save(Path.Combine(assetsDir, "Square150x150Logo.scale-200.png"), ImageFormat.Png);
                    bmp.Save(Path.Combine(assetsDir, "StoreLogo.png"), ImageFormat.Png);
                }
                else if (sz == 48)
                {
                    bmp.Save(Path.Combine(assetsDir, "Square44x44Logo.scale-200.png"), ImageFormat.Png);
                    bmp.Save(Path.Combine(assetsDir, "Square44x44Logo.targetsize-48_altform-lightunplated.png"), ImageFormat.Png);
                }
                else if (sz == 24)
                {
                    bmp.Save(Path.Combine(assetsDir, "Square44x44Logo.targetsize-24_altform-unplated.png"), ImageFormat.Png);
                }
            }

            // Build Windows ICO file
            using (var icoStream = new MemoryStream())
            using (var bw = new BinaryWriter(icoStream))
            {
                // ICO Header
                bw.Write((ushort)0); // Reserved
                bw.Write((ushort)1); // Type 1 = Icon
                bw.Write((ushort)sizes.Length); // Image count

                int offset = 6 + (sizes.Length * 16);

                for (int i = 0; i < sizes.Length; i++)
                {
                    int sz = sizes[i];
                    byte[] data = pngBytesList[i];

                    bw.Write((byte)(sz == 256 ? 0 : sz)); // Width
                    bw.Write((byte)(sz == 256 ? 0 : sz)); // Height
                    bw.Write((byte)0); // Color count
                    bw.Write((byte)0); // Reserved
                    bw.Write((ushort)1); // Color planes
                    bw.Write((ushort)32); // Bits per pixel
                    bw.Write((uint)data.Length); // Size in bytes
                    bw.Write((uint)offset); // File offset

                    offset += data.Length;
                }

                // Write PNG image payloads
                for (int i = 0; i < sizes.Length; i++)
                {
                    bw.Write(pngBytesList[i]);
                }

                string icoPath = Path.Combine(assetsDir, "AppIcon.ico");
                File.WriteAllBytes(icoPath, icoStream.ToArray());
                Console.WriteLine($"[ICON] AppIcon.ico generated ({icoStream.Length} bytes, {sizes.Length} sizes: 16 to 256px)");
            }
        }

        private static Bitmap RenderConceptA(int size)
        {
            var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = size / 256f;

            using (var bgBrush = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size), Color.FromArgb(241, 245, 249), Color.FromArgb(226, 232, 240)))
            {
                g.FillEllipse(bgBrush, 12 * scale, 12 * scale, 232 * scale, 232 * scale);
            }

            // Left Folder
            using (var fBrush = new SolidBrush(Color.FromArgb(37, 99, 235)))
            using (var tabBrush = new SolidBrush(Color.FromArgb(29, 78, 216)))
            {
                g.FillRectangle(tabBrush, 28 * scale, 56 * scale, 48 * scale, 20 * scale);
                g.FillRectangle(fBrush, 28 * scale, 72 * scale, 92 * scale, 108 * scale);
            }

            // Right NAS
            using (var nasBrush = new SolidBrush(Color.FromArgb(15, 23, 42)))
            using (var bayBrush = new SolidBrush(Color.FromArgb(51, 65, 85)))
            using (var ledGreen = new SolidBrush(Color.FromArgb(16, 185, 129)))
            {
                g.FillRectangle(nasBrush, 142 * scale, 60 * scale, 86 * scale, 126 * scale);
                g.FillRectangle(bayBrush, 152 * scale, 74 * scale, 28 * scale, 88 * scale);
                g.FillRectangle(bayBrush, 188 * scale, 74 * scale, 28 * scale, 88 * scale);
                g.FillEllipse(ledGreen, 162 * scale, 168 * scale, 8 * scale, 8 * scale);
                g.FillEllipse(ledGreen, 198 * scale, 168 * scale, 8 * scale, 8 * scale);
            }

            // Single Direction Arrow
            using (var arrowPen = new Pen(Color.FromArgb(245, 158, 11), 8 * scale) { EndCap = LineCap.ArrowAnchor })
            {
                g.DrawLine(arrowPen, 98 * scale, 124 * scale, 142 * scale, 124 * scale);
            }

            return bmp;
        }

        private static Bitmap RenderConceptB(int size)
        {
            var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = size / 256f;

            // Shield
            using (var shieldBrush = new LinearGradientBrush(new PointF(0, 0), new PointF(0, size), Color.FromArgb(16, 185, 129), Color.FromArgb(5, 150, 105)))
            {
                PointF[] pts = new[]
                {
                    new PointF(128 * scale, 18 * scale),
                    new PointF(230 * scale, 52 * scale),
                    new PointF(205 * scale, 182 * scale),
                    new PointF(128 * scale, 242 * scale),
                    new PointF(51 * scale, 182 * scale),
                    new PointF(26 * scale, 52 * scale)
                };
                g.FillPolygon(shieldBrush, pts);
            }

            // Inner folder
            using (var fBrush = new SolidBrush(Color.White))
            using (var arcPen = new Pen(Color.FromArgb(15, 23, 42), 8 * scale) { EndCap = LineCap.ArrowAnchor })
            {
                g.FillRectangle(fBrush, 64 * scale, 78 * scale, 50 * scale, 18 * scale);
                g.FillRectangle(fBrush, 64 * scale, 94 * scale, 128 * scale, 84 * scale);
                g.DrawArc(arcPen, 100 * scale, 108 * scale, 56 * scale, 56 * scale, 45, 270);
            }

            return bmp;
        }

        private static Bitmap RenderConceptC(int size)
        {
            var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = size / 256f;

            // 1. Dark Rounded Slate Badge Background
            using (var bgBrush = new LinearGradientBrush(new PointF(0, 0), new PointF(0, size), Color.FromArgb(15, 23, 42), Color.FromArgb(30, 41, 59)))
            using (var path = CreateRoundedRectanglePath(10 * scale, 10 * scale, 236 * scale, 236 * scale, 44 * scale))
            using (var borderPen = new Pen(Color.FromArgb(80, 255, 255, 255), 2 * scale))
            {
                g.FillPath(bgBrush, path);
                g.DrawPath(borderPen, path);
            }

            // 2. Amber Fluent Top Folder (Source)
            using (var tabBrush = new LinearGradientBrush(new PointF(0, 36 * scale), new PointF(0, 58 * scale), Color.FromArgb(251, 191, 36), Color.FromArgb(245, 158, 11)))
            using (var tabPath = new GraphicsPath())
            {
                tabPath.AddLines(new[]
                {
                    new PointF(40 * scale, 58 * scale),
                    new PointF(40 * scale, 42 * scale),
                    new PointF(92 * scale, 42 * scale),
                    new PointF(112 * scale, 58 * scale)
                });
                tabPath.CloseFigure();
                g.FillPath(tabBrush, tabPath);
            }

            using (var folderBrush = new LinearGradientBrush(new PointF(0, 56 * scale), new PointF(0, 134 * scale), Color.FromArgb(252, 211, 77), Color.FromArgb(217, 119, 6)))
            using (var fPath = CreateRoundedRectanglePath(36 * scale, 56 * scale, 184 * scale, 76 * scale, 12 * scale))
            {
                g.FillPath(folderBrush, fPath);
            }

            // 3. Bottom NAS Storage Rack
            using (var nasBrush = new LinearGradientBrush(new PointF(0, 160 * scale), new PointF(0, 218 * scale), Color.FromArgb(51, 65, 85), Color.FromArgb(15, 23, 42)))
            using (var nasPath = CreateRoundedRectanglePath(40 * scale, 160 * scale, 176 * scale, 58 * scale, 10 * scale))
            {
                g.FillPath(nasBrush, nasPath);
            }

            // NAS Drive Slots
            using (var slotBrush = new SolidBrush(Color.FromArgb(30, 41, 59)))
            {
                g.FillRectangle(slotBrush, 52 * scale, 170 * scale, 40 * scale, 38 * scale);
                g.FillRectangle(slotBrush, 100 * scale, 170 * scale, 40 * scale, 38 * scale);
                g.FillRectangle(slotBrush, 148 * scale, 170 * scale, 40 * scale, 38 * scale);
            }

            // NAS Status LEDs (Green & Blue)
            using (var ledGreen = new SolidBrush(Color.FromArgb(16, 185, 129)))
            using (var ledBlue = new SolidBrush(Color.FromArgb(59, 130, 246)))
            {
                g.FillEllipse(ledGreen, 198 * scale, 172 * scale, 8 * scale, 8 * scale);
                g.FillEllipse(ledBlue, 198 * scale, 186 * scale, 8 * scale, 8 * scale);
            }

            // 4. Downward Sync Stream Arrow (Cyan Blue)
            using (var arrowPen = new Pen(Color.FromArgb(56, 189, 248), 9 * scale) { StartCap = LineCap.Round, EndCap = LineCap.ArrowAnchor })
            {
                g.DrawLine(arrowPen, 128 * scale, 118 * scale, 128 * scale, 154 * scale);
            }

            return bmp;
        }

        private static GraphicsPath CreateRoundedRectanglePath(float x, float y, float width, float height, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + width - d, y, d, d, 270, 90);
            path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
            path.AddArc(x, y + height - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
