using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Connection;

namespace BTP
{
    // Канал к биопринтеру Fabion через шлюз tcp_to_udp_fabion (TCP 127.0.0.1:62000).
    // Повторяет набор методов ChanelOct / ChanelOctKlipper, чтобы окна интерфейса работали без переделки.
    //
    // Оси (номер = индекс мотора на Octopus):
    //   0 X, 1 Y, 2 Z1, 3 Z2, 4 Z3, 5 E1, 6 E2, 7 E3.  Шприцы: 0..2.
    //
    // Команды шлюза — "main M...", команды платы — "num1 M...":
    //   main M598 0 / M596 <строка G-кода> / M597 0 — загрузить и запустить программу
    //   main M700           — полная остановка
    //   main M620 n         — сделать активным шприц n (неактивный Z уходит в парковку)
    //   main M621           — поднять все Z в парковку
    //   main M612 x y z e mx my mz me — ноль системы MANUAL для активного шприца
    //   main M631           — автокалибровка сопла по лазерам
    //   main M640 n / M641 n — текущая / целевая СК (0 MANUAL, 1 AUTO, 2 CURRENT, 3 INTERNAL)
    //   main M610 v / M617 % / M618 % — скорость перемещений, коэффициенты экструзии и скорости
    //   num1 M587 I<ось> V<мм/с> / S<мм отн.> / P<мм абс.> / H — движение одного мотора
    //   num1 M589 X12       — базирование всех осей (Z1..Z3, затем X, Y);  M589 S — стоп моторов
    //   num1 M577 I<0..2> V<значение> — сервопривод захвата
    //   num1 M581 I<канал> A<0..255>  — ШИМ-выход (вентилятор, свет)
    //   num1 M579 N<канал> E<0/1> T<°C> — нагрев/охлаждение (логика на Octopus — позже)
    class ChanelGateway
    {
        public const int AXIS_X = 0, AXIS_Y = 1, AXIS_Z1 = 2, AXIS_Z2 = 3, AXIS_Z3 = 4, AXIS_E1 = 5, AXIS_E2 = 6, AXIS_E3 = 7;
        public const int AXES = 8;
        public const int TOOLS = 3;

        public GatewayDevice device;
        public GatewayClient client;

        //---- совместимость со старым кодом ACS: номера осей = индексы моторов
        public int ACSC_BUFFER_0 { get; set; }
        public int ACSC_BUFFER_1 { get; set; }
        public int ACSC_BUFFER_2 { get; set; }
        public int ACSC_BUFFER_3 { get; set; }
        public int ACSC_BUFFER_4 { get; set; }
        public int ACSC_BUFFER_5 { get; set; }
        public int ACSC_BUFFER_6 { get; set; }
        public int ACSC_BUFFER_7 { get; set; }
        public int ACSC_BUFFER_8 { get; set; }
        public int ACSC_BUFFER_9 { get; set; }
        public int ACSC_AXIS_0 { get; set; } = 0;
        public int ACSC_AXIS_1 { get; set; } = 1;
        public int ACSC_AXIS_2 { get; set; } = 2;
        public int ACSC_AXIS_3 { get; set; } = 3;
        public int ACSC_AXIS_4 { get; set; } = 4;
        public int ACSC_AXIS_5 { get; set; } = 5;
        public int ACSC_AXIS_6 { get; set; } = 6;
        public int ACSC_AXIS_7 { get; set; } = 7;
        // осей 8..16 на Fabion нет — команды по ним игнорируются
        public int ACSC_AXIS_8 { get; set; } = -1;
        public int ACSC_AXIS_9 { get; set; } = -1;
        public int ACSC_AXIS_10 { get; set; } = -1;
        public int ACSC_AXIS_11 { get; set; } = -1;
        public int ACSC_AXIS_12 { get; set; } = -1;
        public int ACSC_AXIS_13 { get; set; } = -1;
        public int ACSC_AXIS_14 { get; set; } = -1;
        public int ACSC_AXIS_15 { get; set; } = -1;
        public int ACSC_AXIS_16 { get; set; } = -1;
        public int ACSC_NONE { get; internal set; } = -1;
        public object ACSC_SAFETY_LL { get; internal set; } = 0;
        public object ACSC_SAFETY_RL { get; internal set; } = 0;
        public int ACSC_MST_MOVE { get; internal set; } = 0;
        public object ACSC_SOCKET_STREAM_PORT { get; internal set; } = 0;
        public object ACSC_AMF_VELOCITY { get; internal set; } = 2;
        public object ACSC_PST_RUN { get; internal set; } = true;
        public object ACSC_PST_COMPILED { get; internal set; } = true;
        public int ACSC_NEGATIVE_DIRECTION { get; internal set; } = -1;
        public int ACSC_POSITIVE_DIRECTION { get; internal set; } = 1;
        public object ACSC_AMF_RELATIVE { get; internal set; } = 1;

        // скорости ручного управления, мм/мин (как в ChanelOct)
        public double xy_vel;
        public double z_vel;
        public double e_vel;
        readonly double[] axis_vel = new double[AXES];   // мм/с, заданные SetVelocity

        public string[] prog;
        public bool prog_loaded;
        public int active_disp = 0;

        // флаги, которые шлюз не хранит — отправляются при подключении и перед стартом
        public bool AutoCoordSystem = false;   // true — печать в системе AUTO (после калибровки по лазерам)
        public int SpeedFactor = 100;          // %
        public int ExtrusionFactor = 100;      // %

        static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

        public ChanelGateway(string host, int port)
        {
            client = new GatewayClient(host, port);
            device = new GatewayDevice(this);
            xy_vel = 600;
            z_vel = 300;
            e_vel = 60;
            for (int i = 0; i < AXES; i++) axis_vel[i] = 0;
        }

        public bool Connect()
        {
            if (!client.Connect()) return false;
            SendSavedFlags();
            return true;
        }

        public void Send(string cmd) { client.Send(cmd); }

        public GatewayState State { get { return client.State; } }

        static bool AxisOk(int axis) { return axis >= 0 && axis < AXES; }

        double VelFor(int axis)
        {
            if (AxisOk(axis) && axis_vel[axis] > 0) return axis_vel[axis];
            if (axis == AXIS_X || axis == AXIS_Y) return xy_vel / 60.0;
            if (axis >= AXIS_Z1 && axis <= AXIS_Z3) return z_vel / 60.0;
            return e_vel / 60.0;
        }

        //================================================================ настройки
        public void SendSavedFlags()
        {
            client.Send("main M641 " + (AutoCoordSystem ? 1 : 0));
            client.Send("main M618 " + SpeedFactor);
            client.Send("main M617 " + ExtrusionFactor);
            client.Send("main M610 " + F(xy_vel / 60.0));
        }
        public void SetSpeedFactor(int percent) { SpeedFactor = percent; client.Send("main M618 " + percent); }
        public void SetExtrusionFactor(int percent) { ExtrusionFactor = percent; client.Send("main M617 " + percent); }
        public void SetAutoCoordSystem(bool auto) { AutoCoordSystem = auto; client.Send("main M641 " + (auto ? 1 : 0)); }
        public void ReloadGatewaySettings() { client.Send("main M614"); }   // перечитать settings_printer.json

        //================================================================ движение
        public void HomeAll()
        {
            client.Send("num1 M589 X12");
        }

        public void HomeDisp(int axis)
        {
            if (!AxisOk(axis)) return;
            client.Send("num1 M587 I" + axis + " H");
        }

        // Относительное перемещение одной оси (мм)
        internal void ToPoint(object move_type, int Axis, double v)
        {
            if (!AxisOk(Axis)) return;
            client.Send("num1 M587 I" + Axis + " V" + F(VelFor(Axis)));
            client.Send("num1 M587 I" + Axis + " S" + F(v));
        }
        internal void ToPoint(object move_type, int Axis, int v) { ToPoint(move_type, Axis, (double)v); }

        // Движение, пока кнопка нажата (остановка — Kill)
        internal void Jog(int Axis, double veloc)
        {
            if (!AxisOk(Axis) || veloc == 0) return;
            client.Send("num1 M587 I" + Axis + " V" + F(Math.Abs(veloc)));
            client.Send("num1 M587 I" + Axis + " S" + (veloc > 0 ? "10000" : "-10000"));   // мягкие концевики ограничат ход
        }
        internal void Jog(object type, int Axis, double veloc) { Jog(Axis, veloc); }

        internal void JogM(object type, int[] axes, int[] directions, double vel)
        {
            if (axes == null || directions == null) return;
            for (int i = 0; i < axes.Length && i < directions.Length; i++)
                if (AxisOk(axes[i])) Jog(axes[i], directions[i] * Math.Abs(vel));
        }

        internal void Kill(int axis)
        {
            if (!AxisOk(axis)) return;
            client.Send("num1 M587 I" + axis + " S0");
        }
        internal void KillM(int[] axes) { if (axes != null) foreach (var a in axes) Kill(a); }
        internal void HaltM(int[] axes) { KillM(axes); }
        internal void KillAll() { client.Send("main M700"); }
        internal void EndSequenceM(int[] ax) { }

        internal void ToPointM(int v, int[] axes, double[] point)
        {
            if (axes == null || point == null) return;
            for (int i = 0; i < axes.Length && i < point.Length; i++)
            {
                if (!AxisOk(axes[i])) continue;
                client.Send("num1 M587 I" + axes[i] + " V" + F(VelFor(axes[i])));
                if (v == 0) client.Send("num1 M587 I" + axes[i] + " P" + F(point[i]));   // абсолютное, машинные мм
                else client.Send("num1 M587 I" + axes[i] + " S" + F(point[i]));
            }
        }
        internal void ExtToPoint(object type, int axis, double v, double vel1, double vel2)
        {
            if (!AxisOk(axis)) return;
            client.Send("num1 M587 I" + axis + " V" + F(Math.Abs(vel1)));
            client.Send("num1 M587 I" + axis + " S" + F(v));
        }
        internal void ExtToPointM(object type, int[] ax, double[] coords, object value1, object value2) { ToPointM(0, ax, coords); }

        public void SetVelocity(int Axis, double Value)
        {
            if (!AxisOk(Axis)) return;
            axis_vel[Axis] = Value;
            client.Send("num1 M587 I" + Axis + " V" + F(Value));
        }
        internal void SetVelocityImm(int Axis, double v) { SetVelocity(Axis, v); }

        // Машинная координата оси, мм (по телеметрии)
        public double GetFPosition(int Axis)
        {
            if (!AxisOk(Axis)) return 0;
            return client.State.MotorMm[Axis];
        }
        internal void SetFPosition(int axis, double v) { }
        internal void SetRPosition(int axis, double v) { }
        internal object GetVelocity(int axis) { return 0.0; }
        internal object GetFVelocity(int axis) { return 0.0; }
        public int GetMotorState(int Axis) { return client.State.MotorsMoving ? 1 : 0; }
        internal void WaitMotionEnd(int axis, int timeout_ms)
        {
            var t0 = DateTime.Now;
            Thread.Sleep(100);
            while (client.State.MotorsMoving && (DateTime.Now - t0).TotalMilliseconds < timeout_ms) Thread.Sleep(20);
        }

        //================================================================ шприцы
        internal void setActiveDisp(int num)
        {
            if (num < 0 || num >= TOOLS) return;
            active_disp = num;
            client.Send("main M620 " + num);
        }
        public void ParkAllZ() { client.Send("main M621"); }

        // Ноль системы MANUAL для активного шприца: текущее положение = 0
        public void zeroDisp(int axis)
        {
            client.Send("main M612 0 0 0 0 1 1 1 0");
        }
        public void calibrateDisp() { client.Send("main M631"); }

        public void SetServo(int index, int value)
        {
            if (index < 0 || index >= TOOLS) return;
            client.Send("num1 M577 I" + index + " V" + value);
        }

        // Сервопривод по углу 0..180°. Плата формирует импульс в тактах прерывания управления
        // (~104 616 тактов в секунду, период 20 мс = 2000 тактов), поэтому угол → длительность импульса → такты.
        // Типовой сервопривод: 0° = 500 мкс, 180° = 2500 мкс. Если ваш ходит не на весь угол — поправьте эти два числа.
        public const double SERVO_MIN_US = 500;
        public const double SERVO_MAX_US = 2500;
        public const double SERVO_TICKS_PER_SEC = 104616.18;
        public void SetServoAngle(int index, int angle)
        {
            angle = Math.Max(0, Math.Min(180, angle));
            var us = SERVO_MIN_US + (SERVO_MAX_US - SERVO_MIN_US) * angle / 180.0;
            var ticks = (int)Math.Round(us * SERVO_TICKS_PER_SEC / 1e6);
            SetServo(index, ticks);
        }

        // channel: 0 — стол, 1..3 — шприцы. temp = 0 — выключить
        public void SetTemperature(int channel, double temp)
        {
            client.Send("num1 M579 N" + channel + " E" + (temp != 0 ? 1 : 0) + " T" + F(temp));
        }

        //================================================================ периферия
        internal void fan_on(int num) { client.Send("num1 M581 I" + num + " A255"); }
        internal void fan_off(int num) { client.Send("num1 M581 I" + num + " A0"); }
        public void SetPwm(int num, int value) { client.Send("num1 M581 I" + num + " A" + Math.Max(0, Math.Min(255, value))); }

        //================================================================ программа
        public async Task upload_program(string[] prog)
        {
            if (prog == null) return;
            var lines = new List<string>();
            foreach (var raw in prog)
            {
                var l = raw;
                var sc = l.IndexOf(';'); if (sc >= 0) l = l.Substring(0, sc);
                l = l.Replace("\r", "").Trim();
                if (l.Length > 0) lines.Add(l);
            }
            this.prog = lines.ToArray();
            prog_loaded = false;
            var cmds = new List<string> { "main M598 0" };
            foreach (var l in lines) cmds.Add("main M596 " + l);
            client.SendMany(cmds);
            // ждём, пока очередь уйдёт в шлюз
            await Task.Run(() => { while (client.QueueLength > 0 && client.Connected) Thread.Sleep(10); });
        }

        public Task start_program()
        {
            SendSavedFlags();
            client.Send("main M597 0");
            return Task.FromResult(0);
        }

        // Паузы в шлюзе пока нет — останавливаем полностью
        public Task pause_program()
        {
            client.Send("main M700");
            return Task.FromResult(0);
        }

        // Выполнить несколько строк G-кода сразу (консоль, генератор). Программа в шлюзе заменяется.
        public void RunLines(IEnumerable<string> lines)
        {
            var cmds = new List<string> { "main M598 0" };
            foreach (var l in lines) if (!string.IsNullOrWhiteSpace(l)) cmds.Add("main M596 " + l.Trim());
            client.SendMany(cmds);
            SendSavedFlags();
            client.Send("main M597 0");
        }

        public bool IsBusy
        {
            get { var s = client.State; return s.ProgramRunning || s.MotorsMoving || s.Waiting || s.WaitingTemp; }
        }

        internal object GetProgramState(int buffer) { return IsBusy; }
        public int get_cur_line(int cur_byte) { return 0; }
        internal object UploadBuffer(int buffer) { return prog == null ? "" : string.Join("\n", prog); }

        public void RunBuffer(int Buffer) { }
        internal void StopBuffer(int b) { client.Send("main M700"); }
        internal void SuspendBuffer(int b) { client.Send("main M700"); }
        internal void ClearBuffer(int b, int v1, int v2) { client.Send("main M598 0"); prog = null; }
        internal void LoadBuffer(int v, string acs_code) { }
        internal void CompileBuffer(int b) { }
        internal void AppendBuffer(int b, string v) { }
        internal void LoadBuffersFromFile(string macrofile) { }

        //================================================================ совместимость ACS (заглушки)
        public void WriteVariable(object Value, string Variable, int nBuf) { }
        public dynamic ReadVariable(string Variable, int nBuf) { return 0.0; }
        internal double ReadVariable(string v1, int aCSC_NONE, int v2, int v3) { return 0.0; }
        internal void SetOutput(int port, int bit, int value) { }
        internal void DisableAll() { client.Send("num1 M587 F"); }
        internal void CancelOperation() { }
        internal void CloseMessageBuffer() { }
        internal void CloseComm() { client.Close(); }
        internal long GetMotorError(int p) { return 0; }
        internal long GetMotionError(int p) { return 0; }
        internal long GetProgramError(int p) { return 0; }
        internal string GetErrorString(int v) { return ""; }
        internal string GetSingleMessage(int v) { return ""; }

        public void startAutoPos() { }
        public void stopAutoPos() { }
        public void enableExtrud() { }

        //================================================================ данные для окон
        // Заполняет ConnectionData по телеметрии (окна читают оттуда в Reload)
        public void UpdateConnectionData()
        {
            var s = client.State;
            ConnectionData.FeedBackX = s.PosTool[0];
            ConnectionData.FeedBackY = s.PosTool[1];
            ConnectionData.FeedBackZ1 = s.MotorMm[AXIS_Z1];
            ConnectionData.FeedBackZ2 = s.MotorMm[AXIS_Z2];
            ConnectionData.FeedBackZ3 = s.MotorMm[AXIS_Z3];
            // у каждой оси один концевик (по направлению базирования)
            ConnectionData.XRLimit = s.Endstops[AXIS_X];
            ConnectionData.YRLimit = s.Endstops[AXIS_Y];
            ConnectionData.ZS1RLimit = s.Endstops[AXIS_Z1];
            ConnectionData.ZS2RLimit = s.Endstops[AXIS_Z2];
            ConnectionData.ZS3RLimit = s.Endstops[AXIS_Z3];
            ConnectionData.bConnected = client.Connected;
            active_disp = s.ActiveTool;
        }

        public string StatusText()
        {
            var s = client.State;
            if (!client.Connected) return "Нет связи со шлюзом";
            if ((DateTime.Now - s.LastUpdate).TotalSeconds > 2) return "Шлюз на связи, плата молчит";
            var sb = new StringBuilder();
            sb.Append("Шприц ").Append(s.ActiveTool + 1).Append("  СК ").Append(s.CoordSystem);
            sb.Append(s.ProgramRunning ? "  печать" : s.MotorsMoving ? "  движение" : "  стоит");
            if (s.HomingStage > 0) sb.Append("  базирование");
            if (s.WaitingTemp) sb.Append("  ждёт температуру");
            sb.Append("  T=").Append(s.Temperature.ToString("0.0", CultureInfo.InvariantCulture));
            sb.Append("  лазеры ").Append(s.Lasers[0] ? "1" : "0").Append(s.Lasers[1] ? "1" : "0");
            return sb.ToString();
        }
    }

    // Объект device — для мест, где окна шлют G-код напрямую (ConnectionData.Value.device.sendCommand)
    class GatewayDevice
    {
        readonly ChanelGateway ch;
        public GatewayDevice(ChanelGateway ch) { this.ch = ch; }

        public void sendCommand(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return;
            var c = cmd.Trim();
            var up = c.ToUpperInvariant();
            if (up.StartsWith("MAIN ") || up.StartsWith("NUM1 ")) { ch.Send(c); return; }
            var w = up.Split(' ');
            switch (w[0])
            {
                case "M410": case "M112": ch.KillAll(); return;
                case "G28": ch.HomeAll(); return;
                case "M84": case "M18":
                    if (up.Contains("E")) { for (int i = 5; i < 8; i++) ch.Send("num1 M587 I" + i + " Q-1"); }
                    else ch.Send("num1 M587 F");
                    return;
                case "M17": ch.Send("num1 M587 W"); return;
                case "M106":
                    {
                        int s = 255, p = 0;
                        foreach (var t in w) { if (t.StartsWith("S")) int.TryParse(t.Substring(1), out s); if (t.StartsWith("P")) int.TryParse(t.Substring(1), out p); }
                        ch.SetPwm(p, s); return;
                    }
                case "M107":
                    {
                        int p = 0;
                        foreach (var t in w) if (t.StartsWith("P")) int.TryParse(t.Substring(1), out p);
                        ch.SetPwm(p, 0); return;
                    }
                case "M29": case "M154": case "M302": case "M27": return;   // от Marlin/Klipper, шлюзу не нужны
            }
            // остальной G-код (G0/G1/G2/G3/G4/G92/T…) — выполнить как программу из одной строки
            if (w[0].StartsWith("G") || w[0].StartsWith("T") || w[0].StartsWith("M"))
            {
                ch.RunLines(new[] { c });
                return;
            }
            Console.WriteLine("Шлюз: команда не поддерживается: " + c);
        }

        public Task<string> GetCurStatusAsync() { return Task.FromResult(ch.StatusText()); }
    }
}
