using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SakoDesk
{
    // =========================================================================
    // INTERNET TUNNEL MANAGER (Allows connecting from phone over 4G/5G/LTE anywhere)
    // Uses free instant SSH reverse tunnel (localhost.run / pinggy.io) with zero install!
    // =========================================================================
    public class InternetTunnelManager
    {
        private Process _sshProc;
        public string PublicUrl { get; private set; }
        public bool IsRunning => _sshProc != null && !_sshProc.HasExited;
        public event Action<string> OnTunnelReady;
        public event Action<string> OnLog;

        public async Task<string> StartTunnelAsync(int localPort = 9880)
        {
            StopTunnel();
            OnLog?.Invoke("🌐 Запуск глобального Интернет-туннеля (для доступа с телефона через 4G/5G из любой точки мира)...");

            var tcs = new TaskCompletionSource<string>();

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "ssh",
                    Arguments = $"-o StrictHostKeyChecking=no -o ServerAliveInterval=30 -R 80:127.0.0.1:{localPort} nokey@localhost.run",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                _sshProc = new Process { StartInfo = psi, EnableRaisingEvents = true };

                void ParseLine(string line)
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    var m = Regex.Match(line, @"https://[a-zA-Z0-9\-]+\.lhr\.life");
                    if (!m.Success)
                        m = Regex.Match(line, @"https://[a-zA-Z0-9\-\.]+\.(localhost\.run|lhr\.life)");

                    if (m.Success && string.IsNullOrEmpty(PublicUrl))
                    {
                        PublicUrl = m.Value;
                        OnLog?.Invoke($"🌍 Глобальная ссылка для телефона готова: {PublicUrl}");
                        OnTunnelReady?.Invoke(PublicUrl);
                        tcs.TrySetResult(PublicUrl);
                    }
                }

                _sshProc.OutputDataReceived += (s, e) => ParseLine(e.Data);
                _sshProc.ErrorDataReceived += (s, e) => ParseLine(e.Data);

                _sshProc.Start();
                _sshProc.BeginOutputReadLine();
                _sshProc.BeginErrorReadLine();

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(12000));
                if (completed == tcs.Task)
                    return await tcs.Task;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"Ошибка запуска интернет-туннеля: {ex.Message}");
            }

            return PublicUrl;
        }

        public void StopTunnel()
        {
            try
            {
                PublicUrl = null;
                if (_sshProc != null && !_sshProc.HasExited)
                {
                    _sshProc.Kill(true);
                    _sshProc.Dispose();
                }
                _sshProc = null;
            }
            catch { }
        }
    }

    // =========================================================================
    // MOBILE & WEB SERVER (HTTP + WebSocket on port 9880 — zero firewall popup!)
    // Unlimited Remote Control from Android / iPhone / iPad / any browser
    // =========================================================================
    public class MobileWebServer
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly Func<string> _getPassword;
        private readonly Func<bool> _getAllowControl;
        private readonly Func<string> _getDeviceId;
        private readonly Action<string> _log;

        public int Port { get; private set; } = 9880;
        public bool IsRunning => _listener != null;

        public MobileWebServer(Func<string> getPassword, Func<bool> getAllowControl, Func<string> getDeviceId, Action<string> log)
        {
            _getPassword = getPassword;
            _getAllowControl = getAllowControl;
            _getDeviceId = getDeviceId;
            _log = log;
        }

        public void Start(int port = 9880)
        {
            if (IsRunning) return;
            Port = port;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _log($"📱 Мобильный Веб-сервер запущен (порт {Port}). Доступ с телефона открыт без ограничений!");
            Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener = null;
            }
            catch { }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(ct);
                    client.NoDelay = true;
                    _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                string headerText = await ReadHttpHeadersAsync(stream, ct);
                if (string.IsNullOrEmpty(headerText)) return;

                string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;

                bool isWebSocket = headerText.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase);
                string wsKey = null;

                foreach (var line in lines)
                {
                    if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                        wsKey = line.Substring("Sec-WebSocket-Key:".Length).Trim();
                }

                if (isWebSocket && !string.IsNullOrEmpty(wsKey))
                {
                    string acceptKey = ComputeWebSocketAcceptKey(wsKey);
                    string response =
                        "HTTP/1.1 101 Switching Protocols\r\n" +
                        "Upgrade: websocket\r\n" +
                        "Connection: Upgrade\r\n" +
                        $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";

                    byte[] respBytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(respBytes, 0, respBytes.Length, ct);
                    await stream.FlushAsync(ct);

                    using WebSocket ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(15));
                    await HandleWebSocketSessionAsync(ws, client.Client.RemoteEndPoint?.ToString() ?? "Телефон", ct);
                }
                else if (lines[0].Contains("GET /SakoDesk.apk", StringComparison.OrdinalIgnoreCase))
                {
                    string apkPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "SakoDesk.apk");
                    if (!File.Exists(apkPath))
                        apkPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SakoDesk.apk");

                    if (File.Exists(apkPath))
                    {
                        byte[] apkBytes = await File.ReadAllBytesAsync(apkPath, ct);
                        string hdr =
                            "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: application/vnd.android.package-archive\r\n" +
                            "Content-Disposition: attachment; filename=\"SakoDesk.apk\"\r\n" +
                            $"Content-Length: {apkBytes.Length}\r\n" +
                            "Connection: close\r\n\r\n";
                        byte[] hdrBytes = Encoding.UTF8.GetBytes(hdr);
                        await stream.WriteAsync(hdrBytes, 0, hdrBytes.Length, ct);
                        await stream.WriteAsync(apkBytes, 0, apkBytes.Length, ct);
                        await stream.FlushAsync(ct);
                        _log("📲 Файл SakoDesk.apk скачан на телефон!");
                    }
                }
                else
                {
                    string html = GetMobileHtmlPage(_getDeviceId(), Environment.MachineName);
                    byte[] body = Encoding.UTF8.GetBytes(html);
                    string httpHeader =
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: text/html; charset=utf-8\r\n" +
                        "Cache-Control: no-cache\r\n" +
                        $"Content-Length: {body.Length}\r\n" +
                        "Connection: close\r\n\r\n";

                    byte[] hdrBytes = Encoding.UTF8.GetBytes(httpHeader);
                    await stream.WriteAsync(hdrBytes, 0, hdrBytes.Length, ct);
                    await stream.WriteAsync(body, 0, body.Length, ct);
                    await stream.FlushAsync(ct);
                }
            }
        }

        private static async Task<string> ReadHttpHeadersAsync(NetworkStream stream, CancellationToken ct)
        {
            using MemoryStream ms = new MemoryStream();
            byte[] buf = new byte[1];
            int matchState = 0;
            while (ms.Length < 16384)
            {
                int r = await stream.ReadAsync(buf, 0, 1, ct);
                if (r == 0) break;
                ms.WriteByte(buf[0]);
                byte b = buf[0];
                if ((matchState == 0 || matchState == 2) && b == '\r') matchState++;
                else if ((matchState == 1 || matchState == 3) && b == '\n') matchState++;
                else matchState = 0;

                if (matchState == 4) break;
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static string ComputeWebSocketAcceptKey(string key)
        {
            string magic = key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(magic));
            return Convert.ToBase64String(hash);
        }

        // =====================================================================
        // WEBSOCKET SESSION WITH PHONE (UNLIMITED CONTROL: MOUSE, KEYS, HOTKEYS,
        // REMOTE CMD/POWERSHELL, FILE UPLOAD, SYSTEM POWER ACTIONS)
        // =====================================================================
        private async Task HandleWebSocketSessionAsync(WebSocket ws, string remoteEndpoint, CancellationToken ct)
        {
            bool authenticated = false;
            int jpegQuality = 60;
            float scaleFactor = 0.80f;
            int targetFps = 24;

            SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
            using CancellationTokenSource sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            async Task SendTextAsync(string json)
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                await sendLock.WaitAsync(sessionCts.Token);
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, sessionCts.Token);
                }
                finally { sendLock.Release(); }
            }

            async Task SendBinaryFrameAsync(byte[] jpegBytes)
            {
                await sendLock.WaitAsync(sessionCts.Token);
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.SendAsync(new ArraySegment<byte>(jpegBytes), WebSocketMessageType.Binary, true, sessionCts.Token);
                }
                finally { sendLock.Release(); }
            }

            try
            {
                byte[] buffer = new byte[256 * 1024];
                while (ws.State == WebSocketState.Open && !sessionCts.Token.IsCancellationRequested)
                {
                    using MemoryStream ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), sessionCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            sessionCts.Cancel();
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    string msg = Encoding.UTF8.GetString(ms.ToArray());
                    using JsonDocument doc = JsonDocument.Parse(msg);
                    JsonElement root = doc.RootElement;
                    string type = root.GetProperty("type").GetString();

                    if (type == "auth")
                    {
                        string pass = root.GetProperty("password").GetString()?.Trim() ?? "";
                        if (pass == _getPassword())
                        {
                            authenticated = true;
                            Rectangle b = Screen.PrimaryScreen.Bounds;
                            _log($"📱 Телефон ({remoteEndpoint}) успешно авторизован (полный доступ)!");
                            await SendTextAsync(JsonSerializer.Serialize(new
                            {
                                type = "auth_ok",
                                machine = Environment.MachineName,
                                width = b.Width,
                                height = b.Height
                            }));

                            _ = Task.Run(async () =>
                            {
                                ImageCodecInfo jpegEncoder = ImageCodecInfo.GetImageDecoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                                while (!sessionCts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
                                {
                                    var sw = Stopwatch.StartNew();
                                    try
                                    {
                                        Rectangle bounds = Screen.PrimaryScreen.Bounds;
                                        using Bitmap rawBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                                        using (Graphics g = Graphics.FromImage(rawBmp))
                                        {
                                            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                                            NativeInput.DrawCursorOnGraphics(g, bounds);
                                        }

                                        using MemoryStream frameStream = new MemoryStream();
                                        using EncoderParameters encParams = new EncoderParameters(1);
                                        encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)jpegQuality);

                                        int swW = Math.Max(320, (int)(bounds.Width * scaleFactor));
                                        int swH = Math.Max(200, (int)(bounds.Height * scaleFactor));
                                        using Bitmap scaled = new Bitmap(swW, swH, PixelFormat.Format24bppRgb);
                                        using (Graphics gs = Graphics.FromImage(scaled))
                                        {
                                            gs.InterpolationMode = InterpolationMode.Bilinear;
                                            gs.DrawImage(rawBmp, 0, 0, swW, swH);
                                        }
                                        scaled.Save(frameStream, jpegEncoder, encParams);

                                        await SendBinaryFrameAsync(frameStream.ToArray());
                                    }
                                    catch { break; }

                                    int delay = Math.Max(8, (1000 / Math.Max(5, targetFps)) - (int)sw.ElapsedMilliseconds);
                                    await Task.Delay(delay, sessionCts.Token);
                                }
                            }, sessionCts.Token);
                        }
                        else
                        {
                            _log($"❌ Телефон ({remoteEndpoint}) ввел неверный пароль.");
                            await SendTextAsync("{\"type\":\"auth_fail\",\"msg\":\"Неверный пароль доступа!\"}");
                        }
                    }
                    else if (!authenticated)
                    {
                        continue;
                    }
                    else if (type == "mouse" && _getAllowControl())
                    {
                        float nx = (float)root.GetProperty("x").GetDouble();
                        float ny = (float)root.GetProperty("y").GetDouble();
                        string act = root.GetProperty("action").GetString();

                        Rectangle b = Screen.PrimaryScreen.Bounds;
                        int absX = b.X + (int)(Math.Clamp(nx, 0f, 1f) * b.Width);
                        int absY = b.Y + (int)(Math.Clamp(ny, 0f, 1f) * b.Height);

                        NativeInput.SetCursorPos(absX, absY);
                        switch (act)
                        {
                            case "click":
                                NativeInput.mouse_event(NativeInput.MOUSEEVENTF_LEFTDOWN, absX, absY, 0, UIntPtr.Zero);
                                NativeInput.mouse_event(NativeInput.MOUSEEVENTF_LEFTUP, absX, absY, 0, UIntPtr.Zero);
                                break;
                            case "dblclick":
                                NativeInput.mouse_event(NativeInput.MOUSEEVENTF_LEFTDOWN, absX, absY, 0, UIntPtr.Zero);
                                NativeInput.mouse_event(NativeInput.MOUSEEVENTF_LEFTUP, absX, absY, 0, UIntPtr.Zero);
                                await Task.Delay(40);
                                NativeInput.mouse_event(NativeInput.MOUSEEVENTF_LEFTDOWN, absX, absY, 0, UIntPtr.Zero);
                                NativeInput.mouse_event(NativeInput.MOUSEEVENTF_LEFTUP, absX, absY, 0, UIntPtr.Zero);
                                break;
                            case "rclick":
                                NativeInput.mouse_event(NativeInput.MOUSEEVENTF_RIGHTDOWN, absX, absY, 0, UIntPtr.Zero);
                                NativeInput.mouse_event(NativeInput.MOUSEEVENTF_RIGHTUP, absX, absY, 0, UIntPtr.Zero);
                                break;
                            case "down":
                                NativeInput.mouse_event(NativeInput.MOUSEEVENTF_LEFTDOWN, absX, absY, 0, UIntPtr.Zero);
                                break;
                            case "up":
                                NativeInput.mouse_event(NativeInput.MOUSEEVENTF_LEFTUP, absX, absY, 0, UIntPtr.Zero);
                                break;
                            case "wheel":
                                int delta = root.TryGetProperty("delta", out var dEl) ? dEl.GetInt32() : -120;
                                NativeInput.mouse_event(NativeInput.MOUSEEVENTF_WHEEL, absX, absY, delta, UIntPtr.Zero);
                                break;
                        }
                    }
                    else if (type == "text" && _getAllowControl())
                    {
                        string text = root.GetProperty("text").GetString() ?? "";
                        if (!string.IsNullOrEmpty(text))
                            SendKeys.SendWait(text);
                    }
                    else if (type == "key" && _getAllowControl())
                    {
                        int vk = root.GetProperty("vk").GetInt32();
                        NativeInput.keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
                        NativeInput.keybd_event((byte)vk, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }
                    else if (type == "hotkey" && _getAllowControl())
                    {
                        // Supports system combinations: alt_f4, win_d, ctrl_c, ctrl_v, taskmgr, alt_tab, lock
                        string hk = root.GetProperty("combo").GetString() ?? "";
                        ExecuteHotkeyCombo(hk);
                    }
                    else if (type == "cmd" && _getAllowControl())
                    {
                        string command = root.GetProperty("command").GetString() ?? "";
                        _log($"📱 Команда терминала с телефона: {command}");
                        string output = await RunCommandCaptureAsync(command);
                        await SendTextAsync(JsonSerializer.Serialize(new { type = "cmd_out", output }));
                    }
                    else if (type == "file" && _getAllowControl())
                    {
                        string fileName = Path.GetFileName(root.GetProperty("name").GetString() ?? "file.bin");
                        string b64 = root.GetProperty("data").GetString() ?? "";
                        byte[] bytes = Convert.FromBase64String(b64);
                        string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SakoDesk_Received");
                        Directory.CreateDirectory(saveDir);
                        string fullPath = Path.Combine(saveDir, fileName);
                        await File.WriteAllBytesAsync(fullPath, bytes);
                        _log($"📱 Получен файл с телефона: {fileName} ({bytes.Length / 1024.0:F1} КБ)");
                    }
                    else if (type == "quality")
                    {
                        jpegQuality = root.GetProperty("q").GetInt32();
                        scaleFactor = (float)root.GetProperty("s").GetDouble();
                        targetFps = root.GetProperty("fps").GetInt32();
                    }
                }
            }
            catch { }
            finally
            {
                sessionCts.Cancel();
                if (authenticated)
                    _log($"📱 Телефон ({remoteEndpoint}) отключился.");
            }
        }

        private static void ExecuteHotkeyCombo(string combo)
        {
            const byte VK_MENU = 0x12;    // Alt
            const byte VK_CONTROL = 0x11; // Ctrl
            const byte VK_SHIFT = 0x10;   // Shift
            const byte VK_LWIN = 0x5B;    // Win
            const byte VK_F4 = 0x73;
            const byte VK_TAB = 0x09;
            const byte VK_ESCAPE = 0x1B;
            const byte VK_D = 0x44;
            const byte VK_C = 0x43;
            const byte VK_V = 0x56;

            switch (combo)
            {
                case "win":
                    NativeInput.keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_LWIN, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    break;
                case "win_d": // Show Desktop
                    NativeInput.keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_D, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_D, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_LWIN, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    break;
                case "alt_f4":
                    NativeInput.keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_F4, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_F4, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_MENU, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    break;
                case "alt_tab":
                    NativeInput.keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_TAB, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_TAB, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_MENU, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    break;
                case "taskmgr": // Ctrl+Shift+Esc
                    NativeInput.keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_ESCAPE, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_SHIFT, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_CONTROL, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    break;
                case "ctrl_c":
                    NativeInput.keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_C, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_C, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_CONTROL, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    break;
                case "ctrl_v":
                    NativeInput.keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_V, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_V, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    NativeInput.keybd_event(VK_CONTROL, 0, NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    break;
            }
        }

        private static async Task<string> RunCommandCaptureAsync(string cmd)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -Command \"{cmd.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using Process p = Process.Start(psi);
                string stdout = await p.StandardOutput.ReadToEndAsync();
                string stderr = await p.StandardError.ReadToEndAsync();
                return (stdout + "\n" + stderr).Trim();
            }
            catch (Exception ex)
            {
                return "Ошибка: " + ex.Message;
            }
        }

        // =====================================================================
        // EMBEDDED MOBILE WEB CLIENT (TOUCHPAD, DRAG&DROP, HOTKEYS, TERMINAL, FILES)
        // =====================================================================
        private static string GetMobileHtmlPage(string hostId, string machineName)
        {
            return @"<!DOCTYPE html>
<html lang='ru'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no'>
<title>Sako Desk Mobile — " + machineName + @"</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; user-select: none; -webkit-user-select: none; }
  body { background: #0f111a; color: #eef2f7; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; overflow: hidden; height: 100dvh; display: flex; flex-direction: column; }
  
  #topbar { height: 44px; background: #181b28; display: flex; align-items: center; justify-content: space-between; padding: 0 10px; border-bottom: 1px solid #2a2f45; z-index: 20; }
  .brand { font-weight: 800; color: #ff4757; font-size: 14px; display: flex; align-items: center; gap: 6px; }
  .stats { font-family: monospace; font-size: 12px; color: #2ed573; }
  
  #viewport { flex: 1; position: relative; background: #000; display: flex; align-items: center; justify-content: center; overflow: hidden; touch-action: none; }
  #screen { max-width: 100%; max-height: 100%; object-fit: contain; pointer-events: none; }
  #vcursor { position: absolute; width: 18px; height: 18px; border: 2px solid #ff4757; background: rgba(255,71,87,0.35); border-radius: 50%; transform: translate(-50%, -50%); pointer-events: none; display: none; z-index: 10; }

  #dock { background: #181b28; border-top: 1px solid #2a2f45; padding: 6px; display: flex; flex-direction: column; gap: 5px; z-index: 20; }
  .btn-row { display: flex; gap: 5px; justify-content: space-between; }
  button, select { flex: 1; background: #262b3d; color: #fff; border: 1px solid #38405a; border-radius: 7px; padding: 9px 4px; font-size: 12px; font-weight: 600; cursor: pointer; }
  button:active { background: #ff4757; border-color: #ff4757; }
  button.active-mode { background: #2ed573; color: #000; border-color: #2ed573; }

  #authModal { position: fixed; inset: 0; background: rgba(12,14,22,0.96); display: flex; align-items: center; justify-content: center; z-index: 100; padding: 20px; }
  .card { background: #1c2030; border: 1px solid #2f3650; border-radius: 14px; padding: 24px; width: 100%; max-width: 360px; text-align: center; box-shadow: 0 12px 35px rgba(0,0,0,0.6); }
  .card h2 { color: #ff4757; margin-bottom: 6px; font-size: 22px; }
  .card p { color: #9aa5be; font-size: 13px; margin-bottom: 16px; }
  .id-badge { background: #121520; padding: 10px; border-radius: 8px; font-family: monospace; font-size: 17px; color: #ff6348; font-weight: bold; margin-bottom: 16px; border: 1px solid #2a3048; }
  input[type=password], input[type=text] { width: 100%; padding: 12px; border-radius: 8px; border: 1px solid #3b4362; background: #11131c; color: #2ed573; font-size: 17px; text-align: center; font-family: monospace; margin-bottom: 14px; outline: none; }
  .btn-primary { width: 100%; background: #ff4757; border: none; padding: 13px; font-size: 16px; font-weight: bold; border-radius: 8px; color: #fff; }
  #errMsg { color: #ff6b81; font-size: 13px; margin-top: 10px; min-height: 18px; }

  .drawer { display: none; gap: 5px; flex-direction: column; background: #131622; padding: 6px; border-radius: 8px; border: 1px solid #2b3148; }
  .drawer.show { display: flex; }
  .drawer-row { display: flex; gap: 5px; }
  #txtInput, #cmdInput { flex: 3; font-size: 14px; margin-bottom: 0; text-align: left; padding: 8px 10px; color: #fff; }
  #cmdOutput { max-height: 110px; overflow-y: auto; font-family: monospace; font-size: 11px; color: #7bed9f; background: #0b0d14; padding: 6px; border-radius: 6px; white-space: pre-wrap; user-select: text; }
</style>
</head>
<body>

<div id='authModal'>
  <div class='card'>
    <h2>⚡ SAKO DESK PRO</h2>
    <p>Полное управление ПК без ограничений</p>
    <div class='id-badge'>ПК: " + machineName + @"<br>ID: " + hostId + @"</div>
    <input type='password' id='passInput' placeholder='Введите пароль с ПК' autocomplete='off'>
    <button class='btn-primary' onclick='doAuth()'>ПОДКЛЮЧИТЬСЯ ➜</button>
    <a href='/SakoDesk.apk' style='display:block;margin-top:12px;color:#2ed573;font-size:13px;font-weight:bold;text-decoration:none;'>📲 Скачать приложение SakoDesk.apk на Android</a>
    <div id='errMsg'></div>
  </div>
</div>

<div id='topbar'>
  <div class='brand'>⚡ " + machineName + @"</div>
  <div class='stats' id='statsLbl'>Ожидание...</div>
  <button style='flex:0;padding:5px 10px;' onclick='toggleFullscreen()'>⛶ Полный экран</button>
</div>

<div id='viewport'>
  <img id='screen' alt='Удаленный экран'>
  <div id='vcursor'></div>
</div>

<div id='dock'>
  <!-- Keyboard Drawer -->
  <div id='kbdDrawer' class='drawer'>
    <div class='drawer-row'>
      <input type='text' id='txtInput' placeholder='Текст для печати на ПК...'>
      <button onclick='sendText()'>Отпр.</button>
      <button onclick='sendVk(13)'>Enter</button>
      <button onclick='sendVk(8)'>⌫</button>
    </div>
  </div>

  <!-- Hotkeys & System Drawer -->
  <div id='sysDrawer' class='drawer'>
    <div class='drawer-row'>
      <button onclick='sendHotkey(""win_d"")'>🖥 Свернуть все</button>
      <button onclick='sendHotkey(""alt_tab"")'>⇄ Alt+Tab</button>
      <button onclick='sendHotkey(""alt_f4"")'>✖ Alt+F4</button>
      <button onclick='sendHotkey(""taskmgr"")'>📊 Диспетчер</button>
    </div>
    <div class='drawer-row'>
      <button onclick='sendHotkey(""win"")'>🪟 Пуск</button>
      <button onclick='sendHotkey(""ctrl_c"")'>Копир.</button>
      <button onclick='sendHotkey(""ctrl_v"")'>Встав.</button>
      <button onclick='document.getElementById(""filePicker"").click()'>📁 Файл на ПК</button>
      <input type='file' id='filePicker' style='display:none' onchange='uploadFile(this)'>
    </div>
  </div>

  <!-- Remote Terminal (PowerShell) Drawer -->
  <div id='termDrawer' class='drawer'>
    <div id='cmdOutput'>PowerShell терминал ПК готов...</div>
    <div class='drawer-row'>
      <input type='text' id='cmdInput' placeholder='Команда PowerShell / CMD...'>
      <button onclick='sendCmd()'>Выполнить</button>
    </div>
  </div>

  <div class='btn-row'>
    <button id='btnTouchMode' class='active-mode' onclick='toggleTouchMode()'>👆 Касание</button>
    <button onclick='triggerClick(""click"")'>ЛКМ</button>
    <button onclick='triggerClick(""dblclick"")'>2x Клик</button>
    <button onclick='triggerClick(""rclick"")'>ПКМ</button>
    <button id='btnDrag' onclick='toggleDrag()'>✊ Зажать</button>
  </div>
  <div class='btn-row'>
    <button onclick='toggleDrawer(""kbdDrawer"")'>⌨ Текст</button>
    <button onclick='toggleDrawer(""sysDrawer"")'>⚡ Система</button>
    <button onclick='toggleDrawer(""termDrawer"")'>💻 Терминал</button>
    <button onclick='sendScroll(240)'>⬆</button>
    <button onclick='sendScroll(-240)'>⬇</button>
    <select id='qualSel' onchange='changeQuality()'>
      <option value='85|1.0|30'>Ultra 30fps</option>
      <option value='60|0.8|24' selected>Быстрое</option>
      <option value='35|0.5|15'>Эконом 4G</option>
    </select>
  </div>
</div>

<script>
let ws = null;
let touchMode = 'direct';
let isDragging = false;
let curX = 0.5, curY = 0.5;
let lastTouchX = 0, lastTouchY = 0;
let frameCount = 0;
let lastFpsCheck = Date.now();
let currentBlobUrl = null;

const screenImg = document.getElementById('screen');
const viewport = document.getElementById('viewport');
const vcursor = document.getElementById('vcursor');

function doAuth() {
  const pass = document.getElementById('passInput').value.trim();
  if (!pass) return;
  document.getElementById('errMsg').innerText = 'Подключение...';

  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  ws = new WebSocket(proto + '//' + location.host + '/ws');
  ws.binaryType = 'arraybuffer';

  ws.onopen = () => {
    ws.send(JSON.stringify({ type: 'auth', password: pass }));
  };

  ws.onmessage = (evt) => {
    if (typeof evt.data === 'string') {
      const msg = JSON.parse(evt.data);
      if (msg.type === 'auth_ok') {
        document.getElementById('authModal').style.display = 'none';
      } else if (msg.type === 'auth_fail') {
        document.getElementById('errMsg').innerText = msg.msg;
      } else if (msg.type === 'cmd_out') {
        const out = document.getElementById('cmdOutput');
        out.innerText = msg.output || '(Выполнено без вывода)';
        out.scrollTop = out.scrollHeight;
      }
    } else {
      const blob = new Blob([evt.data], { type: 'image/jpeg' });
      const url = URL.createObjectURL(blob);
      screenImg.onload = () => {
        if (currentBlobUrl) URL.revokeObjectURL(currentBlobUrl);
        currentBlobUrl = url;
      };
      screenImg.src = url;

      frameCount++;
      const now = Date.now();
      if (now - lastFpsCheck >= 1000) {
        document.getElementById('statsLbl').innerText = '🟢 ' + frameCount + ' FPS';
        frameCount = 0;
        lastFpsCheck = now;
      }
    }
  };

  ws.onclose = () => {
    document.getElementById('statsLbl').innerText = '🔴 Отключено';
  };
}

function toggleFullscreen() {
  if (!document.fullscreenElement) document.documentElement.requestFullscreen();
  else document.exitFullscreen();
}

function mapClientToNorm(clientX, clientY) {
  const rect = screenImg.getBoundingClientRect();
  if (rect.width <= 0 || rect.height <= 0) return { x: 0.5, y: 0.5 };
  const nx = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
  const ny = Math.max(0, Math.min(1, (clientY - rect.top) / rect.height));
  return { x: nx, y: ny };
}

function updateVirtualCursor() {
  const rect = screenImg.getBoundingClientRect();
  const vpRect = viewport.getBoundingClientRect();
  const px = (rect.left - vpRect.left) + curX * rect.width;
  const py = (rect.top - vpRect.top) + curY * rect.height;
  vcursor.style.left = px + 'px';
  vcursor.style.top = py + 'px';
}

function sendMouse(action, x, y, delta = 0) {
  if (!ws || ws.readyState !== 1) return;
  ws.send(JSON.stringify({ type: 'mouse', action, x, y, delta }));
}

let tapStartTime = 0;
let tapMoved = false;

viewport.addEventListener('touchstart', (e) => {
  if (e.touches.length === 1) {
    const t = e.touches[0];
    lastTouchX = t.clientX;
    lastTouchY = t.clientY;
    tapStartTime = Date.now();
    tapMoved = false;

    if (touchMode === 'direct') {
      const norm = mapClientToNorm(t.clientX, t.clientY);
      curX = norm.x; curY = norm.y;
      sendMouse('move', curX, curY);
    }
  }
}, { passive: false });

viewport.addEventListener('touchmove', (e) => {
  e.preventDefault();
  if (e.touches.length === 1) {
    const t = e.touches[0];
    const dx = t.clientX - lastTouchX;
    const dy = t.clientY - lastTouchY;
    if (Math.abs(dx) > 4 || Math.abs(dy) > 4) tapMoved = true;

    if (touchMode === 'direct') {
      const norm = mapClientToNorm(t.clientX, t.clientY);
      curX = norm.x; curY = norm.y;
    } else {
      curX = Math.max(0, Math.min(1, curX + dx / 350));
      curY = Math.max(0, Math.min(1, curY + dy / 350));
      updateVirtualCursor();
    }
    lastTouchX = t.clientX;
    lastTouchY = t.clientY;
    sendMouse('move', curX, curY);
  }
}, { passive: false });

viewport.addEventListener('touchend', (e) => {
  const dt = Date.now() - tapStartTime;
  if (!tapMoved && dt < 280) {
    sendMouse('click', curX, curY);
  } else if (!tapMoved && dt >= 500) {
    sendMouse('rclick', curX, curY);
  }
});

function toggleTouchMode() {
  const btn = document.getElementById('btnTouchMode');
  if (touchMode === 'direct') {
    touchMode = 'trackpad';
    btn.innerText = '🖱 Тачпад';
    vcursor.style.display = 'block';
    updateVirtualCursor();
  } else {
    touchMode = 'direct';
    btn.innerText = '👆 Касание';
    vcursor.style.display = 'none';
  }
}

function toggleDrag() {
  isDragging = !isDragging;
  const btn = document.getElementById('btnDrag');
  btn.classList.toggle('active-mode', isDragging);
  sendMouse(isDragging ? 'down' : 'up', curX, curY);
}

function triggerClick(act) { sendMouse(act, curX, curY); }
function sendScroll(delta) { sendMouse('wheel', curX, curY, delta); }

function toggleDrawer(id) {
  ['kbdDrawer', 'sysDrawer', 'termDrawer'].forEach(d => {
    if (d === id) document.getElementById(d).classList.toggle('show');
    else document.getElementById(d).classList.remove('show');
  });
}

function sendText() {
  const inp = document.getElementById('txtInput');
  if (inp.value && ws && ws.readyState === 1) {
    ws.send(JSON.stringify({ type: 'text', text: inp.value }));
    inp.value = '';
  }
}

function sendVk(vk) {
  if (ws && ws.readyState === 1) ws.send(JSON.stringify({ type: 'key', vk: vk }));
}

function sendHotkey(combo) {
  if (ws && ws.readyState === 1) ws.send(JSON.stringify({ type: 'hotkey', combo: combo }));
}

function sendCmd() {
  const inp = document.getElementById('cmdInput');
  if (inp.value && ws && ws.readyState === 1) {
    document.getElementById('cmdOutput').innerText = 'Выполнение: ' + inp.value + '...';
    ws.send(JSON.stringify({ type: 'cmd', command: inp.value }));
    inp.value = '';
  }
}

function uploadFile(input) {
  if (!input.files || !input.files[0]) return;
  const file = input.files[0];
  const reader = new FileReader();
  reader.onload = () => {
    const b64 = reader.result.split(',')[1];
    ws.send(JSON.stringify({ type: 'file', name: file.name, data: b64 }));
    alert('Файл ' + file.name + ' отправлен на рабочий стол ПК!');
  };
  reader.readAsDataURL(file);
}

function changeQuality() {
  const val = document.getElementById('qualSel').value.split('|');
  if (ws && ws.readyState === 1) {
    ws.send(JSON.stringify({ type: 'quality', q: parseInt(val[0]), s: parseFloat(val[1]), fps: parseInt(val[2]) }));
  }
}
</script>
</body>
</html>";
        }
    }
}
