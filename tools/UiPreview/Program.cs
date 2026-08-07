// Offline renderer for the mod's "pill button" chrome (Sts2ModalPanel.BuildPillButton) - lets you check what a
// texture + hsv.gdshader tint + optional nine-slice composite will actually look like at a given button size,
// WITHOUT launching the game. Godot isn't invoked at all; this is a from-scratch port of the exact math (see
// ApplyHsvShader for the shader port, NinePatchComposite for Godot's NinePatchRect scaling algorithm), rendered
// via System.Drawing onto plain PNGs you can open directly.
//
// Usage:
//   dotnet run -- <path-to-reward_item_button.png> [outputDir]
//
// The source PNG isn't checked into the repo (it's the game's own asset, not ours to redistribute) - extract it
// first via GDRE Tools, same as HANDOFF.md's "게임 내부 코드 조사 방법" section describes for scenes:
//   gdre_tools.exe --headless --recover=<game>\SlayTheSpire2.pck --output=<dir> ^
//     --include=res://images/ui/reward_screen/reward_item_button.png.import ^
//     --include=res://.godot/imported/reward_item_button.png*
// (Must include the .png.import path, not the .png path directly - the raw PNG isn't what's stored in the pck,
// only the imported .ctex + .import remap are, and GDRE only converts what you explicitly ask for.)
//
// This exists because a change here (Sts2ModalPanel.BuildPillButton once briefly used NinePatchRect for the
// gear button's corner rounding) broke every pill button in the actual game in a way that wasn't obvious from
// reading the C# alone - see HANDOFF.md's UI reference section for the full story. Re-run this after any change
// to BuildPillButton's background/shader logic and actually look at the output before asking for an in-game test.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: dotnet run -- <path-to-reward_item_button.png> [outputDir]");
    Console.WriteLine("See the comment at the top of Program.cs for how to extract that PNG via GDRE Tools.");
    return 1;
}

string srcPath = args[0];
string outDir = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "preview_output");
if (!File.Exists(srcPath))
{
    Console.Error.WriteLine($"Source texture not found: {srcPath}");
    return 1;
}
Directory.CreateDirectory(outDir);

using var src = new Bitmap(srcPath);
Console.WriteLine($"Source: {src.Width}x{src.Height}");

// Cases mirror what Sts2ModalPanel.BuildPillButton is actually called with right now (RecruitToggleInjector's
// toggle/gear buttons, BanConfirmPanel's cancel/confirm buttons) - update this list when those call sites change.
// ninePatch:true cases are informational only - BuildPillButton currently uses plain Scale stretch, NOT
// NinePatchRect (see the big comment on BuildPillButton for why nine-slicing doesn't work at this button size).
var cases = new (string name, int w, int h, bool ninePatch, float hue, float sat, float val)[]
{
    ("gear_50x50_plainscale_CURRENT", 50, 50, false, 1.0f, 1.0f, 0.9f),
    ("toggle_200x50_plainscale_CURRENT", 200, 50, false, 1.0f, 1.0f, 0.9f),
    ("ban_cancel_140x50_v2", 140, 50, false, 1.0f, 0.5f, 0.85f),
    ("ban_confirm_220x50_v2", 220, 50, false, 0.9f, 0.7f, 0.85f),
    ("gear_50x50_ninepatch_DONT_USE", 50, 50, true, 1.0f, 1.0f, 0.9f),
};

const int ninePatchMargin = 48;
foreach ((string name, int w, int h, bool ninePatch, float hue, float sat, float val) in cases)
{
    using Bitmap composited = ninePatch
        ? NinePatchComposite(src, ninePatchMargin, ninePatchMargin, ninePatchMargin, ninePatchMargin, w, h)
        : PlainScale(src, w, h);
    using Bitmap shaded = ApplyHsvShader(composited, hue, sat, val);
    string outPath = Path.Combine(outDir, name + ".png");
    shaded.Save(outPath, ImageFormat.Png);
    Console.WriteLine($"Saved {outPath}");
}

// Row preview: toggle + gear side by side on a mid-gray backdrop, closest thing to an in-lobby screenshot.
using (Bitmap row = RenderRow(src))
{
    row.Save(Path.Combine(outDir, "row_CURRENT.png"), ImageFormat.Png);
    Console.WriteLine($"Saved {Path.Combine(outDir, "row_CURRENT.png")}");
}

// Hue sweep grid at a couple of (sat,val) combos - useful for picking a NEW color deliberately (the shader
// rotates whatever hue the base texture already has, so numbers borrowed from a different texture's own scene
// don't transfer - see HANDOFF.md). Each row is a fixed (s,v); columns step hue 0..1 in 0.1 increments.
using (Bitmap grid = RenderHueSweep(src))
{
    grid.Save(Path.Combine(outDir, "hue_sweep.png"), ImageFormat.Png);
    Console.WriteLine($"Saved {Path.Combine(outDir, "hue_sweep.png")}");
}
return 0;

static Bitmap RenderHueSweep(Bitmap src)
{
    var svRows = new (float s, float v)[] { (1.0f, 0.9f), (0.7f, 0.85f), (0.5f, 0.8f) };
    int cellW = 90, cellH = 40, cols = 11;
    var canvas = new Bitmap(cols * cellW, svRows.Length * cellH);
    using var g = Graphics.FromImage(canvas);
    g.Clear(Color.FromArgb(255, 60, 60, 60));
    for (int row = 0; row < svRows.Length; row++)
    {
        (float s, float v) = svRows[row];
        for (int col = 0; col < cols; col++)
        {
            float hue = col / (float)(cols - 1);
            using Bitmap swatch = PlainScale(src, cellW - 6, cellH - 6);
            using Bitmap shaded = ApplyHsvShader(swatch, hue, s, v);
            g.DrawImage(shaded, col * cellW + 3, row * cellH + 3);
        }
    }
    return canvas;
}

static Bitmap RenderRow(Bitmap src)
{
    var canvas = new Bitmap(300, 90);
    using var g = Graphics.FromImage(canvas);
    g.Clear(Color.FromArgb(255, 90, 100, 95)); // rough mid-gray stand-in for the lobby background
    using Bitmap toggle = PlainScale(src, 200, 50);
    using Bitmap gear = PlainScale(src, 50, 50);
    using Bitmap toggleShaded = ApplyHsvShader(toggle, 1.0f, 1.0f, 0.9f);
    using Bitmap gearShaded = ApplyHsvShader(gear, 1.0f, 1.0f, 0.9f);
    g.DrawImage(toggleShaded, 10, 10);
    g.DrawImage(gearShaded, 10 + 200 + 4, 10);
    return canvas;
}

// --- HSV shader (exact port of hsv.gdshader's fragment() - see HANDOFF.md for the extracted shader source) ---
static Bitmap ApplyHsvShader(Bitmap bmp, float h, float s, float v)
{
    int w = bmp.Width, ht = bmp.Height;
    var outBmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb);
    double hue = (1.0 - h) * 2 * Math.PI;
    double cosH = Math.Cos(hue), sinH = Math.Sin(hue);

    double[,] rgbToYiq =
    {
        { 0.2989, 0.5959, 0.2115 },
        { 0.5870, -0.2774, -0.5229 },
        { 0.1140, -0.3216, 0.3114 },
    };
    double[,] yiqToRgb = Invert3x3(rgbToYiq);

    BitmapData srcData = bmp.LockBits(new Rectangle(0, 0, w, ht), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    BitmapData dstData = outBmp.LockBits(new Rectangle(0, 0, w, ht), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    unsafe
    {
        for (int y = 0; y < ht; y++)
        {
            byte* srow = (byte*)srcData.Scan0 + y * srcData.Stride;
            byte* drow = (byte*)dstData.Scan0 + y * dstData.Stride;
            for (int x = 0; x < w; x++)
            {
                int i = x * 4;
                byte a = srow[i + 3];
                if (a == 0)
                {
                    drow[i + 0] = srow[i + 0]; drow[i + 1] = srow[i + 1]; drow[i + 2] = srow[i + 2]; drow[i + 3] = 0;
                    continue;
                }
                double r = srow[i + 2] / 255.0, gCh = srow[i + 1] / 255.0, b = srow[i + 0] / 255.0; // BGRA layout
                double Y = rgbToYiq[0, 0] * r + rgbToYiq[0, 1] * gCh + rgbToYiq[0, 2] * b;
                double I = rgbToYiq[1, 0] * r + rgbToYiq[1, 1] * gCh + rgbToYiq[1, 2] * b;
                double Q = rgbToYiq[2, 0] * r + rgbToYiq[2, 1] * gCh + rgbToYiq[2, 2] * b;

                double I2 = I * cosH + Q * sinH;
                double Q2 = -I * sinH + Q * cosH;
                double I3 = I2 * s;
                double Q3 = Q2 * s;
                double Yf = Y * v, If = I3 * v, Qf = Q3 * v;

                double r2 = yiqToRgb[0, 0] * Yf + yiqToRgb[0, 1] * If + yiqToRgb[0, 2] * Qf;
                double g2 = yiqToRgb[1, 0] * Yf + yiqToRgb[1, 1] * If + yiqToRgb[1, 2] * Qf;
                double b2 = yiqToRgb[2, 0] * Yf + yiqToRgb[2, 1] * If + yiqToRgb[2, 2] * Qf;
                r2 = Math.Clamp(r2, 0, 1); g2 = Math.Clamp(g2, 0, 1); b2 = Math.Clamp(b2, 0, 1);

                drow[i + 0] = (byte)(b2 * 255);
                drow[i + 1] = (byte)(g2 * 255);
                drow[i + 2] = (byte)(r2 * 255);
                drow[i + 3] = a;
            }
        }
    }
    bmp.UnlockBits(srcData);
    outBmp.UnlockBits(dstData);
    return outBmp;
}

static double[,] Invert3x3(double[,] m)
{
    double a = m[0, 0], b = m[0, 1], c = m[0, 2];
    double d = m[1, 0], e = m[1, 1], f = m[1, 2];
    double g = m[2, 0], h = m[2, 1], i = m[2, 2];
    double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
    double D = -(b * i - c * h), E = a * i - c * g, F = -(a * h - b * g);
    double G = b * f - c * e, H = -(a * f - c * d), I = a * e - b * d;
    double det = a * A + b * B + c * C;
    return new double[,]
    {
        { A / det, D / det, G / det },
        { B / det, E / det, H / det },
        { C / det, F / det, I / det },
    };
}

// Godot's NinePatchRect scaling algorithm: corners render at native size unless a margin pair exceeds the
// target size on that axis, in which case ALL patches on that axis shrink by the same factor (uniform, not
// per-axis-independent like plain Scale stretch). NOTE: this does NOT reproduce NinePatchRect.GetMinimumSize()
// (which returns marginLeft+marginRight, marginTop+marginBottom) - that's a Godot Control-layout behavior with
// no equivalent in this offline renderer, and it's the actual reason nine-slicing broke BuildPillButton (Godot
// refused to draw it smaller than its margins, so it overflowed every button that used it). Keep that in mind if
// you're tempted to re-enable ninePatch:true above.
static Bitmap NinePatchComposite(Bitmap src, int marginL, int marginT, int marginR, int marginB, int targetW, int targetH)
{
    int sw = src.Width, sh = src.Height;
    var outBmp = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(outBmp);
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

    double scaleX = (marginL + marginR) > targetW ? (double)targetW / (marginL + marginR) : 1.0;
    double scaleY = (marginT + marginB) > targetH ? (double)targetH / (marginT + marginB) : 1.0;
    int dL = (int)Math.Round(marginL * scaleX), dR = (int)Math.Round(marginR * scaleX);
    int dT = (int)Math.Round(marginT * scaleY), dB = (int)Math.Round(marginB * scaleY);

    int srcMidW = sw - marginL - marginR, srcMidH = sh - marginT - marginB;
    int dstMidW = Math.Max(0, targetW - dL - dR), dstMidH = Math.Max(0, targetH - dT - dB);

    var slices = new (int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh)[]
    {
        (0, 0, marginL, marginT, 0, 0, dL, dT),
        (marginL, 0, srcMidW, marginT, dL, 0, dstMidW, dT),
        (sw - marginR, 0, marginR, marginT, targetW - dR, 0, dR, dT),
        (0, marginT, marginL, srcMidH, 0, dT, dL, dstMidH),
        (marginL, marginT, srcMidW, srcMidH, dL, dT, dstMidW, dstMidH),
        (sw - marginR, marginT, marginR, srcMidH, targetW - dR, dT, dR, dstMidH),
        (0, sh - marginB, marginL, marginB, 0, targetH - dB, dL, dB),
        (marginL, sh - marginB, srcMidW, marginB, dL, targetH - dB, dstMidW, dB),
        (sw - marginR, sh - marginB, marginR, marginB, targetW - dR, targetH - dB, dR, dB),
    };
    foreach ((int sx, int sy, int sliceW, int sliceH, int dx, int dy, int dw, int dh) in slices)
    {
        if (sliceW <= 0 || sliceH <= 0 || dw <= 0 || dh <= 0) continue;
        g.DrawImage(src, new Rectangle(dx, dy, dw, dh), new Rectangle(sx, sy, sliceW, sliceH), GraphicsUnit.Pixel);
    }
    return outBmp;
}

static Bitmap PlainScale(Bitmap src, int targetW, int targetH)
{
    var outBmp = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(outBmp);
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.DrawImage(src, new Rectangle(0, 0, targetW, targetH));
    return outBmp;
}
