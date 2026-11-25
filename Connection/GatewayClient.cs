using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Connection
{
    // Состояние принтера по телеметрии шлюза tcp_to_udp_fabion.
    //
    // Строка от шлюза (одна на каждый пакет платы, ~1000 в секунду):
    //   st1 <строка> <записано> <тип> <флаги> <поля типа...>  ttttt  xc yc zc  xm ym zm  xt yt zt  СК  z1 z2 z3 e1 e2 e3
    //   флаги — 5 цифр: моторы платы движутся · кинематика движется · идёт программа (кольцевой буфер)
    //                   · идёт пауза G4 · ожидание температуры
    //   тип 0: калибр.дельты · лазеры(2 цифры, 1 = луч цел) · концевики(8 цифр) · температура*100 · счётчик
    //          · поиск сопла XYZ(3 цифры, 2 = найдено) · датчики(8) · прогр.концевики(8) · активные Z,E и стадия базирования
    //   тип 1: положение 8 моторов в шагах
    //   ttttt — последняя цифра — активный шприц (0..2)
    //   xc.. — текущая система координат, xm.. — машинные, xt.. — система печати (MANUAL/AUTO)
    //   z1..e3 — машинные координаты Z1..Z3 и E1..E3, мм (добавлено для Fabion)
    public class GatewayState
    {
        public bool MotorsMoving;       // любой мотор платы
        public bool KinematicMoving;    // X, Y, Z и E активного шприца
        public bool ProgramRunning;     // кольцевой буфер включён
        public bool Waiting;            // пауза G4
        public bool WaitingTemp;        // ожидание температуры

        public bool[] Lasers = { true, true };          // true = луч цел
        public bool[] Endstops = new bool[8];           // true = сработал
        public double Temperature;                      // °C — шприц (датчик TH0, Пельтье)
        public double BedTemperature;                   // °C — стол (датчик TB)
        public double SyringeTarget;                    // уставка шприца, 0 — выкл
        public double BedTarget;                        // уставка стола, 0 — выкл
        public bool HasTemperatureTargets;              // прошивка присылает поля 14–16
        public int[] NozzleSearch = new int[3];         // 0 — нет, 1 — ищет, 2 — найдено
        public int HomingStage;                         // 0 — нет, 1 — Z, 2 — XY

        public int ActiveTool;                          // 0..2
        public double[] PosCurrent = new double[3];     // текущая СК (X Y Z)
        public double[] PosMachine = new double[3];     // машинные (X Y Z активного)
        public double[] PosTool = new double[3];        // система печати
        public string CoordSystem = "";
        public double[] MotorMm = new double[8];        // 0 X, 1 Y, 2..4 Z1..Z3, 5..7 E1..E3 (машинные, мм)
        public long[] MotorSteps = new long[8];

        public DateTime LastUpdate = DateTime.MinValue;

        public GatewayState Clone()
        {
            var s = (GatewayState)MemberwiseClone();
            s.Lasers = (bool[])Lasers.Clone();
            s.Endstops = (bool[])Endstops.Clone();
            s.NozzleSearch = (int[])NozzleSearch.Clone();
            s.PosCurrent = (double[])PosCurrent.Clone();
            s.PosMachine = (double[])PosMachine.Clone();
            s.PosTool = (double[])PosTool.Clone();
            s.MotorMm = (double[])MotorMm.Clone();
            s.MotorSteps = (long[])MotorSteps.Clone();
            return s;
        }
    }

    // TCP-клиент шлюза: подключение к 127.0.0.1:62000, очередь отправки, разбор телеметрии в фоне.
    public class GatewayClient : IDisposable
    {
        public string Host { get; private set; }
        public int Port { get; private set; }

        TcpClient _client;
        NetworkStream _stream;
        Thread _readThread;
        Thread _sendThread;
        volatile bool _running;

        readonly object _stateLock = new object();
        GatewayState _state = new GatewayState();

        readonly Queue<string> _sendQueue = new Queue<string>();
        readonly object _sendLock = new object();
        readonly AutoResetEvent _sendEvent = new AutoResetEvent(false);

        static readonly string[] CoordNames = { "MANUAL", "AUTO", "INTERNAL", "CURRENT" };

        public event Action<string> OnMessage;   // строки шлюза, не являющиеся телеметрией

        public GatewayClient(string host, int port)
        {
            Host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
            Port = port <= 0 ? 62000 : port;
        }

        public bool Connected { get { return _client != null && _client.Connected && _running; } }

        public bool Connect()
        {
            try
            {
                Close();
                _client = new TcpClient();
                _client.NoDelay = true;
                var ar = _client.BeginConnect(Host, Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(3000)) { _client.Close(); _client = null; return false; }
                _client.EndConnect(ar);
                _stream = _client.GetStream();
                _running = true;
                _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "gateway-read" };
                _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "gateway-send" };
                _readThread.Start();
                _sendThread.Start();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Шлюз: нет подключения к " + Host + ":" + Port + " — " + ex.Message);
                _running = false;
                return false;
            }
        }

        public Task<bool> ConnectAsync() { return Task.Run(() => Connect()); }

        public void Close()
        {
            _running = false;
            _sendEvent.Set();
            try { if (_stream != null) _stream.Close(); } catch { }
            try { if (_client != null) _client.Close(); } catch { }
            _stream = null;
            _client = null;
        }

        public void Dispose() { Close(); }

        public GatewayState State
        {
            get { lock (_stateLock) return _state.Clone(); }
        }

        //---------------------------------------------------------------- отправка
        // Одна команда шлюзу: "main M..." (команды шлюза) или "num1 M..." (команды платы).
        public void Send(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return;
            lock (_sendLock) _sendQueue.Enqueue(cmd.Trim());
            _sendEvent.Set();
        }

        public void SendMany(IEnumerable<string> cmds)
        {
            lock (_sendLock) foreach (var c in cmds) if (!string.IsNullOrWhiteSpace(c)) _sendQueue.Enqueue(c.Trim());
            _sendEvent.Set();
        }

        public int QueueLength { get { lock (_sendLock) return _sendQueue.Count; } }

        // Шлюз режет входной поток по '\n' и не склеивает строки, разорванные между TCP-пакетами,
        // поэтому шлём пачками по 10 целых строк с паузой 5 мс.
        void SendLoop()
        {
            while (_running)
            {
                _sendEvent.WaitOne(50);
                while (_running)
                {
                    var sb = new StringBuilder();
                    lock (_sendLock)
                    {
                        int n = 0;
                        while (_sendQueue.Count > 0 && n < 10) { sb.Append(_sendQueue.Dequeue()).Append('\n'); n++; }
                    }
                    if (sb.Length == 0) break;
                    try
                    {
                        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
                        _stream.Write(bytes, 0, bytes.Length);
                        _stream.Flush();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Шлюз: ошибка отправки — " + ex.Message);
                        _running = false;
                        break;
                    }
                    Thread.Sleep(5);
                }
            }
        }

        //---------------------------------------------------------------- приём
        void ReadLoop()
        {
            var buf = new byte[65536];
            var acc = new StringBuilder();
            while (_running)
            {
                int n;
                try { n = _stream.Read(buf, 0, buf.Length); }
                catch { break; }
                if (n <= 0) break;
                acc.Append(Encoding.ASCII.GetString(buf, 0, n));
                // разбираем только целые строки, хвост оставляем до следующего пакета
                var text = acc.ToString();
                var last = text.LastIndexOf('\n');
                if (last < 0) continue;
                var whole = text.Substring(0, last);
                acc.Clear();
                acc.Append(text.Substring(last + 1));
                var lines = whole.Split('\n');
                // телеметрия идёт очень часто — берём последние строки каждого типа
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim('\r', ' ');
                    if (line.Length == 0) continue;
                    if (line.StartsWith("st1 ")) ParseState(line);
                    else if (!line.StartsWith("st2 "))
                    {
                        var h = OnMessage;
                        if (h != null) h(line);
                    }
                }
            }
            _running = false;
        }

        static double D(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
        static long L(string s)
        {
            long v;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
        static int Digit(string s, int i) { return (s != null && i < s.Length && char.IsDigit(s[i])) ? s[i] - '0' : 0; }

        void ParseState(string line)
        {
            var t = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 6) return;
            // хвост шлюза: ищем имя СК
            int k = -1;
            for (int i = t.Length - 1; i > 4; i--)
                if (Array.IndexOf(CoordNames, t[i]) >= 0) { k = i; break; }

            lock (_stateLock)
            {
                var s = _state;
                var flags = t[4];
                if (flags.Length >= 5)
                {
                    s.MotorsMoving = Digit(flags, 0) == 1;
                    s.KinematicMoving = Digit(flags, 1) == 1;
                    s.ProgramRunning = Digit(flags, 2) == 1;
                    s.Waiting = Digit(flags, 3) == 1;
                    s.WaitingTemp = Digit(flags, 4) == 1;
                }
                var type = (int)L(t[3]);
                if (type == 0 && t.Length > 12)
                {
                    s.Lasers[0] = Digit(t[6], 0) == 1;
                    s.Lasers[1] = Digit(t[6], 1) == 1;
                    for (int i = 0; i < 8; i++) s.Endstops[i] = Digit(t[7], i) == 1;
                    s.Temperature = D(t[8]) / 100.0;
                    for (int i = 0; i < 3; i++) s.NozzleSearch[i] = Digit(t[10], i);
                    if (t.Length > 13 && t[13].Length >= 3) s.HomingStage = Digit(t[13], 2);
                }
                else if (type == 1 && t.Length >= 13)
                {
                    for (int i = 0; i < 8; i++) s.MotorSteps[i] = L(t[5 + i]);
                }
                // Fabion: тип 2 — температуры: 5 — стол *100, 6 — уставка шприца *100, 7 — уставка стола *100.
                // Поле «ttttt» шлюза идёт сразу после полей платы (k-10): если оно на позиции 8,
                // значит, три поля температуры есть (в старой прошивке здесь были 8 концевиков — тогда k-10 = 13).
                else if (type == 2 && k - 10 == 8)
                {
                    s.BedTemperature = D(t[5]) / 100.0;
                    s.SyringeTarget = D(t[6]) / 100.0;
                    s.BedTarget = D(t[7]) / 100.0;
                    s.HasTemperatureTargets = true;
                }

                if (k > 10)
                {
                    s.ActiveTool = Digit(t[k - 10], 4);
                    for (int i = 0; i < 3; i++)
                    {
                        s.PosCurrent[i] = D(t[k - 9 + i]);
                        s.PosMachine[i] = D(t[k - 6 + i]);
                        s.PosTool[i] = D(t[k - 3 + i]);
                    }
                    s.CoordSystem = t[k];
                    s.MotorMm[0] = s.PosMachine[0];
                    s.MotorMm[1] = s.PosMachine[1];
                    if (t.Length >= k + 7)
                        for (int i = 0; i < 6; i++) s.MotorMm[2 + i] = D(t[k + 1 + i]);
                }
                s.LastUpdate = DateTime.Now;
            }
        }
    }
}
