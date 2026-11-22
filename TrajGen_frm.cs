using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace BTP
{
    // Fabion: генератор простых траекторий (по образцу bioprint_ui/gen.py).
    // Фигуры: решётка прямоугольная, решётка круглая, цилиндр (стенки), спираль (сплошной диск).
    // Результат — стандартный G-код: G90, G0/G1 с F (мм/мин) и накопленным E (ход поршня, мм), T0..T2.
    // Координаты — от центра фигуры (cx, cy) в системе печати; Z — от поверхности подложки.
    public class TrajGen_frm : Form
    {
        //----------------------------------------------------------------- параметры
        public class GenParams
        {
            public int Shape = 0;              // 0 решётка ▭, 1 решётка ○, 2 цилиндр, 3 спираль
            public double Width = 10, Length = 10, Diameter = 10;
            public double Pitch = 1.25;        // шаг филаментов / стенок, мм
            public int Layers = 4;
            public double LayerH = 0.3;
            public int AngleSet = 0;           // 0: 0/90, 1: 0/60/120, 2: 0/45/90/135
            public bool Contour = true;
            public bool Continuous = true;     // решётка одной непрерывной линией
            public int Walls = 2;
            public double Speed = 4, TravelSpeed = 10;   // мм/с
            public double SyringeD = 12.06, NozzleD = 0.6, Flow = 100;
            public double SafeZ = 10;          // высота переходов, мм
            public double Cx = 0, Cy = 0;      // центр фигуры
            public int Tool = -1;              // -1 — текущий шприц (без T), 0..2 — T0..T2
            public bool Home = false;          // G28 в начале

            public static readonly int[][] Angles = { new[] { 0, 90 }, new[] { 0, 60, 120 }, new[] { 0, 45, 90, 135 } };

            public double StrandArea
            {
                get
                {
                    double d = NozzleD, h = LayerH;
                    if (d <= h) return Math.PI * d * d / 4;
                    return (d - h) * h + Math.PI * h * h / 4;
                }
            }
            // ход поршня (мм) на 1 мм пути сопла
            public double EPerMm
            {
                get
                {
                    var a = Math.PI * SyringeD * SyringeD / 4;
                    return a > 0 ? Flow / 100.0 * StrandArea / a : 0;
                }
            }
        }

        //----------------------------------------------------------------- геометрия
        static List<PointF[]> Circle(double r, bool reverse, double seg = 0.5)
        {
            int n = Math.Max(24, (int)Math.Ceiling(2 * Math.PI * r / seg));
            var pts = new PointF[n + 1];
            for (int i = 0; i <= n; i++)
            {
                var a = (reverse ? -1 : 1) * 2 * Math.PI * i / n;
                pts[i] = new PointF((float)(r * Math.Cos(a)), (float)(r * Math.Sin(a)));
            }
            return new List<PointF[]> { pts };
        }

        static bool ClipRect(double px, double py, double dx, double dy, double hw, double hl, out PointF a, out PointF b)
        {
            double t0 = -1e9, t1 = 1e9;
            a = b = PointF.Empty;
            var pc = new[] { px, py }; var dc = new[] { dx, dy }; var lim = new[] { hw, hl };
            for (int k = 0; k < 2; k++)
            {
                if (Math.Abs(dc[k]) < 1e-12) { if (Math.Abs(pc[k]) > lim[k] + 1e-9) return false; continue; }
                double u = (-lim[k] - pc[k]) / dc[k], v = (lim[k] - pc[k]) / dc[k];
                t0 = Math.Max(t0, Math.Min(u, v)); t1 = Math.Min(t1, Math.Max(u, v));
            }
            if (t1 - t0 < 1e-6) return false;
            a = new PointF((float)(px + dx * t0), (float)(py + dy * t0));
            b = new PointF((float)(px + dx * t1), (float)(py + dy * t1));
            return true;
        }

        static bool ClipCircle(double px, double py, double dx, double dy, double r, out PointF a, out PointF b)
        {
            a = b = PointF.Empty;
            var bb = px * dx + py * dy;
            var c = px * px + py * py - r * r;
            var disc = bb * bb - c;
            if (disc <= 1e-9) return false;
            var s = Math.Sqrt(disc);
            double t0 = -bb - s, t1 = -bb + s;
            a = new PointF((float)(px + dx * t0), (float)(py + dy * t0));
            b = new PointF((float)(px + dx * t1), (float)(py + dy * t1));
            return true;
        }

        static List<PointF[]> Hatch(GenParams p, double angleDeg, bool reverse)
        {
            var ang = angleDeg * Math.PI / 180;
            double dx = Math.Cos(ang), dy = Math.Sin(ang);
            double nx = -dy, ny = dx;
            double extent = p.Shape == 0 ? Math.Abs(nx) * p.Width / 2 + Math.Abs(ny) * p.Length / 2 : p.Diameter / 2;
            int k = (int)Math.Floor(extent / p.Pitch + 1e-9);
            var offsets = new List<double>();
            for (int i = -k; i <= k; i++) offsets.Add(i * p.Pitch);
            if (reverse) offsets.Reverse();
            var segs = new List<PointF[]>();
            foreach (var off in offsets)
            {
                PointF a, b;
                bool ok = p.Shape == 0
                    ? ClipRect(nx * off, ny * off, dx, dy, p.Width / 2, p.Length / 2, out a, out b)
                    : ClipCircle(nx * off, ny * off, dx, dy, p.Diameter / 2, out a, out b);
                if (!ok) continue;
                segs.Add(segs.Count % 2 == 1 ? new[] { b, a } : new[] { a, b });   // змейкой
            }
            if (segs.Count == 0 || !p.Continuous) return segs;
            var path = new List<PointF>();
            foreach (var sg in segs) path.AddRange(sg);
            return new List<PointF[]> { path.ToArray() };
        }

        // Линии печати одного слоя (относительно центра фигуры)
        public static List<PointF[]> LayerPaths(GenParams p, int layer)
        {
            bool rev = layer % 2 == 1;
            var paths = new List<PointF[]>();
            if (p.Shape == 0 || p.Shape == 1)
            {
                if (p.Contour)
                {
                    if (p.Shape == 0)
                    {
                        float hw = (float)(p.Width / 2), hl = (float)(p.Length / 2);
                        paths.Add(new[] { new PointF(-hw, -hl), new PointF(hw, -hl), new PointF(hw, hl), new PointF(-hw, hl), new PointF(-hw, -hl) });
                    }
                    else paths.AddRange(Circle(p.Diameter / 2, rev));
                }
                var angs = GenParams.Angles[Math.Max(0, Math.Min(2, p.AngleSet))];
                paths.AddRange(Hatch(p, angs[layer % angs.Length], (layer / angs.Length) % 2 == 1));
            }
            else if (p.Shape == 2)
            {
                var r0 = p.Diameter / 2;
                for (int w = 0; w < Math.Max(1, p.Walls); w++)
                {
                    var r = r0 - w * p.Pitch;
                    if (r <= p.Pitch / 2) break;
                    paths.AddRange(Circle(r, rev));
                }
            }
            else
            {
                var r0 = p.Diameter / 2;
                if (p.Contour) { paths.AddRange(Circle(r0, rev)); r0 -= p.Pitch; }
                var pts = new List<PointF>();
                double th = 0, r = r0;
                while (r > p.Pitch / 2)
                {
                    pts.Add(new PointF((float)(r * Math.Cos(th)), (float)(r * Math.Sin(th))));
                    th += Math.Min(0.5 / Math.Max(r, 0.5), Math.PI / 12);
                    r = r0 - p.Pitch * th / (2 * Math.PI);
                }
                if (pts.Count > 1) { if (rev) pts.Reverse(); paths.Add(pts.ToArray()); }
            }
            return paths;
        }

        static string Fm(double v)
        {
            var s = v.ToString("0.###", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        public static string Generate(GenParams p)
        {
            var sb = new StringBuilder();
            var names = new[] { "Решётка прямоугольная", "Решётка круглая", "Цилиндр", "Спираль" };
            double fp = p.Speed * 60, ft = p.TravelSpeed * 60, epm = p.EPerMm;
            double top = p.LayerH * p.Layers;
            double zTravel = Math.Max(p.SafeZ, top + 2);
            sb.AppendLine("; " + names[p.Shape] + "; слоёв " + p.Layers + " x " + Fm(p.LayerH) + " мм, шаг " + Fm(p.Pitch) + " мм, скорость " + Fm(p.Speed) + " мм/с");
            sb.AppendLine("; шприц " + Fm(p.SyringeD) + " мм, сопло " + Fm(p.NozzleD) + " мм, подача " + Fm(p.Flow) + " % -> E " + epm.ToString("0.000000", CultureInfo.InvariantCulture) + " на 1 мм пути");
            sb.AppendLine("; центр X" + Fm(p.Cx) + " Y" + Fm(p.Cy) + ", переходы на Z" + Fm(zTravel));
            if (p.Home) sb.AppendLine("G28");
            sb.AppendLine("G90");
            sb.AppendLine("M82");
            if (p.Tool >= 0) sb.AppendLine("T" + p.Tool);
            sb.AppendLine("G92 E0");
            sb.AppendLine("G0 Z" + Fm(zTravel) + " F" + Fm(ft));

            double e = 0;
            double? x = null, y = null;
            for (int L = 0; L < p.Layers; L++)
            {
                var z = p.LayerH * (L + 1);
                bool first = true;
                foreach (var path in LayerPaths(p, L))
                {
                    if (path.Length < 2) continue;
                    double sx = path[0].X + p.Cx, sy = path[0].Y + p.Cy;
                    if (x == null)
                    {
                        sb.AppendLine("G0 X" + Fm(sx) + " Y" + Fm(sy) + " F" + Fm(ft));
                        sb.AppendLine("G1 Z" + Fm(z) + " F" + Fm(ft));
                    }
                    else
                    {
                        if (first) sb.AppendLine("G1 Z" + Fm(z) + " F" + Fm(ft));
                        if (Math.Abs(sx - x.Value) > 1e-6 || Math.Abs(sy - y.Value) > 1e-6)
                            sb.AppendLine("G0 X" + Fm(sx) + " Y" + Fm(sy) + " F" + Fm(ft));
                    }
                    first = false;
                    x = sx; y = sy;
                    for (int i = 1; i < path.Length; i++)
                    {
                        double nx = path[i].X + p.Cx, ny = path[i].Y + p.Cy;
                        var d = Math.Sqrt((nx - x.Value) * (nx - x.Value) + (ny - y.Value) * (ny - y.Value));
                        if (d < 1e-6) continue;
                        e += d * epm;
                        sb.AppendLine("G1 X" + Fm(nx) + " Y" + Fm(ny) + " E" + e.ToString("0.00000", CultureInfo.InvariantCulture) + " F" + Fm(fp));
                        x = nx; y = ny;
                    }
                }
            }
            sb.AppendLine("G0 Z" + Fm(zTravel) + " F" + Fm(ft));
            return sb.ToString();
        }

        //----------------------------------------------------------------- окно
        readonly Program_frm programForm;
        readonly GenParams prm = new GenParams();

        ComboBox cbShape, cbAngles, cbTool;
        NumericUpDown nWidth, nLength, nDiam, nPitch, nLayers, nLayerH, nWalls, nSpeed, nTravel, nSyrD, nNozD, nFlow, nSafeZ, nCx, nCy, nPreviewLayer;
        CheckBox chContour, chContinuous, chHome;
        Panel preview;
        Label lblInfo;
        string lastGcode = "";

        public TrajGen_frm(Program_frm programForm)
        {
            this.programForm = programForm;
            Text = "Генератор траекторий";
            Size = new Size(1100, 760);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 10f);

            var left = new TableLayoutPanel { Dock = DockStyle.Left, Width = 380, ColumnCount = 2, AutoScroll = true, Padding = new Padding(8) };
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

            cbShape = Combo(new[] { "Решётка прямоугольная", "Решётка круглая", "Цилиндр", "Спираль (диск)" }, 0);
            cbAngles = Combo(new[] { "0/90", "0/60/120", "0/45/90/135" }, 0);
            cbTool = Combo(new[] { "Текущий (без T)", "Шприц 1 (T0)", "Шприц 2 (T1)", "Шприц 3 (T2)" }, 0);
            nWidth = Num(1, 200, 10, 1); nLength = Num(1, 200, 10, 1); nDiam = Num(1, 200, 10, 1);
            nPitch = Num(0.1m, 20, 1.25m, 2); nLayers = Num(1, 500, 4, 0); nLayerH = Num(0.01m, 5, 0.3m, 2);
            nWalls = Num(1, 50, 2, 0);
            nSpeed = Num(0.1m, 100, 4, 1); nTravel = Num(0.1m, 100, 10, 1);
            nSyrD = Num(0.1m, 50, 12.06m, 2); nNozD = Num(0.05m, 5, 0.6m, 2); nFlow = Num(1, 500, 100, 0);
            nSafeZ = Num(0, 200, 10, 1); nCx = Num(-500, 500, 0, 2); nCy = Num(-500, 500, 0, 2);
            nPreviewLayer = Num(0, 500, 0, 0);
            chContour = new CheckBox { Text = "Контур", Checked = true, AutoSize = true };
            chContinuous = new CheckBox { Text = "Одной линией", Checked = true, AutoSize = true };
            chHome = new CheckBox { Text = "G28 в начале", Checked = false, AutoSize = true };

            Row(left, "Фигура", cbShape);
            Row(left, "Ширина X, мм", nWidth);
            Row(left, "Длина Y, мм", nLength);
            Row(left, "Диаметр, мм", nDiam);
            Row(left, "Шаг, мм", nPitch);
            Row(left, "Углы слоёв", cbAngles);
            Row(left, "Стенок (цилиндр)", nWalls);
            Row(left, "Слоёв", nLayers);
            Row(left, "Высота слоя, мм", nLayerH);
            Row(left, "Скорость печати, мм/с", nSpeed);
            Row(left, "Скорость переходов, мм/с", nTravel);
            Row(left, "Шприц", cbTool);
            Row(left, "Диаметр шприца, мм", nSyrD);
            Row(left, "Диаметр сопла, мм", nNozD);
            Row(left, "Подача, %", nFlow);
            Row(left, "Высота переходов, мм", nSafeZ);
            Row(left, "Центр X, мм", nCx);
            Row(left, "Центр Y, мм", nCy);
            Row(left, "", chContour);
            Row(left, "", chContinuous);
            Row(left, "", chHome);
            Row(left, "Слой в превью (0 — все)", nPreviewLayer);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(8), FlowDirection = FlowDirection.LeftToRight };
            var bToProg = new Button { Text = "В окно «Программа»", AutoSize = true, Height = 40 };
            var bSave = new Button { Text = "Сохранить .gcode", AutoSize = true, Height = 40 };
            var bRun = new Button { Text = "Загрузить и запустить", AutoSize = true, Height = 40, BackColor = Color.YellowGreen };
            bottom.Controls.AddRange(new Control[] { bToProg, bSave, bRun });

            lblInfo = new Label { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8, 4, 8, 0) };
            preview = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Color.White };
            preview.Paint += Preview_Paint;
            preview.Resize += (s, e) => preview.Invalidate();

            Controls.Add(preview);
            Controls.Add(lblInfo);
            Controls.Add(bottom);
            Controls.Add(left);

            foreach (Control c in left.Controls)
            {
                if (c is NumericUpDown) ((NumericUpDown)c).ValueChanged += (s, e) => Regenerate();
                if (c is ComboBox) ((ComboBox)c).SelectedIndexChanged += (s, e) => Regenerate();
                if (c is CheckBox) ((CheckBox)c).CheckedChanged += (s, e) => Regenerate();
            }
            bToProg.Click += (s, e) => { if (programForm != null && !programForm.IsDisposed) programForm.SetGCode(lastGcode, "Генератор: " + cbShape.Text); };
            bSave.Click += (s, e) => SaveGcode();
            bRun.Click += async (s, e) =>
            {
                if (ConnectionData.Value == null || !ConnectionData.Value.client.Connected) { MessageBox.Show("Нет связи со шлюзом"); return; }
                if (MessageBox.Show("Загрузить программу в шлюз и запустить печать?", "Генератор", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                if (programForm != null && !programForm.IsDisposed) programForm.SetGCode(lastGcode, "Генератор: " + cbShape.Text);
                await ConnectionData.Value.upload_program(lastGcode.Split('\n'));
                await ConnectionData.Value.start_program();
            };
            Regenerate();
        }

        class DoubleBufferedPanel : Panel { public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; } }

        static ComboBox Combo(string[] items, int sel)
        {
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            cb.Items.AddRange(items);
            cb.SelectedIndex = sel;
            return cb;
        }
        static NumericUpDown Num(decimal min, decimal max, decimal val, int dec)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Value = val, DecimalPlaces = dec, Increment = dec == 0 ? 1 : (decimal)Math.Pow(10, -Math.Min(dec, 1)), Dock = DockStyle.Fill };
        }
        static void Row(TableLayoutPanel t, string caption, Control c)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) });
            t.Controls.Add(c);
        }

        void ReadParams()
        {
            prm.Shape = cbShape.SelectedIndex;
            prm.Width = (double)nWidth.Value; prm.Length = (double)nLength.Value; prm.Diameter = (double)nDiam.Value;
            prm.Pitch = (double)nPitch.Value; prm.Layers = (int)nLayers.Value; prm.LayerH = (double)nLayerH.Value;
            prm.AngleSet = cbAngles.SelectedIndex; prm.Walls = (int)nWalls.Value;
            prm.Speed = (double)nSpeed.Value; prm.TravelSpeed = (double)nTravel.Value;
            prm.SyringeD = (double)nSyrD.Value; prm.NozzleD = (double)nNozD.Value; prm.Flow = (double)nFlow.Value;
            prm.SafeZ = (double)nSafeZ.Value; prm.Cx = (double)nCx.Value; prm.Cy = (double)nCy.Value;
            prm.Tool = cbTool.SelectedIndex - 1;
            prm.Contour = chContour.Checked; prm.Continuous = chContinuous.Checked; prm.Home = chHome.Checked;
            nWidth.Enabled = nLength.Enabled = prm.Shape == 0;
            nDiam.Enabled = prm.Shape != 0;
            cbAngles.Enabled = chContinuous.Enabled = prm.Shape <= 1;
            nWalls.Enabled = prm.Shape == 2;
            chContour.Enabled = prm.Shape != 2;
            nPreviewLayer.Maximum = prm.Layers;
        }

        void Regenerate()
        {
            ReadParams();
            lastGcode = Generate(prm);
            // длина пути и время
            double len = 0, e = 0;
            for (int L = 0; L < prm.Layers; L++)
                foreach (var path in LayerPaths(prm, L))
                    for (int i = 1; i < path.Length; i++)
                    {
                        var dx = path[i].X - path[i - 1].X; var dy = path[i].Y - path[i - 1].Y;
                        len += Math.Sqrt(dx * dx + dy * dy);
                    }
            e = len * prm.EPerMm;
            var vol_ul = e * Math.PI * prm.SyringeD * prm.SyringeD / 4;   // мм³ = мкл
            var t_min = prm.Speed > 0 ? len / prm.Speed / 60 : 0;
            var lines = lastGcode.Split('\n').Length;
            lblInfo.Text = string.Format(CultureInfo.InvariantCulture,
                "Путь печати {0:0.0} мм, ход поршня {1:0.000} мм, объём {2:0.0} мкл, время печати ≈ {3:0.0} мин, строк G-кода {4}",
                len, e, vol_ul, t_min, lines);
            preview.Invalidate();
        }

        void Preview_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);
            double half = prm.Shape == 0 ? Math.Max(prm.Width, prm.Length) / 2 : prm.Diameter / 2;
            half = Math.Max(half, 1) * 1.15;
            float w = preview.ClientSize.Width, h = preview.ClientSize.Height;
            float scale = (float)(Math.Min(w, h) / (2 * half));
            Func<PointF, PointF> map = p => new PointF(w / 2 + p.X * scale, h / 2 - p.Y * scale);

            using (var axis = new Pen(Color.LightGray, 1))
            {
                g.DrawLine(axis, 0, h / 2, w, h / 2);
                g.DrawLine(axis, w / 2, 0, w / 2, h);
            }
            int only = (int)nPreviewLayer.Value;
            var colors = new[] { Color.SteelBlue, Color.IndianRed, Color.SeaGreen, Color.DarkOrange };
            for (int L = 0; L < prm.Layers; L++)
            {
                if (only > 0 && L != only - 1) continue;
                var col = colors[L % colors.Length];
                using (var pen = new Pen(Color.FromArgb(only > 0 ? 255 : 140, col), Math.Max(1f, (float)(prm.NozzleD * scale * 0.6))))
                {
                    pen.LineJoin = LineJoin.Round;
                    foreach (var path in LayerPaths(prm, L))
                        if (path.Length >= 2) g.DrawLines(pen, path.Select(map).ToArray());
                }
            }
            using (var f = new Font("Segoe UI", 9f))
                g.DrawString("Вид сверху. Перекрестие — центр фигуры (X" + Fm(prm.Cx) + " Y" + Fm(prm.Cy) + ")", f, Brushes.Gray, 6, 6);
        }

        void SaveGcode()
        {
            using (var dlg = new SaveFileDialog { Filter = "G-код (*.gcode)|*.gcode|Все файлы|*.*", FileName = "traj.gcode" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dlg.FileName, lastGcode);
            }
        }
    }
}
