using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;

namespace Connection
{
    // ============ ENUMS AND DATA CLASSES ============

    public enum PrinterState
    {
        Ready,
        Printing,
        Paused,
        Error,
        Offline,
        Unknown
    }
    public class PrinterInfoResult
    {
        [JsonProperty("state")]
        public string State { get; set; } = "";

        [JsonProperty("state_message")]
        public string StateMessage { get; set; } = "";

        [JsonProperty("hostname")]
        public string Hostname { get; set; } = "";

        [JsonProperty("software_version")]
        public string SoftwareVersion { get; set; } = "";

        [JsonProperty("cpu_info")]
        public string CpuInfo { get; set; } = "";

        [JsonProperty("klipper_path")]
        public string KlipperPath { get; set; } = "";

        [JsonProperty("python_path")]
        public string PythonPath { get; set; } = "";

        [JsonProperty("log_file")]
        public string LogFile { get; set; } = "";

        [JsonProperty("config_file")]
        public string ConfigFile { get; set; } = "";
    }

    public class ToolheadStatus
    {
        [JsonProperty("position")]
        public List<double> Position { get; set; } = new List<double>();

        [JsonProperty("homed_axes")]
        public string HomedAxes { get; set; } = "";

        [JsonProperty("axis_minimum")]
        public List<double> AxisMinimum { get; set; } = new List<double>();

        [JsonProperty("axis_maximum")]
        public List<double> AxisMaximum { get; set; } = new List<double>();
    }

    public class GcodeMoveStatus
    {
        [JsonProperty("absolute_coordinates")]
        public bool AbsoluteCoordinates { get; set; }

        [JsonProperty("absolute_extrude")]
        public bool AbsoluteExtrude { get; set; }

        [JsonProperty("speed_factor")]
        public double SpeedFactor { get; set; } = 1.0;

        [JsonProperty("extrude_factor")]
        public double ExtrudeFactor { get; set; } = 1.0;

        [JsonProperty("homing_origin")]
        public List<double> HomingOrigin { get; set; } = new List<double>();

        [JsonProperty("gcode_position")]
        public List<double> GcodePosition { get; set; } = new List<double>();
    }

    public class MotionReportStatus
    {
        [JsonProperty("live_velocity")]
        public double LiveVelocity { get; set; }

        [JsonProperty("live_extruder_velocity")]
        public double LiveExtruderVelocity { get; set; }

        [JsonProperty("live_position")]
        public List<double> LivePosition { get; set; } = new List<double>();
    }

    public class IdleTimeoutStatus
    {
        [JsonProperty("state")]
        public string State { get; set; } = "";

        [JsonProperty("printing_time")]
        public double PrintingTime { get; set; }
    }

    public class PrintStatsStatus
    {
        [JsonProperty("filename")]
        public string Filename { get; set; } = "";

        [JsonProperty("state")]
        public string State { get; set; } = "";

        [JsonProperty("print_duration")]
        public double PrintDuration { get; set; }

        [JsonProperty("total_duration")]
        public double TotalDuration { get; set; }

        [JsonProperty("info")]
        public JObject Info { get; set; } = new JObject();
    }
    public class PrinterStatus
    {
        public PrinterState State { get; set; }
        public double ExtruderTemp { get; set; }
        public double BedTemp { get; set; }
        public double TargetExtruderTemp { get; set; }
        public double TargetBedTemp { get; set; }
        public Dictionary<string, double> Position { get; set; } = new Dictionary<string, double>();
        public double PrintProgress { get; set; }
        public double PrintTime { get; set; }
        public double PrintTimeLeft { get; set; }
        public string Filename { get; set; } = "";
        public int Layer { get; set; }
        public int TotalLayers { get; set; }
    }

    public class ServerInfo
    {
        [JsonProperty("server_version")]
        public string ServerVersion { get; set; } = "";

        [JsonProperty("api_version")]
        public List<int> ApiVersion { get; set; } = new List<int>();

        [JsonProperty("klipper_version")]
        public string KlipperVersion { get; set; } = "";
    }

    public class TemperatureData
    {
        public double Temperature { get; set; }
        public double Target { get; set; }
        public double Power { get; set; }
    }

    public class PrintStatus
    {
        public string State { get; set; } = "";
        public string Filename { get; set; } = "";
        public double Progress { get; set; }
    }

    public class JobStatus
    {
        public string Filename { get; set; } = "";
        public double PrintTime { get; set; }
        public double PrintTimeLeft { get; set; }
        public double Progress { get; set; }
    }

    public class FileInfo
    {
        public string Path { get; set; } = "";
        public string Root { get; set; } = "";
        public long Size { get; set; }
        public DateTime Modified { get; set; }
    }

    public class MoonrakerResponse
    {
        [JsonProperty("result")]
        public JToken Result { get; set; }

        [JsonProperty("error")]
        public JToken Error { get; set; }
    }



    // ============ EXCEPTION CLASS ============

    public class MoonrakerException : Exception
    {
        public MoonrakerException(string message) : base(message) { }
        public MoonrakerException(string message, Exception innerException) : base(message, innerException) { }
    }

    // ============ MAIN CLIENT CLASS ============

    public class MoonrakerClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _webSocketCts;
        private Task _webSocketTask;
        private bool _isWebSocketConnected = false;
        private readonly Subject<string> _gcodeResponses = new Subject<string>();
        private readonly Subject<PrinterStatus> _statusUpdates = new Subject<PrinterStatus>();
        private readonly JsonSerializerSettings _jsonSettings;

        public IObservable<string> GcodeResponses => _gcodeResponses.AsObservable();
        public IObservable<PrinterStatus> StatusUpdates => _statusUpdates.AsObservable();

        public MoonrakerClient(string host = "localhost", int port = 7125, string apiKey = null)
        {
            _baseUrl = $"http://{host}:{port}";
            _apiKey = apiKey;

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            if (!string.IsNullOrEmpty(apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
            }

            _jsonSettings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore
            };
        }
        public async Task<PrinterInfoResult> GetPrinterInfoAsync(CancellationToken cancellationToken = default)
        {
            var result = await MakeRequestAsync("GET", "/printer/info", null, cancellationToken);
            return result.ToObject<PrinterInfoResult>();
        }

        /// <summary>
        /// Получает полный статус принтера, включая координаты и homing статус
        /// </summary>
        public async Task<PrinterStatus> GetFullStatusAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Создаем объект статуса
                var status = new PrinterStatus();

                // Параллельно запрашиваем различную информацию
                var serverInfoTask = GetServerInfoAsync(cancellationToken);
                var printerInfoTask = GetPrinterInfoAsync(cancellationToken);
                var temperaturesTask = GetTemperaturesAsync(cancellationToken);
                var jobStatusTask = GetJobStatusAsync(cancellationToken);

                // Запрашиваем позицию и статус homing через объекты query
                var motionQueryTask = MakeRequestAsync("GET",
                    "/printer/objects/query?toolhead&gcode_move&motion_report&idle_timeout",
                    null,
                    cancellationToken);

                // Запрашиваем информацию о печати
                var printStatsTask = MakeRequestAsync("GET",
                    "/printer/objects/query?print_stats&display_status",
                    null,
                    cancellationToken);

                // Ждем завершения всех задач
                await Task.WhenAll(
                    serverInfoTask,
                    printerInfoTask,
                    temperaturesTask,
                    jobStatusTask,
                    motionQueryTask,
                    printStatsTask
                );

                var serverInfo = await serverInfoTask;
                var printerInfo = await printerInfoTask;
                var temperatures = await temperaturesTask;
                var jobStatus = await jobStatusTask;
                var motionResult = await motionQueryTask;
                var printStatsResult = await printStatsTask;

                // 1. Parse printer state
                if (!string.IsNullOrEmpty(printerInfo.State))
                {
                    var stateStr = printerInfo.State.ToLower();

                    if (stateStr.Contains("ready") || stateStr.Contains("complete") || stateStr.Contains("standby"))
                    {
                        status.State = PrinterState.Ready;
                    }
                    else if (stateStr.Contains("printing"))
                    {
                        status.State = PrinterState.Printing;
                    }
                    else if (stateStr.Contains("paused"))
                    {
                        status.State = PrinterState.Paused;
                    }
                    else if (stateStr.Contains("error"))
                    {
                        status.State = PrinterState.Error;
                    }
                    else if (stateStr.Contains("offline") || stateStr.Contains("disconnected"))
                    {
                        status.State = PrinterState.Offline;
                    }
                    else
                    {
                        status.State = PrinterState.Unknown;
                    }
                }

                // 2. Parse temperatures
                if (temperatures.TryGetValue("extruder", out var extruderTemp))
                {
                    status.ExtruderTemp = extruderTemp.Temperature;
                    status.TargetExtruderTemp = extruderTemp.Target;
                }
                else if (temperatures.TryGetValue("extruder_actual", out extruderTemp))
                {
                    status.ExtruderTemp = extruderTemp.Temperature;
                    status.TargetExtruderTemp = extruderTemp.Target;
                }

                if (temperatures.TryGetValue("heater_bed", out var bedTemp))
                {
                    status.BedTemp = bedTemp.Temperature;
                    status.TargetBedTemp = bedTemp.Target;
                }
                else if (temperatures.TryGetValue("bed_actual", out bedTemp))
                {
                    status.BedTemp = bedTemp.Temperature;
                    status.TargetBedTemp = bedTemp.Target;
                }

                // 3. Parse job status
                status.Filename = jobStatus.Filename;
                status.PrintProgress = jobStatus.Progress * 100;
                status.PrintTime = jobStatus.PrintTime;
                status.PrintTimeLeft = jobStatus.PrintTimeLeft;

                // 4. Parse position and homing status from motion query
                if (motionResult?["status"] is JObject statusObj)
                {
                    // Parse toolhead status
                    var toolheadStatus = statusObj["toolhead"]?.ToObject<ToolheadStatus>();
                    if (toolheadStatus != null && toolheadStatus.Position.Count >= 3)
                    {
                        try
                        {
                            // Получаем координаты
                            status.Position["x"] = toolheadStatus.Position[0];
                            status.Position["y"] = toolheadStatus.Position[1];
                            status.Position["z"] = toolheadStatus.Position[2];
                            Console.WriteLine(status.Position["x"] + " " + status.Position["y"] + " " + status.Position["z"] + " ");
                            // Если есть экструдер (E)
                            if (toolheadStatus.Position.Count >= 4)
                            {
                                status.Position["e"] = toolheadStatus.Position[3];
                            }

                            // Получаем статус homing
                            if (!string.IsNullOrEmpty(toolheadStatus.HomedAxes))
                            {
                                var homedAxes = toolheadStatus.HomedAxes.ToLower();
                                status.Position["homed_x"] = homedAxes.Contains("x") ? 1 : 0;
                                status.Position["homed_y"] = homedAxes.Contains("y") ? 1 : 0;
                                status.Position["homed_z"] = homedAxes.Contains("z") ? 1 : 0;

                                var homingInfo = new StringBuilder();
                                homingInfo.Append($"X: {(homedAxes.Contains("x") ? "Homed" : "Not Homed")}, ");
                                homingInfo.Append($"Y: {(homedAxes.Contains("y") ? "Homed" : "Not Homed")}, ");
                                homingInfo.Append($"Z: {(homedAxes.Contains("z") ? "Homed" : "Not Homed")}");
                               // status.Position["homing_status"] = homingInfo.ToString();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error parsing position: {ex.Message}");
                        }
                    }

                    // Parse gcode move status
                    var gcodeMoveStatus = statusObj["gcode_move"]?.ToObject<GcodeMoveStatus>();
                    if (gcodeMoveStatus != null)
                    {
                        status.Position["absolute_mode"] = gcodeMoveStatus.AbsoluteCoordinates ? 1 : 0;
                        status.Position["absolute_extrude"] = gcodeMoveStatus.AbsoluteExtrude ? 1 : 0;

                        // Сохраняем факторы скорости
                        status.Position["speed_factor"] = gcodeMoveStatus.SpeedFactor;
                        status.Position["extrude_factor"] = gcodeMoveStatus.ExtrudeFactor;

                        // Сохраняем позицию G-кода
                        if (gcodeMoveStatus.GcodePosition.Count >= 3)
                        {
                            status.Position["gcode_x"] = gcodeMoveStatus.GcodePosition[0];
                            status.Position["gcode_y"] = gcodeMoveStatus.GcodePosition[1];
                            status.Position["gcode_z"] = gcodeMoveStatus.GcodePosition[2];
                            if (gcodeMoveStatus.GcodePosition.Count >= 4)
                            {
                                status.Position["gcode_e"] = gcodeMoveStatus.GcodePosition[3];
                            }
                        }
                    }

                    // Parse motion report
                    var motionReportStatus = statusObj["motion_report"]?.ToObject<MotionReportStatus>();
                    if (motionReportStatus != null)
                    {
                        status.Position["speed"] = motionReportStatus.LiveVelocity * 60; // Convert from mm/s to mm/min
                        status.Position["extruder_speed"] = motionReportStatus.LiveExtruderVelocity * 60;

                        if (motionReportStatus.LivePosition.Count >= 3)
                        {
                            status.Position["live_x"] = motionReportStatus.LivePosition[0];
                            status.Position["live_y"] = motionReportStatus.LivePosition[1];
                            status.Position["live_z"] = motionReportStatus.LivePosition[2];
                        }
                    }

                    // Parse idle timeout
                    var idleTimeoutStatus = statusObj["idle_timeout"]?.ToObject<IdleTimeoutStatus>();
                    if (idleTimeoutStatus != null)
                    {
                        status.Position["idle_state"] = idleTimeoutStatus.State.ToLower().Contains("idle") ? 0 : 1;
                        status.Position["printing_time"] = idleTimeoutStatus.PrintingTime;
                    }
                }

                // 5. Parse print stats
                if (printStatsResult?["status"]?["print_stats"] is JObject printStatsObj)
                {
                    var printStats = printStatsObj.ToObject<PrintStatsStatus>();

                    if (printStats != null)
                    {
                        // Обновляем имя файла, если оно есть
                        if (!string.IsNullOrEmpty(printStats.Filename))
                        {
                            status.Filename = printStats.Filename;
                        }

                        // Обновляем время печати
                        if (printStats.PrintDuration > 0)
                        {
                            status.PrintTime = printStats.PrintDuration;
                        }

                        // Обновляем прогресс
                        if (printStats.TotalDuration > 0 && printStats.PrintDuration > 0)
                        {
                            var progress = (printStats.PrintDuration / printStats.TotalDuration) * 100;
                            if (progress > 0)
                            {
                                status.PrintProgress = progress;
                                status.PrintTimeLeft = printStats.TotalDuration - printStats.PrintDuration;
                            }
                        }

                        // Получаем информацию о слоях
                        if (printStats.Info != null)
                        {
                            if (printStats.Info["total_layer"] != null)
                            {
                                status.TotalLayers = printStats.Info["total_layer"].Value<int>();
                            }
                            if (printStats.Info["current_layer"] != null)
                            {
                                status.Layer = printStats.Info["current_layer"].Value<int>();
                            }
                        }
                    }
                }

                return status;
            }
            catch (Exception ex)
            {
                throw new MoonrakerException("Failed to get printer status", ex);
            }
        }
        public async Task<string> GetCurStatusAsync(CancellationToken cancellationToken = default)
        {
            //try
            {
                // Создаем объект статуса
                var status = new PrinterStatus();
                var status_str = "";
                // Запрашиваем позицию и статус homing через объекты query
                /*var motionQueryTask = MakeRequestAsync("GET",
                    "/printer/objects/query?toolhead&gcode_move&motion_report&idle_timeout",
                    null,
                    cancellationToken);

                // Запрашиваем информацию о печати
                var printStatsTask = MakeRequestAsync("GET",
                    "/printer/objects/query?print_stats&display_status",
                    null,
                    cancellationToken);

                // Ждем завершения всех задач
                await Task.WhenAll(
                    motionQueryTask,
                    printStatsTask
                );
                */
                var motionQueryTask = MakeRequestAsync("GET",
                   "/printer/objects/query?toolhead",
                   null,
                   cancellationToken);
                var motionResult = await motionQueryTask;
                //var printStatsResult = await printStatsTask;
                if (motionResult?["status"] is JObject statusObj)
                {
                    var toolheadStatus = statusObj["toolhead"]?.ToObject<ToolheadStatus>();
                    status.Position["x"] = toolheadStatus.Position[0];
                    status.Position["y"] = toolheadStatus.Position[1];
                    status.Position["z"] = toolheadStatus.Position[2];
                    status_str = status.Position["x"] + " " + status.Position["y"] + " " + status.Position["z"] + " ";
                }
                return status_str;

                // 4. Parse position and homing status from motion query
                /*if (motionResult?["status"] is JObject statusObj)
                {
                    // Parse toolhead status
                    var toolheadStatus = statusObj["toolhead"]?.ToObject<ToolheadStatus>();
                    if (toolheadStatus != null && toolheadStatus.Position.Count >= 3)
                    {
                        try
                        {
                            // Получаем координаты
                            status.Position["x"] = toolheadStatus.Position[0];
                            status.Position["y"] = toolheadStatus.Position[1];
                            status.Position["z"] = toolheadStatus.Position[2];
                            status_str  = status.Position["x"] + " " + status.Position["y"] + " " + status.Position["z"] + " ";
                            Console.WriteLine(status_str);
                            // Если есть экструдер (E)
                            if (toolheadStatus.Position.Count >= 4)
                            {
                                status.Position["e"] = toolheadStatus.Position[3];
                            }

                            // Получаем статус homing
                            if (!string.IsNullOrEmpty(toolheadStatus.HomedAxes))
                            {
                                var homedAxes = toolheadStatus.HomedAxes.ToLower();
                                status.Position["homed_x"] = homedAxes.Contains("x") ? 1 : 0;
                                status.Position["homed_y"] = homedAxes.Contains("y") ? 1 : 0;
                                status.Position["homed_z"] = homedAxes.Contains("z") ? 1 : 0;

                                var homingInfo = new StringBuilder();
                                homingInfo.Append($"X: {(homedAxes.Contains("x") ? "Homed" : "Not Homed")}, ");
                                homingInfo.Append($"Y: {(homedAxes.Contains("y") ? "Homed" : "Not Homed")}, ");
                                homingInfo.Append($"Z: {(homedAxes.Contains("z") ? "Homed" : "Not Homed")}");
                                // status.Position["homing_status"] = homingInfo.ToString();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error parsing position: {ex.Message}");
                        }
                    }

                    // Parse gcode move status
                    var gcodeMoveStatus = statusObj["gcode_move"]?.ToObject<GcodeMoveStatus>();
                    if (gcodeMoveStatus != null)
                    {
                        status.Position["absolute_mode"] = gcodeMoveStatus.AbsoluteCoordinates ? 1 : 0;
                        status.Position["absolute_extrude"] = gcodeMoveStatus.AbsoluteExtrude ? 1 : 0;

                        // Сохраняем факторы скорости
                        status.Position["speed_factor"] = gcodeMoveStatus.SpeedFactor;
                        status.Position["extrude_factor"] = gcodeMoveStatus.ExtrudeFactor;

                        // Сохраняем позицию G-кода
                        if (gcodeMoveStatus.GcodePosition.Count >= 3)
                        {
                            status.Position["gcode_x"] = gcodeMoveStatus.GcodePosition[0];
                            status.Position["gcode_y"] = gcodeMoveStatus.GcodePosition[1];
                            status.Position["gcode_z"] = gcodeMoveStatus.GcodePosition[2];
                            if (gcodeMoveStatus.GcodePosition.Count >= 4)
                            {
                                status.Position["gcode_e"] = gcodeMoveStatus.GcodePosition[3];
                            }
                        }
                    }

                    // Parse motion report
                    var motionReportStatus = statusObj["motion_report"]?.ToObject<MotionReportStatus>();
                    if (motionReportStatus != null)
                    {
                        status.Position["speed"] = motionReportStatus.LiveVelocity * 60; // Convert from mm/s to mm/min
                        status.Position["extruder_speed"] = motionReportStatus.LiveExtruderVelocity * 60;

                        if (motionReportStatus.LivePosition.Count >= 3)
                        {
                            status.Position["live_x"] = motionReportStatus.LivePosition[0];
                            status.Position["live_y"] = motionReportStatus.LivePosition[1];
                            status.Position["live_z"] = motionReportStatus.LivePosition[2];
                        }
                    }

                    // Parse idle timeout
                    var idleTimeoutStatus = statusObj["idle_timeout"]?.ToObject<IdleTimeoutStatus>();
                    if (idleTimeoutStatus != null)
                    {
                        status.Position["idle_state"] = idleTimeoutStatus.State.ToLower().Contains("idle") ? 0 : 1;
                        status.Position["printing_time"] = idleTimeoutStatus.PrintingTime;
                    }
                }

                // 5. Parse print stats
                if (printStatsResult?["status"]?["print_stats"] is JObject printStatsObj)
                {
                    var printStats = printStatsObj.ToObject<PrintStatsStatus>();

                    if (printStats != null)
                    {
                        // Обновляем имя файла, если оно есть
                        if (!string.IsNullOrEmpty(printStats.Filename))
                        {
                            status.Filename = printStats.Filename;
                        }

                        // Обновляем время печати
                        if (printStats.PrintDuration > 0)
                        {
                            status.PrintTime = printStats.PrintDuration;
                        }

                        // Обновляем прогресс
                        if (printStats.TotalDuration > 0 && printStats.PrintDuration > 0)
                        {
                            var progress = (printStats.PrintDuration / printStats.TotalDuration) * 100;
                            if (progress > 0)
                            {
                                status.PrintProgress = progress;
                                status.PrintTimeLeft = printStats.TotalDuration - printStats.PrintDuration;
                            }
                        }

                        // Получаем информацию о слоях
                        if (printStats.Info != null)
                        {
                            if (printStats.Info["total_layer"] != null)
                            {
                                //status.TotalLayers = printStats.Info["total_layer"].Value<int>();
                            }
                            if (printStats.Info["current_layer"] != null)
                            {
                                //status.Layer = printStats.Info["current_layer"].Value<int>();
                            }
                        }
                    }
                }

                return status_str;*/
            }
            //catch (Exception ex)
            {
                
               // throw new MoonrakerException("Failed to get printer status", ex);
            }
            return "";
        }
        // ============ HELPER METHODS FOR POSITION AND HOMING ============

        /// <summary>
        /// Получает текущую позицию всех осей
        /// </summary>
        public async Task<Dictionary<string, double>> GetCurrentPositionAsync(CancellationToken cancellationToken = default)
        {
            var positions = new Dictionary<string, double>();

            try
            {
                var result = await MakeRequestAsync("GET",
                    "/printer/objects/query?toolhead",
                    null,
                    cancellationToken);

                if (result?["status"]?["toolhead"]?.ToObject<ToolheadStatus>() is ToolheadStatus toolhead)
                {
                    if (toolhead.Position.Count > 0) positions["x"] = toolhead.Position[0];
                    if (toolhead.Position.Count > 1) positions["y"] = toolhead.Position[1];
                    if (toolhead.Position.Count > 2) positions["z"] = toolhead.Position[2];
                    if (toolhead.Position.Count > 3) positions["e"] = toolhead.Position[3];
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting position: {ex.Message}");
            }

            return positions;
        }

        /// <summary>
        /// Получает статус homing для всех осей
        /// </summary>
        public async Task<Dictionary<string, bool>> GetHomingStatusAsync(CancellationToken cancellationToken = default)
        {
            var homingStatus = new Dictionary<string, bool>
            {
                ["x"] = false,
                ["y"] = false,
                ["z"] = false
            };

            try
            {
                var result = await MakeRequestAsync("GET",
                    "/printer/objects/query?toolhead",
                    null,
                    cancellationToken);

                if (result?["status"]?["toolhead"]?.ToObject<ToolheadStatus>() is ToolheadStatus toolhead)
                {
                    if (!string.IsNullOrEmpty(toolhead.HomedAxes))
                    {
                        var homedAxes = toolhead.HomedAxes.ToLower();
                        homingStatus["x"] = homedAxes.Contains("x");
                        homingStatus["y"] = homedAxes.Contains("y");
                        homingStatus["z"] = homedAxes.Contains("z");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting homing status: {ex.Message}");
            }

            return homingStatus;
        }

        /// <summary>
        /// Получает детальную информацию о движении
        /// </summary>
        public async Task<Dictionary<string, object>> GetMotionStatusAsync(CancellationToken cancellationToken = default)
        {
            var motionStatus = new Dictionary<string, object>();

            try
            {
                var result = await MakeRequestAsync("GET",
                    "/printer/objects/query?toolhead&gcode_move&motion_report",
                    null,
                    cancellationToken);

                if (result?["status"] is JObject statusObj)
                {
                    // Toolhead information
                    if (statusObj["toolhead"]?.ToObject<ToolheadStatus>() is ToolheadStatus toolhead)
                    {
                        motionStatus["toolhead"] = toolhead;

                        // Extract position separately
                        if (toolhead.Position.Count >= 3)
                        {
                            motionStatus["position"] = new Dictionary<string, double>
                            {
                                ["x"] = toolhead.Position[0],
                                ["y"] = toolhead.Position[1],
                                ["z"] = toolhead.Position[2]
                            };

                            if (toolhead.Position.Count >= 4)
                            {
                                ((Dictionary<string, double>)motionStatus["position"])["e"] = toolhead.Position[3];
                            }
                        }
                    }

                    // G-code move information
                    if (statusObj["gcode_move"]?.ToObject<GcodeMoveStatus>() is GcodeMoveStatus gcodeMove)
                    {
                        motionStatus["gcode_move"] = gcodeMove;
                    }

                    // Motion report
                    if (statusObj["motion_report"]?.ToObject<MotionReportStatus>() is MotionReportStatus motionReport)
                    {
                        motionStatus["motion_report"] = motionReport;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting motion status: {ex.Message}");
            }

            return motionStatus;
        }

        /// <summary>
        /// Получает статус всех осей, включая homing и положение
        /// </summary>
        public async Task<Dictionary<string, object>> GetAxesStatusAsync(CancellationToken cancellationToken = default)
        {
            var axesStatus = new Dictionary<string, object>();

            try
            {
                // Получаем позицию
                var position = await GetCurrentPositionAsync(cancellationToken);
                axesStatus["position"] = position;

                // Получаем статус homing
                var homing = await GetHomingStatusAsync(cancellationToken);
                axesStatus["homed"] = homing;

                // Получаем дополнительные данные о движении
                var motion = await GetMotionStatusAsync(cancellationToken);
                axesStatus["motion"] = motion;

                // Проверяем, все ли оси в нулевой позиции
                bool allAtZero = true;
                foreach (var axis in new[] { "x", "y", "z" })
                {
                    if (position.ContainsKey(axis) && Math.Abs(position[axis]) > 0.001)
                    {
                        allAtZero = false;
                        break;
                    }
                }
                axesStatus["all_at_zero"] = allAtZero;

                // Проверяем, все ли оси выполнены homing
                bool allHomed = homing.Values.All(h => h);
                axesStatus["all_homed"] = allHomed;

                // Добавляем метку состояния
                axesStatus["status"] = allHomed ? "All axes homed" : "Some axes not homed";

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting axes status: {ex.Message}");
            }

            return axesStatus;
        }

        /// <summary>
        /// Выполняет homing для указанных осей
        /// </summary>
        public async Task HomeAxesAsync(bool homeX = true, bool homeY = true, bool homeZ = true, CancellationToken cancellationToken = default)
        {
            var axes = new List<string>();
            if (homeX) axes.Add("X");
            if (homeY) axes.Add("Y");
            if (homeZ) axes.Add("Z");

            if (axes.Count == 0) return;

            var gcode = $"G28 {string.Join(" ", axes)}";
            await SendGcodeAsync(gcode, cancellationToken);

            Console.WriteLine($"Homing axes: {string.Join(", ", axes)}");

            // Ждем завершения homing
            if (axes.Count == 3)
            {
                Console.WriteLine("Waiting for homing to complete...");
                await Task.Delay(3000, cancellationToken);

                // Проверяем статус homing
                var homingStatus = await GetHomingStatusAsync(cancellationToken);
                Console.WriteLine($"Homing status: X={homingStatus["x"]}, Y={homingStatus["y"]}, Z={homingStatus["z"]}");
            }
        }
        // ============ HTTP METHODS ============



        // ============ FILE UPLOAD METHODS ============

        /// <summary>
        /// Загружает файл G-code на сервер
        /// </summary>
        public async Task<string> UploadGCodeFileAsync(string filePath, string remoteFilename = null, string root = "gcodes", CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                throw new MoonrakerException($"Файл не найден: {filePath}");
            }

            if (string.IsNullOrEmpty(remoteFilename))
            {
                remoteFilename = Path.GetFileName(filePath);
            }

            // Очищаем имя файла от недопустимых символов
            remoteFilename = CleanFileName(remoteFilename);

            Console.WriteLine($"Загрузка файла: {filePath} -> {remoteFilename} в корень: {root}");

            var url = $"{_baseUrl}/server/files/upload";
            Console.WriteLine($"URL загрузки: {url}");

            try
            {
                // Читаем файл
                var fileBytes = File.ReadAllBytes(filePath);
                Console.WriteLine($"Размер файла: {fileBytes.Length} байт");

                var content = new MultipartFormDataContent("----WebKitFormBoundary7MA4YWxkTrZu0gW");

                // Добавляем файл
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                content.Add(fileContent, "file", remoteFilename);

                // Добавляем root
                content.Add(new StringContent(root), "root");

                // Добавляем path если указан
                if (!string.IsNullOrEmpty(remoteFilename))
                {
                    content.Add(new StringContent(remoteFilename), "path");
                }

                // Логируем заголовки
                Console.WriteLine("Отправка запроса загрузки...");

                // Отправляем запрос
                var response = await _httpClient.PostAsync(url, content, cancellationToken);

                var responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Статус ответа: {(int)response.StatusCode} {response.StatusCode}");
                Console.WriteLine($"Тело ответа: {responseContent}");

                if (!response.IsSuccessStatusCode)
                {
                    throw new MoonrakerException($"Ошибка загрузки файла: {(int)response.StatusCode} {response.StatusCode}. Ответ: {responseContent}");
                }

                // Парсим JSON ответ
                var jsonObj = JObject.Parse(responseContent);

                // Проверяем наличие ошибки
                if (jsonObj["error"] != null && jsonObj["error"].Type != JTokenType.Null)
                {
                    var error = jsonObj["error"].ToString();
                    throw new MoonrakerException($"API ошибка: {error}");
                }

                // Получаем информацию о загруженном файле
                var result = jsonObj["result"]?["item"];
                if (result != null)
                {
                    var uploadedPath = result["path"]?.ToString() ?? remoteFilename;
                    Console.WriteLine($"✓ Файл успешно загружен: {uploadedPath}");
                    return uploadedPath;
                }

                return remoteFilename;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"HTTP ошибка: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Внутренняя ошибка: {ex.InnerException.Message}");
                }
                throw new MoonrakerException($"Ошибка HTTP при загрузке файла: {ex.Message}", ex);
            }
            catch (JsonReaderException ex)
            {
                Console.WriteLine($"JSON ошибка парсинга: {ex.Message}");
                throw new MoonrakerException($"Ошибка парсинга JSON ответа: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Общая ошибка: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Загружает G-code в виде строки на сервер
        /// </summary>
        public async Task<string> UploadGCodeStringAsync(string gcodeContent, string filename="cur_prog.gcode", string root = "gcodes", CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(filename))
            {
                throw new MoonrakerException("Имя файла не может быть пустым");
            }

            // Очищаем имя файла
            filename = CleanFileName(filename);

            // Создаем временный файл
            var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".gcode");

            try
            {
                Console.WriteLine($"Создание временного файла: {tempFile}");
                Console.WriteLine($"Длина G-code: {gcodeContent.Length} символов");

                // Записываем G-code во временный файл (синхронно)
                File.WriteAllText(tempFile, gcodeContent, Encoding.UTF8);

                // Загружаем временный файл
                return await UploadGCodeFileAsync(tempFile, filename, root, cancellationToken);
            }
            finally
            {
                // Удаляем временный файл
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                        Console.WriteLine($"Временный файл удален: {tempFile}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка удаления временного файла: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Загружает G-code из строки и немедленно начинает печать
        /// </summary>
        public async Task<string> UploadAndPrintGCodeAsync(string gcodeContent, string filename, CancellationToken cancellationToken = default)
        {
            // Загружаем файл
            var filePath = await UploadGCodeStringAsync(gcodeContent, filename, "gcodes", cancellationToken);

            // Ждем немного, чтобы файл был полностью обработан
            Console.WriteLine("Ожидание обработки файла...");
            await Task.Delay(2000, cancellationToken);

            // Начинаем печать
            await PrintFileAsync(filePath, cancellationToken);

            return filePath;
        }

        // ============ PRINT MANAGEMENT ============

        /// <summary>
        /// Начинает печать указанного файла
        /// </summary>
        public async Task PrintFileAsync(string filename="cur_prog.gcode", CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(filename))
            {
                throw new MoonrakerException("Имя файла не может быть пустым");
            }

            Console.WriteLine($"Запуск печати файла: {filename}");

            var data = new { filename };
            var url = $"{_baseUrl}/printer/print/start";

            try
            {
                var json = JsonConvert.SerializeObject(data, _jsonSettings);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                Console.WriteLine($"POST {url}");
                Console.WriteLine($"Тело запроса: {json}");

                var response = await _httpClient.PostAsync(url, content, cancellationToken);
                var responseContent = await response.Content.ReadAsStringAsync();

                Console.WriteLine($"Статус ответа: {(int)response.StatusCode} {response.StatusCode}");
                Console.WriteLine($"Тело ответа: {responseContent}");

                if (!response.IsSuccessStatusCode)
                {
                    throw new MoonrakerException($"Ошибка запуска печати: {(int)response.StatusCode} {response.StatusCode}");
                }

                // Проверяем JSON ответ
                var jsonObj = JObject.Parse(responseContent);
                if (jsonObj["error"] != null && jsonObj["error"].Type != JTokenType.Null)
                {
                    var error = jsonObj["error"].ToString();
                    throw new MoonrakerException($"API ошибка: {error}");
                }

                Console.WriteLine($"✓ Печать файла '{filename}' успешно запущена");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при запуске печати: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Останавливает текущую печать
        /// </summary>
        public async Task StopPrintAsync(CancellationToken cancellationToken = default)
        {
            await CancelPrintAsync(cancellationToken);
        }

        // ============ ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ============

        private string CleanFileName(string fileName)
        {
            // Убираем путь, оставляем только имя файла
            fileName = Path.GetFileName(fileName);

            // Заменяем недопустимые символы
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var ch in invalidChars)
            {
                fileName = fileName.Replace(ch, '_');
            }

            // Убедимся, что расширение .gcode
            if (!fileName.ToLower().EndsWith(".gcode"))
            {
                fileName += ".gcode";
            }

            return fileName;
        }



        private async Task<JToken> MakeRequestAsync(string method, string endpoint, object data = null, CancellationToken cancellationToken = default)
        {
            var url = $"{_baseUrl}{endpoint}";
            HttpResponseMessage response = null;

            try
            {
                if (method == "GET")
                {
                    response = await _httpClient.GetAsync(url, cancellationToken);
                }
                else if (method == "POST")
                {
                    var json = JsonConvert.SerializeObject(data, _jsonSettings);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    response = await _httpClient.PostAsync(url, content, cancellationToken);
                }
                else if (method == "DELETE")
                {
                    response = await _httpClient.DeleteAsync(url, cancellationToken);
                }
                else
                {
                    throw new MoonrakerException($"Unsupported HTTP method: {method}");
                }

                response.EnsureSuccessStatusCode();

                // Читаем ответ как строку
                var responseJson = await response.Content.ReadAsStringAsync();

                // Парсим JSON
                var jsonObj = JObject.Parse(responseJson);

                // Проверяем наличие ошибки
                if (jsonObj["error"] != null && jsonObj["error"].Type != JTokenType.Null)
                {
                    var error = jsonObj["error"].ToString();
                    throw new MoonrakerException($"API error: {error}");
                }

                // Возвращаем результат
                return jsonObj["result"];
            }
            /*catch (HttpRequestException ex)
            {
                Console.WriteLine(ex.Message);
                //throw new MoonrakerException($"HTTP request failed: {ex.Message}", ex);
            }
            catch (JsonReaderException ex)
            {
                Console.WriteLine(ex.Message);
               // throw new MoonrakerException($"JSON parsing failed: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex)
            {
                Console.WriteLine(ex.Message);
               // throw new MoonrakerException($"Request timeout: {ex.Message}", ex);
            }
            finally
            {
                response?.Dispose();
            }*/
            catch { return null; }

        }

        public async Task<ServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
        {
            var result = await MakeRequestAsync("GET", "/server/info", null, cancellationToken);
            return result.ToObject<ServerInfo>();
        }


        // ============ GCODE COMMANDS ============

        public async Task<string> SendGcodeAsync(string gcode, CancellationToken cancellationToken = default)
        {
            var data = new { script = gcode };
            var result = await MakeRequestAsync("POST", "/printer/gcode/script", data, cancellationToken);

            if (result is JObject obj && obj["response"] != null)
            {
                return obj["response"].ToString();
            }

            return "Command sent successfully";
        }

        public async Task<string> SendGcodeScriptAsync(string script, CancellationToken cancellationToken = default)
        {
            return await SendGcodeAsync(script, cancellationToken);
        }

        public async Task EmergencyStopAsync(CancellationToken cancellationToken = default)
        {
            await SendGcodeAsync("M112", cancellationToken);
        }

        public async Task PausePrintAsync(CancellationToken cancellationToken = default)
        {
            await SendGcodeAsync("PAUSE", cancellationToken);
        }

        public async Task ResumePrintAsync(CancellationToken cancellationToken = default)
        {
            await SendGcodeAsync("RESUME", cancellationToken);
        }

        public async Task CancelPrintAsync(CancellationToken cancellationToken = default)
        {
            await SendGcodeAsync("CANCEL_PRINT", cancellationToken);
        }

        // ============ MOVEMENT COMMANDS ============

        public async Task HomeAllAsync(CancellationToken cancellationToken = default)
        {
            await SendGcodeAsync("G28", cancellationToken);
        }

        public async Task HomeXAsync(CancellationToken cancellationToken = default)
        {
            await SendGcodeAsync("G28 X", cancellationToken);
        }

        public async Task HomeYAsync(CancellationToken cancellationToken = default)
        {
            await SendGcodeAsync("G28 Y", cancellationToken);
        }

        public async Task HomeZAsync(CancellationToken cancellationToken = default)
        {
            await SendGcodeAsync("G28 Z", cancellationToken);
        }

        public async Task MoveRelativeAsync(double? x = null, double? y = null, double? z = null, double? e = null, double? speed = null, CancellationToken cancellationToken = default)
        {
            var script = new StringBuilder();
            script.AppendLine("G91"); // Relative mode

            script.Append("G1 ");
            if (x.HasValue) script.Append($"X{x.Value} ");
            if (y.HasValue) script.Append($"Y{y.Value} ");
            if (z.HasValue) script.Append($"Z{z.Value} ");
            if (e.HasValue) script.Append($"E{e.Value} ");
            if (speed.HasValue) script.Append($"F{speed.Value}");

            script.AppendLine();
            script.Append("G90"); // Back to absolute mode

            await SendGcodeScriptAsync(script.ToString(), cancellationToken);
        }

        public async Task MoveAbsoluteAsync(double? x = null, double? y = null, double? z = null, double? e = null, double? speed = null, CancellationToken cancellationToken = default)
        {
            var gcode = new StringBuilder("G90\nG1 "); // Absolute mode

            if (x.HasValue) gcode.Append($"X{x.Value} ");
            if (y.HasValue) gcode.Append($"Y{y.Value} ");
            if (z.HasValue) gcode.Append($"Z{z.Value} ");
            if (e.HasValue) gcode.Append($"E{e.Value} ");
            if (speed.HasValue) gcode.Append($"F{speed.Value}");

            await SendGcodeAsync(gcode.ToString(), cancellationToken);
        }

        public async Task SetPositionAsync(double? x = null, double? y = null, double? z = null, double? e = null, CancellationToken cancellationToken = default)
        {
            var gcode = new StringBuilder("G92 ");

            if (x.HasValue) gcode.Append($"X{x.Value} ");
            if (y.HasValue) gcode.Append($"Y{y.Value} ");
            if (z.HasValue) gcode.Append($"Z{z.Value} ");
            if (e.HasValue) gcode.Append($"E{e.Value}");

            await SendGcodeAsync(gcode.ToString(), cancellationToken);
        }

        // ============ TEMPERATURE COMMANDS ============

        public async Task SetExtruderTempAsync(double temp, int tool = 0, CancellationToken cancellationToken = default)
        {
            if (tool == 0)
                await SendGcodeAsync($"M104 S{temp}", cancellationToken);
            else
                await SendGcodeAsync($"M104 T{tool} S{temp}", cancellationToken);
        }

        public async Task SetExtruderTempAndWaitAsync(double temp, int tool = 0, CancellationToken cancellationToken = default)
        {
            if (tool == 0)
                await SendGcodeAsync($"M109 S{temp}", cancellationToken);
            else
                await SendGcodeAsync($"M109 T{tool} S{temp}", cancellationToken);
        }

        public async Task SetBedTempAsync(double temp, CancellationToken cancellationToken = default)
        {
            await SendGcodeAsync($"M140 S{temp}", cancellationToken);
        }

        public async Task SetBedTempAndWaitAsync(double temp, CancellationToken cancellationToken = default)
        {
            await SendGcodeAsync($"M190 S{temp}", cancellationToken);
        }

        public async Task<Dictionary<string, TemperatureData>> GetTemperaturesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await MakeRequestAsync("GET",
                    "/printer/objects/query?heater_bed&extruder&temperature_sensor",
                null,
                cancellationToken);

                if (result?["status"] is JObject statusObj)
                {
                    var temperatures = new Dictionary<string, TemperatureData>();

                    foreach (var item in statusObj)
                    {
                        if (item.Key.Contains("temperature") ||
                            item.Key.Contains("heater") ||
                            item.Key.Contains("extruder") ||
                            item.Key.Contains("bed"))
                        {
                            try
                            {
                                var tempData = item.Value.ToObject<TemperatureData>();
                                temperatures[item.Key] = tempData;
                            }
                            catch
                            {
                                // Пропускаем элементы, которые не являются температурой
                            }
                        }
                    }

                    return temperatures;
                }

                return new Dictionary<string, TemperatureData>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting temperatures: {ex.Message}");
                return new Dictionary<string, TemperatureData>();
            }
        }

        // ============ FAN COMMANDS ============

        public async Task SetFanSpeedAsync(double speed, int fan = 0, CancellationToken cancellationToken = default)
        {
            int pwm;
            if (speed >= 0 && speed <= 1)
                pwm = (int)(speed * 255);
            else
                pwm = (int)speed;

            await SendGcodeAsync($"M106 P{fan} S{pwm}", cancellationToken);
        }

        public async Task FanOffAsync(int fan = 0, CancellationToken cancellationToken = default)
        {
            await SendGcodeAsync($"M107 P{fan}", cancellationToken);
        }

        // ============ EXTRUSION COMMANDS ============

        public async Task ExtrudeAsync(double amount, double speed = 300, CancellationToken cancellationToken = default)
        {
            await SendGcodeAsync($"G1 E{amount} F{speed}", cancellationToken);
        }

        public async Task RetractAsync(double amount = 1.0, double speed = 1500, CancellationToken cancellationToken = default)
        {
            await SendGcodeAsync($"G1 E-{amount} F{speed}", cancellationToken);
        }

        // ============ PRINT MANAGEMENT ============

  
        public async Task<PrintStatus> GetPrintStatusAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await MakeRequestAsync("GET", "/printer/print/status", null, cancellationToken);
                return result?.ToObject<PrintStatus>() ?? new PrintStatus();
            }
            catch
            {
                return new PrintStatus();
            }
        }

        public async Task<JobStatus> GetJobStatusAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await MakeRequestAsync("GET", "/server/job/status", null, cancellationToken);

                if (result?["job"] is JObject jobObj)
                {
                    return new JobStatus
                    {
                        Filename = jobObj["filename"]?.ToString() ?? "",
                        PrintTime = jobObj["print_time"]?.ToObject<double>() ?? 0,
                        PrintTimeLeft = jobObj["print_time_left"]?.ToObject<double>() ?? 0,
                        Progress = jobObj["progress"]?.ToObject<double>() ?? 0
                    };
                }

                return new JobStatus();
            }
            catch
            {
                return new JobStatus();
            }
        }

        // ============ FILE MANAGEMENT ============

        public async Task<List<FileInfo>> ListFilesAsync(string root = "gcodes", CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await MakeRequestAsync("GET", $"/server/files/list?root={root}", null, cancellationToken);
                return result?.ToObject<List<FileInfo>>() ?? new List<FileInfo>();
            }
            catch
            {
                return new List<FileInfo>();
            }
        }

        public async Task DeleteFileAsync(string filename, string root = "gcodes", CancellationToken cancellationToken = default)
        {
            var data = new { filename, root };
            await MakeRequestAsync("DELETE", "/server/files/delete", data, cancellationToken);
        }

        // ============ MACROS ============

        public async Task RunMacroAsync(string macroName, Dictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            if (parameters != null && parameters.Count > 0)
            {
                var paramsStr = string.Join(" ", parameters.Select(kv => $"{kv.Key}={kv.Value}"));
                await SendGcodeAsync($"{macroName} {paramsStr}", cancellationToken);
            }
            else
            {
                await SendGcodeAsync(macroName, cancellationToken);
            }
        }

        // ============ WEBSOCKET CONNECTION ============

        public async Task ConnectWebSocketAsync()
        {
            if (_isWebSocketConnected)
                return;

            _webSocketCts = new CancellationTokenSource();
            _webSocket = new ClientWebSocket();

            var wsUrl = $"ws://{_baseUrl.Replace("http://", "")}/websocket";
            await _webSocket.ConnectAsync(new Uri(wsUrl), _webSocketCts.Token);

            _isWebSocketConnected = true;

            // Start receiving messages
            _webSocketTask = Task.Run(() => ReceiveWebSocketMessagesAsync(_webSocketCts.Token));

            Console.WriteLine("WebSocket connected");
        }

        private async Task ReceiveWebSocketMessagesAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];

            while (_isWebSocketConnected && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    //Console.WriteLine(result.ToString());
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                        _isWebSocketConnected = false;
                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    //Console.WriteLine(message);
                    ProcessWebSocketMessage(message);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WebSocket error: {ex.Message}");
                    _isWebSocketConnected = false;
                    break;
                }
            }
        }

        private void ProcessWebSocketMessage(string message)
        {
            try
            {
                var jsonObj = JObject.Parse(message);

                if (jsonObj["method"] != null)
                {
                    var method = jsonObj["method"].ToString();

                    if (method == "notify_gcode_response")
                    {
                        var response = jsonObj["params"]?[0]?.ToString();
                        if (!string.IsNullOrEmpty(response))
                        {
                            _gcodeResponses.OnNext(response);
                        }
                    }
                    else if (method == "notify_status_update")
                    {
                        var status = ParseStatusUpdate(jsonObj);
                        _statusUpdates.OnNext(status);
                    }
                }
            }
            catch (JsonReaderException ex)
            {
                Console.WriteLine($"Failed to parse WebSocket message: {ex.Message}");
            }
        }

        private PrinterStatus ParseStatusUpdate(JObject jsonObj)
        {
            var status = new PrinterStatus();

            try
            {
                var paramsArray = jsonObj["params"] as JArray;
                if (paramsArray != null && paramsArray.Count > 0)
                {
                    var statusData = paramsArray[0] as JObject;

                    if (statusData != null)
                    {
                        // Parse temperatures
                        var extruderElement = statusData["extruder"] as JObject;
                        if (extruderElement != null)
                        {
                            status.ExtruderTemp = extruderElement["temperature"]?.ToObject<double>() ?? 0;
                            status.TargetExtruderTemp = extruderElement["target"]?.ToObject<double>() ?? 0;
                        }

                        var bedElement = statusData["heater_bed"] as JObject;
                        if (bedElement != null)
                        {
                            status.BedTemp = bedElement["temperature"]?.ToObject<double>() ?? 0;
                            status.TargetBedTemp = bedElement["target"]?.ToObject<double>() ?? 0;
                        }

                        // Parse print status
                        var printStatsElement = statusData["print_stats"] as JObject;
                        if (printStatsElement != null)
                        {
                            var stateStr = printStatsElement["state"]?.ToString();
                            if (!string.IsNullOrEmpty(stateStr))
                            {
                                // Используем обычный switch statement
                                switch (stateStr.ToLower())
                                {
                                    case "printing":
                                        status.State = PrinterState.Printing;
                                        break;
                                    case "paused":
                                        status.State = PrinterState.Paused;
                                        break;
                                    case "complete":
                                    case "standby":
                                    case "ready":
                                        status.State = PrinterState.Ready;
                                        break;
                                    case "error":
                                        status.State = PrinterState.Error;
                                        break;
                                    default:
                                        status.State = PrinterState.Unknown;
                                        break;
                                }
                            }

                            status.Filename = printStatsElement["filename"]?.ToString() ?? "";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing status: {ex.Message}");
            }

            return status;
        }

        public async Task DisconnectWebSocketAsync()
        {
            if (!_isWebSocketConnected)
                return;

            _isWebSocketConnected = false;
            _webSocketCts?.Cancel();

            if (_webSocket != null)
            {
                try
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                catch
                {
                    // Ignore
                }

                _webSocket.Dispose();
                _webSocket = null;
            }

            if (_webSocketTask != null)
            {
                try
                {
                    await _webSocketTask;
                }
                catch (OperationCanceledException)
                {
                    // Это ожидаемо при отмене
                }
                catch
                {
                    // Ignore other exceptions
                }

                _webSocketTask = null;
            }

            Console.WriteLine("WebSocket disconnected");
        }

        // ============ STATUS METHODS ============


        public async Task<bool> WaitForTemperatureAsync(string heater = "extruder", double targetTemp = 200, int timeoutSeconds = 300, CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"Waiting for {heater} to reach {targetTemp}°C");

            var startTime = DateTime.Now;
            double lastTemp = 0;

            while (DateTime.Now.Subtract(startTime).TotalSeconds < timeoutSeconds)
            {
                try
                {
                    var temperatures = await GetTemperaturesAsync(cancellationToken);

                    // Try different key names
                    var tempData = temperatures.ContainsKey(heater)
                        ? temperatures[heater]
                        : temperatures.ContainsKey($"{heater}_actual")
                            ? temperatures[$"{heater}_actual"]
                            : null;

                    if (tempData != null)
                    {
                        var currentTemp = tempData.Temperature;

                        if (Math.Abs(currentTemp - lastTemp) > 0.5)
                        {
                            Console.WriteLine($"Current {heater} temperature: {currentTemp:F1}°C");
                            lastTemp = currentTemp;
                        }

                        if (currentTemp >= targetTemp - 2) // ±2°C tolerance
                        {
                            Console.WriteLine($"Temperature reached: {currentTemp:F1}°C");
                            return true;
                        }
                    }

                    await Task.Delay(2000, cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error checking temperature: {ex.Message}");
                    await Task.Delay(5000, cancellationToken);
                }
            }

            Console.WriteLine($"Timeout waiting for temperature {targetTemp}°C");
            return false;
        }

        // ============ DISPOSABLE PATTERN ============
        public async void sendCommand(string command)
        {
            await SendGcodeAsync(command);
        }

        public void sendCommand(string com, string[] vars, object[] vals)
        {
            if (vars.Length != vals.Length)
            {
                return;
            }
            var command = com;
            for (int i = 0; i < vars.Length; i++)
            {
                command += " " + vars[i] + vals[i].ToString();
            }
            sendCommand(command);
        }
        public void Dispose()
        {
            try
            {
                DisconnectWebSocketAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore
            }

            _httpClient?.Dispose();
            _gcodeResponses?.Dispose();
            _statusUpdates?.Dispose();
            _webSocketCts?.Dispose();
        }
    }

    // ============ GCODE MANAGER CLASS ============

    public class GCodeManager
    {
        private readonly MoonrakerClient _client;
        private readonly Dictionary<string, List<string>> _macros;

        public GCodeManager(MoonrakerClient client)
        {
            _client = client;
            _macros = new Dictionary<string, List<string>>();
            LoadStandardMacros();
        }

        private void LoadStandardMacros()
        {
            _macros["bed_level"] = new List<string>
            {
                "G28",
                "G29"
            };

            _macros["purge_line"] = new List<string>
            {
                "G1 X0 Y20 Z0.2 F3000",
                "G1 X100 E20 F500",
                "G1 Z0.5"
            };

            _macros["clean_nozzle"] = new List<string>
            {
                "G1 Z10 F300",
                "G1 X0 Y250 F3000",
                "G4 P5000",
                "G1 X0 Y20 F3000"
            };

            _macros["preheat_pla"] = new List<string>
            {
                "M104 S200",
                "M140 S60"
            };

            _macros["preheat_abs"] = new List<string>
            {
                "M104 S240",
                "M140 S100"
            };

            _macros["cooldown"] = new List<string>
            {
                "M104 S0",
                "M140 S0",
                "M107"
            };
        }

        public async Task<bool> ExecuteMacroAsync(string macroName, CancellationToken cancellationToken = default)
        {
            if (!_macros.TryGetValue(macroName, out var gcodeSequence))
            {
                Console.WriteLine($"Macro '{macroName}' not found");
                return false;
            }

            try
            {
                Console.WriteLine($"Executing macro: {macroName}");

                foreach (var gcode in gcodeSequence)
                {
                    await _client.SendGcodeAsync(gcode, cancellationToken);
                    await Task.Delay(500, cancellationToken);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing macro '{macroName}': {ex.Message}");
                return false;
            }
        }

        public void AddMacro(string name, List<string> gcodeSequence)
        {
            _macros[name] = gcodeSequence;
            Console.WriteLine($"Macro '{name}' added");
        }

        public void RemoveMacro(string name)
        {
            if (_macros.Remove(name))
                Console.WriteLine($"Macro '{name}' removed");
            else
                Console.WriteLine($"Macro '{name}' not found");
        }

        public List<string> GetMacroNames()
        {
            return _macros.Keys.ToList();
        }
    }

    // ============ EXAMPLE PROGRAM ============

    public static class Program
    {
        public static async Task Main(string[] args)
        {
            Console.WriteLine("Moonraker Client for Klipper");
            Console.WriteLine("============================\n");

            // Настройки подключения
            string host = "192.168.1.100";
            int port = 7125;
            string apiKey = null;

            // Если переданы аргументы командной строки
            if (args.Length >= 1)
            {
                host = args[0];
            }
            if (args.Length >= 2)
            {
                if (int.TryParse(args[1], out int customPort))
                {
                    port = customPort;
                }
            }
            if (args.Length >= 3)
            {
                apiKey = args[2];
            }

            var client = new MoonrakerClient(host, port, apiKey);
            var gcodeManager = new GCodeManager(client);

            try
            {
                // 1. Получаем информацию о сервере
                Console.WriteLine("1. Getting server info...");
                var serverInfo = await client.GetServerInfoAsync();
                Console.WriteLine($"✓ Moonraker version: {serverInfo.ServerVersion}");
                Console.WriteLine($"✓ Klipper version: {serverInfo.KlipperVersion}");

                // 2. Получаем информацию о принтере
                Console.WriteLine("\n2. Getting printer info...");
                var printerInfo = await client.GetPrinterInfoAsync();
                Console.WriteLine($"✓ Printer state: {printerInfo.State}");

                // 3. Подключаем WebSocket для обновлений в реальном времени
                Console.WriteLine("\n3. Connecting WebSocket...");
                await client.ConnectWebSocketAsync();

                // Подписываемся на ответы G-кода
                client.GcodeResponses.Subscribe(response =>
                    Console.WriteLine($"[G-code] {response}"));

                // Подписываемся на обновления статуса
                client.StatusUpdates.Subscribe(status =>
                    Console.WriteLine($"[Status] {status.State}, Extruder: {status.ExtruderTemp:F1}°C, Bed: {status.BedTemp:F1}°C"));

                // 4. Показываем главное меню
                await ShowMenuAsync(client, gcodeManager);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                }
            }
            finally
            {
                Console.WriteLine("\nDisconnecting...");
            }

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        private static async Task ShowMenuAsync(MoonrakerClient client, GCodeManager gcodeManager)
        {
            while (true)
            {
                Console.WriteLine("\n" + new string('=', 50));
                Console.WriteLine("PRINTER CONTROL MENU");
                Console.WriteLine(new string('=', 50));
                Console.WriteLine("1. Send G-code");
                Console.WriteLine("2. Execute macro");
                Console.WriteLine("3. Movement control");
                Console.WriteLine("4. Temperature control");
                Console.WriteLine("5. Print management");
                Console.WriteLine("6. Get status");
                Console.WriteLine("7. List files");
                Console.WriteLine("8. Exit");
                Console.Write("\nSelect option: ");

                var choice = Console.ReadLine();

                switch (choice)
                {
                    case "1":
                        await SendGcodeAsync(client);
                        break;
                    case "2":
                        await ExecuteMacroAsync(gcodeManager);
                        break;
                    case "3":
                        await MovementControlAsync(client);
                        break;
                    case "4":
                        await TemperatureControlAsync(client);
                        break;
                    case "5":
                        await PrintManagementAsync(client);
                        break;
                    case "6":
                        await GetStatusAsync(client);
                        break;
                    case "7":
                        await ListFilesAsync(client);
                        break;
                    case "8":
                        return;
                    default:
                        Console.WriteLine("Invalid option");
                        break;
                }
            }
        }

        private static async Task SendGcodeAsync(MoonrakerClient client)
        {
            Console.Write("Enter G-code: ");
            var gcode = Console.ReadLine();

            if (!string.IsNullOrEmpty(gcode))
            {
                try
                {
                    var result = await client.SendGcodeAsync(gcode);
                    Console.WriteLine($"✓ G-code sent: {result}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"✗ Error: {ex.Message}");
                }
            }
        }

        private static async Task ExecuteMacroAsync(GCodeManager gcodeManager)
        {
            Console.WriteLine("\nAvailable macros:");
            var macros = gcodeManager.GetMacroNames();
            for (int i = 0; i < macros.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {macros[i]}");
            }

            Console.Write("\nEnter macro name or number: ");
            var input = Console.ReadLine();

            if (int.TryParse(input, out int index) && index > 0 && index <= macros.Count)
            {
                await gcodeManager.ExecuteMacroAsync(macros[index - 1]);
            }
            else
            {
                await gcodeManager.ExecuteMacroAsync(input);
            }
        }

        private static async Task MovementControlAsync(MoonrakerClient client)
        {
            Console.WriteLine("\nMovement Control:");
            Console.WriteLine("1. Home all");
            Console.WriteLine("2. Home X");
            Console.WriteLine("3. Home Y");
            Console.WriteLine("4. Home Z");
            Console.WriteLine("5. Move absolute");
            Console.WriteLine("6. Move relative");
            Console.Write("\nSelect option: ");

            var choice = Console.ReadLine();

            switch (choice)
            {
                case "1":
                    await client.HomeAllAsync();
                    break;
                case "2":
                    await client.HomeXAsync();
                    break;
                case "3":
                    await client.HomeYAsync();
                    break;
                case "4":
                    await client.HomeZAsync();
                    break;
                case "5":
                    await MoveAbsoluteAsync(client);
                    break;
                case "6":
                    await MoveRelativeAsync(client);
                    break;
                default:
                    Console.WriteLine("Invalid option");
                    break;
            }
        }

        private static async Task MoveAbsoluteAsync(MoonrakerClient client)
        {
            Console.Write("X (press Enter to skip): ");
            var xInput = Console.ReadLine();
            Console.Write("Y (press Enter to skip): ");
            var yInput = Console.ReadLine();
            Console.Write("Z (press Enter to skip): ");
            var zInput = Console.ReadLine();
            Console.Write("E (press Enter to skip): ");
            var eInput = Console.ReadLine();
            Console.Write("Speed (mm/min, optional): ");
            var speedInput = Console.ReadLine();

            double? x = ParseNullableDouble(xInput);
            double? y = ParseNullableDouble(yInput);
            double? z = ParseNullableDouble(zInput);
            double? e = ParseNullableDouble(eInput);
            double? speed = ParseNullableDouble(speedInput);

            await client.MoveAbsoluteAsync(x, y, z, e, speed);
        }

        private static async Task MoveRelativeAsync(MoonrakerClient client)
        {
            Console.Write("ΔX (press Enter to skip): ");
            var xInput = Console.ReadLine();
            Console.Write("ΔY (press Enter to skip): ");
            var yInput = Console.ReadLine();
            Console.Write("ΔZ (press Enter to skip): ");
            var zInput = Console.ReadLine();
            Console.Write("ΔE (press Enter to skip): ");
            var eInput = Console.ReadLine();
            Console.Write("Speed (mm/min, optional): ");
            var speedInput = Console.ReadLine();

            double? x = ParseNullableDouble(xInput);
            double? y = ParseNullableDouble(yInput);
            double? z = ParseNullableDouble(zInput);
            double? e = ParseNullableDouble(eInput);
            double? speed = ParseNullableDouble(speedInput);

            await client.MoveRelativeAsync(x, y, z, e, speed);
        }

        private static double? ParseNullableDouble(string input)
        {
            if (string.IsNullOrEmpty(input))
                return null;

            return double.TryParse(input, out double result) ? result : (double?)null;
        }

        private static async Task TemperatureControlAsync(MoonrakerClient client)
        {
            Console.WriteLine("\nTemperature Control:");
            Console.WriteLine("1. Set extruder temperature");
            Console.WriteLine("2. Set bed temperature");
            Console.WriteLine("3. Get temperatures");
            Console.WriteLine("4. Preheat PLA");
            Console.WriteLine("5. Preheat ABS");
            Console.WriteLine("6. Cooldown");
            Console.Write("\nSelect option: ");

            var choice = Console.ReadLine();

            switch (choice)
            {
                case "1":
                    Console.Write("Temperature (°C): ");
                    if (double.TryParse(Console.ReadLine(), out double extruderTemp))
                        await client.SetExtruderTempAsync(extruderTemp);
                    break;
                case "2":
                    Console.Write("Temperature (°C): ");
                    if (double.TryParse(Console.ReadLine(), out double bedTemp))
                        await client.SetBedTempAsync(bedTemp);
                    break;
                case "3":
                    var temps = await client.GetTemperaturesAsync();
                    if (temps.Count == 0)
                    {
                        Console.WriteLine("No temperature data available");
                    }
                    else
                    {
                        foreach (var temp in temps)
                        {
                            Console.WriteLine($"{temp.Key}: {temp.Value.Temperature:F1}°C / {temp.Value.Target:F1}°C");
                        }
                    }
                    break;
                case "4":
                    await client.SetExtruderTempAsync(200);
                    await client.SetBedTempAsync(60);
                    break;
                case "5":
                    await client.SetExtruderTempAsync(240);
                    await client.SetBedTempAsync(100);
                    break;
                case "6":
                    await client.SetExtruderTempAsync(0);
                    await client.SetBedTempAsync(0);
                    await client.FanOffAsync();
                    break;
                default:
                    Console.WriteLine("Invalid option");
                    break;
            }
        }

        private static async Task PrintManagementAsync(MoonrakerClient client)
        {
            Console.WriteLine("\nPrint Management:");
            Console.WriteLine("1. List files");
            Console.WriteLine("2. Start print");
            Console.WriteLine("3. Pause print");
            Console.WriteLine("4. Resume print");
            Console.WriteLine("5. Cancel print");
            Console.Write("\nSelect option: ");

            var choice = Console.ReadLine();

            switch (choice)
            {
                case "1":
                    await ListFilesAsync(client);
                    break;
                case "2":
                    Console.Write("Filename: ");
                    var filename = Console.ReadLine();
                    //await client.PrintFileAsync(filename);
                    break;
                case "3":
                    await client.PausePrintAsync();
                    break;
                case "4":
                    await client.ResumePrintAsync();
                    break;
                case "5":
                    await client.CancelPrintAsync();
                    break;
                default:
                    Console.WriteLine("Invalid option");
                    break;
            }
        }

        private static async Task GetStatusAsync(MoonrakerClient client)
        {
            try
            {
                var status = await client.GetFullStatusAsync();
                Console.WriteLine("\nPrinter Status:");
                Console.WriteLine($"State: {status.State}");
                Console.WriteLine($"Extruder: {status.ExtruderTemp:F1}°C / {status.TargetExtruderTemp:F1}°C");
                Console.WriteLine($"Bed: {status.BedTemp:F1}°C / {status.TargetBedTemp:F1}°C");

                if (!string.IsNullOrEmpty(status.Filename))
                {
                    Console.WriteLine($"File: {status.Filename}");
                    Console.WriteLine($"Progress: {status.PrintProgress:F1}%");
                    Console.WriteLine($"Elapsed: {status.PrintTime:F0}s");
                    Console.WriteLine($"Remaining: {status.PrintTimeLeft:F0}s");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error getting status: {ex.Message}");
            }
        }

        private static async Task ListFilesAsync(MoonrakerClient client)
        {
            try
            {
                var files = await client.ListFilesAsync();
                Console.WriteLine($"\nFiles on printer ({files.Count} total):");
                foreach (var file in files)
                {
                    Console.WriteLine($"  {file.Path} ({(file.Size / 1024):N0} KB)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error listing files: {ex.Message}");
            }
        }
    }
}



/*
public async void sendCommand(string command)
{
    await SendGcodeAsync(command);
}

public void sendCommand(string com, string[] vars, object[] vals)
{
    if (vars.Length != vals.Length)
    {
        return;
    }
    var command = com;
    for (int i = 0; i < vars.Length; i++)
    {
        command += " " + vars[i] + vals[i].ToString();
    }
    sendCommand(command);
}

*/