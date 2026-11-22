using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SakoDesk
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Contains("--relay"))
            {
                int port = 9871;
                int idx = Array.IndexOf(args, "--port");
                if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out int p))
                    port = p;
                RelayServer.RunConsole(port);
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // =========================================================================
    // NATIVE WIN32 INPUT & SCREEN HELPERS
    // =========================================================================
    public static class NativeInput
    {
        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        static extern bool GetCursorInfo(out CURSORINFO pci);

        [DllImport("user32.dll")]
        static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        const int CURSOR_SHOWING = 0x00000001;

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        public static void DrawCursorOnGraphics(Graphics g, Rectangle bounds)
        {
            try
            {
                CURSORINFO pci = new CURSORINFO();
                pci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                if (GetCursorInfo(out pci) && pci.flags == CURSOR_SHOWING)
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        DrawIcon(hdc, pci.ptScreenPos.x - bounds.X, pci.ptScreenPos.y - bounds.Y, pci.hCursor);
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }
            }
            catch { }
        }
    }

    // =========================================================================
    // PACKET PROTOCOL
    // =========================================================================
    public enum PacketType : byte
    {
        AuthRequest = 1,
        AuthResponse = 2,
        VideoFrame = 3,
        MouseEvent = 4,
        KeyEvent = 5,
        ChatMessage = 6,
        FileOffer = 7,
        QualityConfig = 8,
        Ping = 9,
        Pong = 10,
        RelayRegister = 20,
        RelayConnect = 21,
        RelayReady = 22
    }

    public static class PacketIO
    {
        public static async Task SendPacketAsync(Stream stream, SemaphoreSlim writeLock, PacketType type, byte[] payload, CancellationToken ct = default)
        {
            payload ??= Array.Empty<byte>();
            byte[] header = new byte[5];
            header[0] = (byte)type;
            BitConverter.GetBytes(payload.Length).CopyTo(header, 1);

            await writeLock.WaitAsync(ct);
            try
            {
                await stream.WriteAsync(header, 0, 5, ct);
                if (payload.Length > 0)
                    await stream.WriteAsync(payload, 0, payload.Length, ct);
                await stream.FlushAsync(ct);
            }
            finally
            {
                writeLock.Release();
            }
        }

        public static async Task<(PacketType type, byte[] payload)?> ReadPacketAsync(Stream stream, CancellationToken ct = default)
        {
            byte[] header = await ReadExactAsync(stream, 5, ct);
            if (header == null) return null;

            PacketType type = (PacketType)header[0];
            int length = BitConverter.ToInt32(header, 1);
            if (length < 0 || length > 100_000_000) // Max 100 MB per packet
                throw new InvalidDataException($"Некорректный размер пакета: {length}");

            byte[] payload = length > 0 ? await ReadExactAsync(stream, length, ct) : Array.Empty<byte>();
            if (length > 0 && payload == null) return null;

            return (type, payload);
        }

        private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, ct);
                if (read == 0) return null;
                offset += read;
            }
            return buffer;
        }
    }

    // =========================================================================
    // ID & NETWORK DISCOVERY HELPER
    // =========================================================================
    public static class NetworkHelper
    {
        public static IPAddress GetPrimaryLocalIPv4()
        {
            try
            {
                using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                if (socket.LocalEndPoint is IPEndPoint endPoint)
                    return endPoint.Address;
            }
            catch { }

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            return ua.Address;
                    }
                }
            }
            return IPAddress.Loopback;
        }

        // Generates a 9-digit AnyDesk/RustDesk style ID that encodes the local IP info + machine hash
        public static string GenerateDeviceId(IPAddress ip)
        {
            byte[] bytes = ip.GetAddressBytes();
            // Create a deterministic 9-digit number where the last 6 digits encode the last 2 octets (for instant LAN resolving)
            // and the first 3 digits encode the subnet hash
            int prefix = ((bytes[0] * 31 + bytes[1]) % 800) + 100; // 100..899
            int mid = bytes[2]; // 000..255
            int last = bytes[3]; // 000..255
            return $"{prefix:D3} {mid:D3} {last:D3}";
        }

        public static string NormalizeId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            return new string(raw.Where(char.IsDigit).ToArray());
        }

        // Decode 9-digit ID back to local subnet candidate IP if possible
        public static IPAddress TryResolveIdToLanIp(string cleanId, IPAddress myIp)
        {
            if (cleanId.Length != 9) return null;
            if (!int.TryParse(cleanId.Substring(3, 3), out int oct2)) return null;
            if (!int.TryParse(cleanId.Substring(6, 3), out int oct3)) return null;
            if (oct2 < 0 || oct2 > 255 || oct3 < 0 || oct3 > 255) return null;

            byte[] myBytes = myIp.GetAddressBytes();
            return new IPAddress(new byte[] { myBytes[0], myBytes[1], (byte)oct2, (byte)oct3 });
        }

        public static string GeneratePassword()
        {
            const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
            byte[] data = RandomNumberGenerator.GetBytes(6);
            char[] res = new char[6];
            for (int i = 0; i < 6; i++)
                res[i] = chars[data[i] % chars.Length];
            return new string(res);
        }
    }

    // =========================================================================
    // BUILT-IN RELAY SERVER (For internet connections across NAT)
    // =========================================================================
    public class RelayServer
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly ConcurrentDictionary<string, (TcpClient client, SemaphoreSlim writeLock)> _waitingHosts = new();
        public event Action<string> OnLog;
        public bool IsRunning => _listener != null;

        public void Start(int port)
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Log($"Relay-сервер запущен на порту {port}");
            Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener = null;
                foreach (var kv in _waitingHosts)
                    kv.Value.client.Close();
                _waitingHosts.Clear();
                Log("Relay-сервер остановлен");
            }
            catch { }
        }

        private void Log(string msg) => OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(ct);
                    client.NoDelay = true;
                    _ = Task.Run(() => HandleRelayClientAsync(client, ct), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log($"Ошибка Relay: {ex.Message}"); }
            }
        }

        private async Task HandleRelayClientAsync(TcpClient client, CancellationToken ct)
        {
            NetworkStream stream = client.GetStream();
            SemaphoreSlim wLock = new SemaphoreSlim(1, 1);
            var pkt = await PacketIO.ReadPacketAsync(stream, ct);
            if (pkt == null) { client.Close(); return; }

            string id = Encoding.UTF8.GetString(pkt.Value.payload).Trim();

            if (pkt.Value.type == PacketType.RelayRegister)
            {
                if (_waitingHosts.TryRemove(id, out var old))
                    old.client.Close();

                _waitingHosts[id] = (client, wLock);
                Log($"Хост зарегистрирован в Relay: ID {id}");
            }
            else if (pkt.Value.type == PacketType.RelayConnect)
            {
                if (_waitingHosts.TryRemove(id, out var hostTuple))
                {
                    Log($"Соединение в Relay: Клиент -> Хост ID {id}");
                    try
                    {
                        await PacketIO.SendPacketAsync(hostTuple.client.GetStream(), hostTuple.writeLock, PacketType.RelayReady, Encoding.UTF8.GetBytes("OK"), ct);
                        await PacketIO.SendPacketAsync(stream, wLock, PacketType.RelayReady, Encoding.UTF8.GetBytes("OK"), ct);

                        // Pipe streams bidirectionally
                        Stream hostStream = hostTuple.client.GetStream();
                        Task t1 = stream.CopyToAsync(hostStream, ct);
                        Task t2 = hostStream.CopyToAsync(stream, ct);
                        await Task.WhenAny(t1, t2);
                    }
                    catch { }
                    finally
                    {
                        hostTuple.client.Close();
                        client.Close();
                    }
                }
                else
                {
                    await PacketIO.SendPacketAsync(stream, wLock, PacketType.AuthResponse, Encoding.UTF8.GetBytes("FAIL:Хост с таким ID не найден на Relay-сервере"), ct);
                    client.Close();
                }
            }
        }

        public static void RunConsole(int port)
        {
            Console.Title = $"SakoDesk Relay Server (Port {port})";
            RelayServer srv = new RelayServer();
            srv.OnLog += Console.WriteLine;
            srv.Start(port);
            Console.WriteLine("Нажмите Ctrl+C для выхода...");
            Thread.Sleep(Timeout.Infinite);
        }
    }

    // =========================================================================
    // MAIN SAKO DESK DASHBOARD FORM
    // =========================================================================
    public class MainForm : Form
    {
        public const int HOST_PORT = 9870;
        public const int DISCOVERY_PORT = 9869;

        private string _myId;
        private string _myPassword;
        private IPAddress _myIp;

        private TcpListener _hostListener;
        private UdpClient _udpDiscovery;
        private CancellationTokenSource _serviceCts = new();
        private RelayServer _localRelay = new();
        private MobileWebServer _mobileServer;
        private InternetTunnelManager _tunnelManager = new();

        // UI Controls
        private Label _lblMyId;
        private TextBox _txtPassword;
        private TextBox _txtRemoteId;
        private TextBox _txtRelayHost;
        private CheckBox _chkUseRelay;
        private CheckBox _chkAllowControl;
        private ListBox _lstLanPeers;
        private RichTextBox _txtLog;
        private Label _lblStatusDot;
        private Label _lblStatusText;

        private readonly ConcurrentDictionary<string, (string ip, string name, DateTime lastSeen)> _discoveredPeers = new();

        public MainForm()
        {
            _myIp = NetworkHelper.GetPrimaryLocalIPv4();
            _myId = NetworkHelper.GenerateDeviceId(_myIp);
            _myPassword = NetworkHelper.GeneratePassword();

            BuildUI();
            StartBackgroundServices();
        }

        private void BuildUI()
        {
            Text = "Sako Desk — Удаленный доступ";
            Size = new Size(960, 620);
            MinimumSize = new Size(880, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(18, 20, 28);
            ForeColor = Color.FromArgb(235, 238, 245);
            Font = new Font("Segoe UI", 10F, FontStyle.Regular);

            // Top header bar
            Panel header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 64,
                BackColor = Color.FromArgb(25, 28, 38),
                Padding = new Padding(24, 0, 24, 0)
            };

            Label lblBrand = new Label
            {
                Text = "⚡ SAKO DESK",
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 82, 82),
                AutoSize = true,
                Location = new Point(24, 16)
            };

            Label lblSub = new Label
            {
                Text = "Быстрый удаленный рабочий стол (AnyDesk / RustDesk архитектура)",
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = Color.FromArgb(148, 156, 176),
                AutoSize = true,
                Location = new Point(210, 23)
            };

            _lblStatusDot = new Label
            {
                Text = "●",
                Font = new Font("Segoe UI", 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(46, 213, 115),
                AutoSize = true,
                Location = new Point(680, 17),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            _lblStatusText = new Label
            {
                Text = $"Готов к работе ({_myIp}:{HOST_PORT})",
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(200, 210, 225),
                AutoSize = true,
                Location = new Point(705, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            header.Controls.AddRange(new Control[] { lblBrand, lblSub, _lblStatusDot, _lblStatusText });

            // Main container split into Left (Your Desk) and Right (Connect to Remote)
            TableLayoutPanel mainGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(20),
                BackColor = Color.FromArgb(18, 20, 28)
            };
            mainGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46F));
            mainGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54F));
            mainGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // =================== LEFT CARD: YOUR WORKSTATION ===================
            Panel leftCard = CreateCardPanel();
            leftCard.Padding = new Padding(22);

            Label lblYourDeskTitle = new Label
            {
                Text = "ЭТОТ КОМПЬЮТЕР",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(130, 140, 165),
                Location = new Point(22, 18),
                AutoSize = true
            };

            Label lblIdCaption = new Label
            {
                Text = "Ваш ID для подключения:",
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.FromArgb(190, 198, 215),
                Location = new Point(22, 46),
                AutoSize = true
            };

            _lblMyId = new Label
            {
                Text = _myId,
                Font = new Font("Consolas", 26F, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 99, 72),
                Location = new Point(18, 72),
                AutoSize = true,
                Cursor = Cursors.Hand
            };
            _lblMyId.Click += (s, e) =>
            {
                Clipboard.SetText(_myId);
                AppendLog($"Ваш ID ({_myId}) скопирован в буфер обмена.");
            };

            Button btnCopyId = CreateSecondaryButton("Копировать ID", new Point(22, 128), new Size(145, 34));
            btnCopyId.Click += (s, e) =>
            {
                Clipboard.SetText($"{_myId} (IP: {_myIp})");
                AppendLog("ID и IP скопированы в буфер обмена.");
            };

            Button btnCopyIp = CreateSecondaryButton($"IP: {_myIp}", new Point(176, 128), new Size(175, 34));
            btnCopyIp.Click += (s, e) =>
            {
                Clipboard.SetText(_myIp.ToString());
                AppendLog($"IP-адрес {_myIp} скопирован.");
            };

            Label lblPassCaption = new Label
            {
                Text = "Одноразовый / постоянный пароль:",
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.FromArgb(190, 198, 215),
                Location = new Point(22, 180),
                AutoSize = true
            };

            _txtPassword = new TextBox
            {
                Text = _myPassword,
                Font = new Font("Consolas", 15F, FontStyle.Bold),
                BackColor = Color.FromArgb(18, 20, 28),
                ForeColor = Color.FromArgb(46, 213, 115),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(22, 206),
                Size = new Size(195, 36)
            };

            Button btnNewPass = CreateSecondaryButton("🔄 Обновить", new Point(226, 206), new Size(125, 35));
            btnNewPass.Click += (s, e) =>
            {
                _myPassword = NetworkHelper.GeneratePassword();
                _txtPassword.Text = _myPassword;
                AppendLog("Сгенерирован новый пароль доступа.");
            };

            _chkAllowControl = new CheckBox
            {
                Text = "Разрешить управление мышью и клавиатурой",
                Checked = true,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = Color.FromArgb(210, 215, 228),
                Location = new Point(22, 255),
                AutoSize = true
            };

            // Relay section inside Left Card
            Label lblRelayHeader = new Label
            {
                Text = "СЕТЬ И RELAY-СЕРВЕР (ДЛЯ ИНТЕРНЕТА)",
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(130, 140, 165),
                Location = new Point(22, 298),
                AutoSize = true
            };

            _chkUseRelay = new CheckBox
            {
                Text = "Подключаться через Relay-сервер:",
                Checked = false,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = Color.FromArgb(200, 208, 224),
                Location = new Point(22, 322),
                AutoSize = true
            };

            _txtRelayHost = new TextBox
            {
                Text = "127.0.0.1:9871",
                Font = new Font("Consolas", 11F),
                BackColor = Color.FromArgb(18, 20, 28),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(22, 348),
                Size = new Size(195, 28)
            };

            Button btnToggleRelay = CreateSecondaryButton("Запустить Relay", new Point(226, 347), new Size(135, 30));
            btnToggleRelay.Click += (s, e) =>
            {
                if (!_localRelay.IsRunning)
                {
                    _localRelay.Start(9871);
                    btnToggleRelay.Text = "Остановить Relay";
                    btnToggleRelay.BackColor = Color.FromArgb(180, 50, 50);
                    _chkUseRelay.Checked = true;
                    _ = RegisterOnRelayAsync();
                }
                else
                {
                    _localRelay.Stop();
                    btnToggleRelay.Text = "Запустить Relay";
                    btnToggleRelay.BackColor = Color.FromArgb(42, 47, 64);
                }
            };

            Button btnRegRelay = CreateSecondaryButton("Зарегистрировать ID на Relay", new Point(22, 386), new Size(339, 32));
            btnRegRelay.Click += async (s, e) =>
            {
                _chkUseRelay.Checked = true;
                await RegisterOnRelayAsync();
            };

            Button btnSelfTest = CreateSecondaryButton("🧪 Тест (ПК)", new Point(22, 428), new Size(130, 34));
            btnSelfTest.Click += (s, e) =>
            {
                _txtRemoteId.Text = "127.0.0.1";
                ConnectToRemote("127.0.0.1", _txtPassword.Text.Trim());
            };

            Button btnMobileQr = new Button
            {
                Text = "📱 Подключить Телефон (QR / Ссылка)",
                Location = new Point(160, 428),
                Size = new Size(201, 34),
                BackColor = Color.FromArgb(46, 213, 115),
                ForeColor = Color.FromArgb(15, 17, 24),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnMobileQr.FlatAppearance.BorderSize = 0;
            btnMobileQr.Click += (s, e) => ShowMobileConnectDialog();

            leftCard.Controls.AddRange(new Control[]
            {
                lblYourDeskTitle, lblIdCaption, _lblMyId, btnCopyId, btnCopyIp,
                lblPassCaption, _txtPassword, btnNewPass, _chkAllowControl,
                lblRelayHeader, _chkUseRelay, _txtRelayHost, btnToggleRelay, btnRegRelay, btnSelfTest, btnMobileQr
            });

            // =================== RIGHT CARD: REMOTE CONNECTION & LAN PEERS ===================
            Panel rightCard = CreateCardPanel();
            rightCard.Padding = new Padding(22);

            Label lblRemoteTitle = new Label
            {
                Text = "ПОДКЛЮЧЕНИЕ К УДАЛЕННОМУ ПК",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(130, 140, 165),
                Location = new Point(22, 18),
                AutoSize = true
            };

            Label lblEnterRemote = new Label
            {
                Text = "Введите ID партнера или IP-адрес:",
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.FromArgb(190, 198, 215),
                Location = new Point(22, 46),
                AutoSize = true
            };

            _txtRemoteId = new TextBox
            {
                Text = "",
                PlaceholderText = "Например: 412 168 015 или 192.168.1.15",
                Font = new Font("Segoe UI", 15F, FontStyle.Bold),
                BackColor = Color.FromArgb(18, 20, 28),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(22, 74),
                Size = new Size(280, 38)
            };

            Button btnConnect = new Button
            {
                Text = "ПОДКЛЮЧИТЬСЯ ➜",
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                BackColor = Color.FromArgb(255, 71, 87),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(312, 74),
                Size = new Size(165, 38),
                Cursor = Cursors.Hand
            };
            btnConnect.FlatAppearance.BorderSize = 0;
            btnConnect.Click += (s, e) => PromptAndConnect(_txtRemoteId.Text.Trim());

            _txtRemoteId.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    PromptAndConnect(_txtRemoteId.Text.Trim());
                }
            };

            Label lblLanTitle = new Label
            {
                Text = "ОБНАРУЖЕННЫЕ КОМПЬЮТЕРЫ В СЕТИ (АВТОПОИСК, ДВОЙНОЙ КЛИК):",
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(130, 140, 165),
                Location = new Point(22, 128),
                AutoSize = true
            };

            _lstLanPeers = new ListBox
            {
                BackColor = Color.FromArgb(18, 20, 28),
                ForeColor = Color.FromArgb(72, 219, 251),
                Font = new Font("Consolas", 10.5F),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(22, 152),
                Size = new Size(455, 115)
            };
            _lstLanPeers.DoubleClick += (s, e) =>
            {
                if (_lstLanPeers.SelectedItem != null)
                {
                    string item = _lstLanPeers.SelectedItem.ToString();
                    // Format: "ID: 123 456 789 | PC-NAME | 192.168.1.10"
                    var parts = item.Split('|');
                    if (parts.Length >= 3)
                    {
                        string ip = parts[2].Trim();
                        _txtRemoteId.Text = ip;
                        PromptAndConnect(ip);
                    }
                }
            };

            Label lblLogTitle = new Label
            {
                Text = "ЖУРНАЛ СОБЫТИЙ И БЕЗОПАСНОСТИ:",
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(130, 140, 165),
                Location = new Point(22, 282),
                AutoSize = true
            };

            _txtLog = new RichTextBox
            {
                BackColor = Color.FromArgb(15, 17, 24),
                ForeColor = Color.FromArgb(175, 185, 205),
                Font = new Font("Consolas", 9.5F),
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                Location = new Point(22, 306),
                Size = new Size(455, 156)
            };

            rightCard.Controls.AddRange(new Control[]
            {
                lblRemoteTitle, lblEnterRemote, _txtRemoteId, btnConnect,
                lblLanTitle, _lstLanPeers, lblLogTitle, _txtLog
            });

            mainGrid.Controls.Add(leftCard, 0, 0);
            mainGrid.Controls.Add(rightCard, 1, 0);

            Controls.Add(mainGrid);
            Controls.Add(header);

            _localRelay.OnLog += AppendLog;
            _tunnelManager.OnLog += AppendLog;
            FormClosing += (s, e) =>
            {
                _serviceCts.Cancel();
                _hostListener?.Stop();
                _udpDiscovery?.Close();
                _localRelay.Stop();
                _mobileServer?.Stop();
                _tunnelManager.StopTunnel();
            };
        }

        private void ShowMobileConnectDialog()
        {
            string lanUrl = $"http://{_myIp}:9880";
            string pass = _txtPassword.Text.Trim();

            using Form dlg = new Form
            {
                Text = "📱 Управление ПК с телефона без ограничений (Wi-Fi + 4G/5G Интернет)",
                Size = new Size(560, 440),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.FromArgb(22, 25, 36),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10F)
            };

            Label lblHead = new Label
            {
                Text = "1. Внутри домашнего Wi-Fi (максимальная скорость):",
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(230, 235, 245),
                Location = new Point(24, 18),
                AutoSize = true
            };

            TextBox txtUrl = new TextBox
            {
                Text = lanUrl,
                ReadOnly = true,
                Font = new Font("Consolas", 15F, FontStyle.Bold),
                BackColor = Color.FromArgb(14, 16, 24),
                ForeColor = Color.FromArgb(46, 213, 115),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(24, 46),
                Size = new Size(370, 34)
            };

            Button btnCopyUrl = CreateSecondaryButton("Копировать", new Point(404, 46), new Size(115, 34));
            btnCopyUrl.Click += (s, e) =>
            {
                Clipboard.SetText(lanUrl);
                AppendLog($"Мобильная ссылка {lanUrl} скопирована.");
            };

            Label lblGlobalHead = new Label
            {
                Text = "2. Из любой точки мира (через 4G / 5G / мобильный интернет):",
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(72, 219, 251),
                Location = new Point(24, 96),
                AutoSize = true
            };

            TextBox txtGlobalUrl = new TextBox
            {
                Text = _tunnelManager.PublicUrl ?? "Нажмите «Включить Интернет-ссылку» ->",
                ReadOnly = true,
                Font = new Font("Consolas", 11.5F, FontStyle.Bold),
                BackColor = Color.FromArgb(14, 16, 24),
                ForeColor = Color.FromArgb(72, 219, 251),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(24, 124),
                Size = new Size(330, 32)
            };

            Button btnStartGlobal = new Button
            {
                Text = _tunnelManager.IsRunning ? "Копировать URL" : "🌍 Включить 4G/5G",
                Location = new Point(364, 123),
                Size = new Size(155, 34),
                BackColor = Color.FromArgb(30, 144, 255),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnStartGlobal.FlatAppearance.BorderSize = 0;
            btnStartGlobal.Click += async (s, e) =>
            {
                if (!string.IsNullOrEmpty(_tunnelManager.PublicUrl))
                {
                    Clipboard.SetText(_tunnelManager.PublicUrl);
                    AppendLog($"Глобальная ссылка {_tunnelManager.PublicUrl} скопирована.");
                    return;
                }
                btnStartGlobal.Enabled = false;
                btnStartGlobal.Text = "Создание...";
                txtGlobalUrl.Text = "Подключение глобального HTTPS туннеля...";
                string pub = await _tunnelManager.StartTunnelAsync(9880);
                if (!string.IsNullOrEmpty(pub))
                {
                    txtGlobalUrl.Text = pub;
                    Clipboard.SetText(pub);
                    btnStartGlobal.Text = "Копировать URL";
                }
                else
                {
                    txtGlobalUrl.Text = "Ошибка туннеля (проверьте интернет)";
                    btnStartGlobal.Text = "Повторить";
                }
                btnStartGlobal.Enabled = true;
            };

            Label lblPassInfo = new Label
            {
                Text = $"Пароль для входа с телефона:   {pass}",
                Font = new Font("Consolas", 13F, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 99, 72),
                Location = new Point(24, 174),
                AutoSize = true
            };

            Label lblTips = new Label
            {
                Text = "ВОЗМОЖНОСТИ МОБИЛЬНОГО ПУЛЬТА SAKO DESK PRO (БЕЗ ОГРАНИЧЕНИЙ):\n" +
                       "• Прямое касание + режим Тачпада + кнопка «✊ Зажать» (для перетаскивания окон и файлов).\n" +
                       "• Панель «⚡ Система»: Диспетчер задач, Alt+F4, Alt+Tab, Свернуть все (Win+D), Пуск, Копировать/Вставить.\n" +
                       "• Панель «💻 Терминал»: удаленное выполнение любых команд PowerShell / CMD прямо с телефона.\n" +
                       "• Кнопка «📁 Файл на ПК»: отправка фото/файлов из памяти телефона прямо на рабочий стол ПК.",
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.FromArgb(175, 185, 205),
                Location = new Point(24, 210),
                AutoSize = true
            };

            Button btnOpenLocal = new Button
            {
                Text = "🌐 Открыть и протестировать мобильный пульт в браузере",
                Location = new Point(24, 340),
                Size = new Size(495, 40),
                BackColor = Color.FromArgb(255, 71, 87),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnOpenLocal.FlatAppearance.BorderSize = 0;
            btnOpenLocal.Click += (s, e) =>
            {
                Process.Start(new ProcessStartInfo(lanUrl) { UseShellExecute = true });
            };

            dlg.Controls.AddRange(new Control[]
            {
                lblHead, txtUrl, btnCopyUrl,
                lblGlobalHead, txtGlobalUrl, btnStartGlobal,
                lblPassInfo, lblTips, btnOpenLocal
            });
            dlg.ShowDialog(this);
        }

        private Panel CreateCardPanel()
        {
            return new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(26, 29, 41),
                Margin = new Padding(8)
            };
        }

        private Button CreateSecondaryButton(string text, Point location, Size size)
        {
            Button btn = new Button
            {
                Text = text,
                Location = location,
                Size = size,
                BackColor = Color.FromArgb(42, 47, 64),
                ForeColor = Color.FromArgb(225, 230, 240),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(62, 68, 90);
            btn.FlatAppearance.BorderSize = 1;
            return btn;
        }

        public void AppendLog(string text)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLog), text);
                return;
            }
            _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
            _txtLog.ScrollToCaret();
        }

        // =====================================================================
        // HOST & DISCOVERY BACKGROUND SERVICES
        // =====================================================================
        private void StartBackgroundServices()
        {
            // 0. Start Mobile Web & WebSocket Server (Port 7080)
            try
            {
                _mobileServer = new MobileWebServer(
                    getPassword: () =>
                    {
                        string p = _myPassword;
                        if (IsHandleCreated) Invoke(new Action(() => p = _txtPassword.Text.Trim()));
                        return p;
                    },
                    getAllowControl: () =>
                    {
                        bool allow = true;
                        if (IsHandleCreated) Invoke(new Action(() => allow = _chkAllowControl.Checked));
                        return allow;
                    },
                    getDeviceId: () => _myId,
                    log: AppendLog
                );
                _mobileServer.Start(9880);
                AppendLog($"📱 Ссылка для входа с телефона: http://{_myIp}:9880");
            }
            catch (Exception ex)
            {
                AppendLog($"Ошибка запуска мобильного сервера: {ex.Message}");
            }

            // 1. Start TCP Host Listener
            Task.Run(async () =>
            {
                try
                {
                    _hostListener = new TcpListener(IPAddress.Any, HOST_PORT);
                    _hostListener.Start();
                    AppendLog($"Хост-сервер запущен. Ожидание входящих подключений (порт {HOST_PORT})...");

                    while (!_serviceCts.Token.IsCancellationRequested)
                    {
                        TcpClient client = await _hostListener.AcceptTcpClientAsync(_serviceCts.Token);
                        client.NoDelay = true;
                        string remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "неизвестный";
                        AppendLog($"Входящее подключение от {remoteEp}...");
                        _ = Task.Run(() => HandleIncomingHostSessionAsync(client, _serviceCts.Token));
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppendLog($"Ошибка хост-сервера: {ex.Message}");
                }
            });

            // 2. Start UDP LAN Auto-Discovery (broadcast & listen)
            Task.Run(async () =>
            {
                try
                {
                    _udpDiscovery = new UdpClient();
                    _udpDiscovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _udpDiscovery.Client.Bind(new IPEndPoint(IPAddress.Any, DISCOVERY_PORT));
                    _udpDiscovery.EnableBroadcast = true;

                    // Listener task
                    _ = Task.Run(async () =>
                    {
                        while (!_serviceCts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                var res = await _udpDiscovery.ReceiveAsync(_serviceCts.Token);
                                string msg = Encoding.UTF8.GetString(res.Buffer);
                                if (msg.StartsWith("SAKODESK|"))
                                {
                                    var parts = msg.Split('|');
                                    if (parts.Length >= 4)
                                    {
                                        string peerId = parts[1];
                                        string peerName = parts[2];
                                        string peerIp = res.RemoteEndPoint.Address.ToString();
                                        _discoveredPeers[peerId] = (peerIp, peerName, DateTime.Now);
                                        UpdateLanPeersList();
                                    }
                                }
                            }
                            catch { }
                        }
                    });

                    // Broadcaster loop
                    while (!_serviceCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            string beacon = $"SAKODESK|{_myId}|{Environment.MachineName}|{_myIp}";
                            byte[] data = Encoding.UTF8.GetBytes(beacon);
                            await _udpDiscovery.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Broadcast, DISCOVERY_PORT));
                        }
                        catch { }
                        await Task.Delay(3000, _serviceCts.Token);
                    }
                }
                catch { }
            });
        }

        private void UpdateLanPeersList()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(UpdateLanPeersList));
                return;
            }

            _lstLanPeers.Items.Clear();
            foreach (var kv in _discoveredPeers.Values.OrderBy(x => x.name))
            {
                string id = NetworkHelper.GenerateDeviceId(IPAddress.Parse(kv.ip));
                string tag = kv.ip == _myIp.ToString() ? " (Этот ПК)" : "";
                _lstLanPeers.Items.Add($"ID: {id} | {kv.name}{tag} | {kv.ip}");
            }
        }

        private async Task RegisterOnRelayAsync()
        {
            try
            {
                var (host, port) = ParseHostPort(_txtRelayHost.Text, 9871);
                TcpClient relayClient = new TcpClient();
                relayClient.NoDelay = true;
                await relayClient.ConnectAsync(host, port);

                NetworkStream stream = relayClient.GetStream();
                SemaphoreSlim wLock = new SemaphoreSlim(1, 1);
                string cleanMyId = NetworkHelper.NormalizeId(_myId);

                await PacketIO.SendPacketAsync(stream, wLock, PacketType.RelayRegister, Encoding.UTF8.GetBytes(cleanMyId), _serviceCts.Token);
                AppendLog($"Зарегистрирован на Relay-сервере {host}:{port} под ID {_myId}. Ожидание клиента...");

                // Wait for RelayReady when a remote viewer connects to our ID
                var pkt = await PacketIO.ReadPacketAsync(stream, _serviceCts.Token);
                if (pkt != null && pkt.Value.type == PacketType.RelayReady)
                {
                    AppendLog("Клиент подключился через Relay-сервер! Запуск сессии...");
                    _ = Task.Run(() => HandleIncomingHostSessionAsync(relayClient, _serviceCts.Token));
                    // Re-register for next session
                    _ = Task.Run(RegisterOnRelayAsync);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Не удалось подключиться к Relay: {ex.Message}");
            }
        }

        // =====================================================================
        // INCOMING HOST SESSION (STREAM SCREEN + RECEIVE MOUSE/KEYBOARD/CHAT)
        // =====================================================================
        private async Task HandleIncomingHostSessionAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);

                // 1. Read AuthRequest
                var authPkt = await PacketIO.ReadPacketAsync(stream, ct);
                if (authPkt == null || authPkt.Value.type != PacketType.AuthRequest)
                    return;

                string providedPass = Encoding.UTF8.GetString(authPkt.Value.payload);
                string currentPass = "";
                bool allowControl = true;
                Invoke(new Action(() =>
                {
                    currentPass = _txtPassword.Text.Trim();
                    allowControl = _chkAllowControl.Checked;
                }));

                if (providedPass != currentPass)
                {
                    AppendLog("❌ Отклонено входящее подключение: неверный пароль.");
                    await PacketIO.SendPacketAsync(stream, writeLock, PacketType.AuthResponse, Encoding.UTF8.GetBytes("FAIL:Неверный пароль доступа!"), ct);
                    return;
                }

                Rectangle screenBounds = Screen.PrimaryScreen.Bounds;
                await PacketIO.SendPacketAsync(stream, writeLock, PacketType.AuthResponse,
                    Encoding.UTF8.GetBytes($"OK|{Environment.MachineName}|{screenBounds.Width}|{screenBounds.Height}"), ct);

                AppendLog($"✅ Удаленный пользователь авторизован! Трансляция экрана ({screenBounds.Width}x{screenBounds.Height})...");

                using CancellationTokenSource sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                int jpegQuality = 65;
                float scaleFactor = 1.0f;
                int targetFps = 25;

                // Task A: Screen Capture & Streaming Loop
                Task streamTask = Task.Run(async () =>
                {
                    ImageCodecInfo jpegEncoder = ImageCodecInfo.GetImageDecoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

                    while (!sessionCts.Token.IsCancellationRequested)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            Rectangle bounds = Screen.PrimaryScreen.Bounds;
                            using Bitmap rawBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                            using (Graphics g = Graphics.FromImage(rawBmp))
                            {
                                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                                NativeInput.DrawCursorOnGraphics(g, bounds);
                            }

                            using MemoryStream ms = new MemoryStream();
                            using EncoderParameters encParams = new EncoderParameters(1);
                            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)jpegQuality);

                            if (scaleFactor < 0.99f)
                            {
                                int swWidth = Math.Max(320, (int)(bounds.Width * scaleFactor));
                                int swHeight = Math.Max(200, (int)(bounds.Height * scaleFactor));
                                using Bitmap scaledBmp = new Bitmap(swWidth, swHeight, PixelFormat.Format24bppRgb);
                                using (Graphics gs = Graphics.FromImage(scaledBmp))
                                {
                                    gs.InterpolationMode = InterpolationMode.Bilinear;
                                    gs.DrawImage(rawBmp, 0, 0, swWidth, swHeight);
                                }
                                scaledBmp.Save(ms, jpegEncoder, encParams);
                            }
                            else
                            {
                                rawBmp.Save(ms, jpegEncoder, encParams);
                            }

                            byte[] frameBytes = ms.ToArray();
                            await PacketIO.SendPacketAsync(stream, writeLock, PacketType.VideoFrame, frameBytes, sessionCts.Token);
                        }
                        catch (Exception)
                        {
                            sessionCts.Cancel();
                            break;
                        }

                        int delay = Math.Max(5, (1000 / Math.Max(5, targetFps)) - (int)sw.ElapsedMilliseconds);
                        await Task.Delay(delay, sessionCts.Token);
                    }
                }, sessionCts.Token);

                // Task B: Incoming Control Commands, Chat & File Transfers
                try
                {
                    while (!sessionCts.Token.IsCancellationRequested)
                    {
                        var pkt = await PacketIO.ReadPacketAsync(stream, sessionCts.Token);
                        if (pkt == null) break;

                        switch (pkt.Value.type)
                        {
                            case PacketType.MouseEvent:
                                if (allowControl && pkt.Value.payload.Length >= 13)
                                {
                                    byte action = pkt.Value.payload[0];
                                    float normX = BitConverter.ToSingle(pkt.Value.payload, 1);
                                    float normY = BitConverter.ToSingle(pkt.Value.payload, 5);
                                    int wheelDelta = BitConverter.ToInt32(pkt.Value.payload, 9);

                                    Rectangle b = Screen.PrimaryScreen.Bounds;
                                    int absX = b.X + (int)(normX * b.Width);
                                    int absY = b.Y + (int)(normY * b.Height);

                                    NativeInput.SetCursorPos(absX, absY);
                                    switch (action)
                                    {
                                        case 1: NativeInput.mouse_event(NativeInput.MOUSEEVENTF_LEFTDOWN, absX, absY, 0, UIntPtr.Zero); break;
                                        case 2: NativeInput.mouse_event(NativeInput.MOUSEEVENTF_LEFTUP, absX, absY, 0, UIntPtr.Zero); break;
                                        case 3: NativeInput.mouse_event(NativeInput.MOUSEEVENTF_RIGHTDOWN, absX, absY, 0, UIntPtr.Zero); break;
                                        case 4: NativeInput.mouse_event(NativeInput.MOUSEEVENTF_RIGHTUP, absX, absY, 0, UIntPtr.Zero); break;
                                        case 5: NativeInput.mouse_event(NativeInput.MOUSEEVENTF_MIDDLEDOWN, absX, absY, 0, UIntPtr.Zero); break;
                                        case 6: NativeInput.mouse_event(NativeInput.MOUSEEVENTF_MIDDLEUP, absX, absY, 0, UIntPtr.Zero); break;
                                        case 7: NativeInput.mouse_event(NativeInput.MOUSEEVENTF_WHEEL, absX, absY, wheelDelta, UIntPtr.Zero); break;
                                    }
                                }
                                break;

                            case PacketType.KeyEvent:
                                if (allowControl && pkt.Value.payload.Length >= 2)
                                {
                                    byte vk = pkt.Value.payload[0];
                                    bool isDown = pkt.Value.payload[1] == 1;
                                    NativeInput.keybd_event(vk, 0, isDown ? 0u : NativeInput.KEYEVENTF_KEYUP, UIntPtr.Zero);
                                }
                                break;

                            case PacketType.QualityConfig:
                                string cfg = Encoding.UTF8.GetString(pkt.Value.payload);
                                var cParts = cfg.Split('|');
                                if (cParts.Length >= 3)
                                {
                                    int.TryParse(cParts[0], out jpegQuality);
                                    float.TryParse(cParts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out scaleFactor);
                                    int.TryParse(cParts[2], out targetFps);
                                    AppendLog($"Настройки качества изменены клиентом: JPEG={jpegQuality}%, Масштаб={(int)(scaleFactor * 100)}%, FPS={targetFps}");
                                }
                                break;

                            case PacketType.ChatMessage:
                                string chatMsg = Encoding.UTF8.GetString(pkt.Value.payload);
                                AppendLog($"💬 Сообщение от клиента: {chatMsg}");
                                break;

                            case PacketType.FileOffer:
                                HandleIncomingFile(pkt.Value.payload);
                                break;

                            case PacketType.Ping:
                                await PacketIO.SendPacketAsync(stream, writeLock, PacketType.Pong, pkt.Value.payload, sessionCts.Token);
                                break;
                        }
                    }
                }
                catch { }
                finally
                {
                    sessionCts.Cancel();
                    AppendLog("Сессия удаленного доступа завершена.");
                }
            }
        }

        private void HandleIncomingFile(byte[] payload)
        {
            try
            {
                using MemoryStream ms = new MemoryStream(payload);
                using BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                string fileName = Path.GetFileName(br.ReadString());
                int fileLen = br.ReadInt32();
                byte[] fileBytes = br.ReadBytes(fileLen);

                string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SakoDesk_Received");
                Directory.CreateDirectory(saveDir);
                string fullPath = Path.Combine(saveDir, fileName);
                File.WriteAllBytes(fullPath, fileBytes);

                AppendLog($"📁 Получен файл '{fileName}' ({fileLen / 1024.0:F1} КБ) -> сохранен в {fullPath}");
            }
            catch (Exception ex)
            {
                AppendLog($"Ошибка сохранения файла: {ex.Message}");
            }
        }

        // =====================================================================
        // CLIENT VIEWER LAUNCHER
        // =====================================================================
        private void PromptAndConnect(string targetInput)
        {
            if (string.IsNullOrWhiteSpace(targetInput))
            {
                MessageBox.Show("Введите ID партнера или IP-адрес!", "Sako Desk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Ask for password in a sleek modal dialog
            using Form passDlg = new Form
            {
                Text = "Авторизация Sako Desk",
                Size = new Size(380, 210),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.FromArgb(25, 28, 38),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10F)
            };

            Label lblPrompt = new Label
            {
                Text = $"Введите пароль доступа для [{targetInput}]:",
                Location = new Point(20, 20),
                AutoSize = true
            };

            TextBox txtPass = new TextBox
            {
                Text = (targetInput == "127.0.0.1" || targetInput == _myIp.ToString() || NetworkHelper.NormalizeId(targetInput) == NetworkHelper.NormalizeId(_myId))
                    ? _txtPassword.Text.Trim() : "",
                Font = new Font("Consolas", 14F, FontStyle.Bold),
                BackColor = Color.FromArgb(18, 20, 28),
                ForeColor = Color.FromArgb(46, 213, 115),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(20, 52),
                Size = new Size(320, 34)
            };

            Button btnOk = new Button
            {
                Text = "Подключиться",
                DialogResult = DialogResult.OK,
                BackColor = Color.FromArgb(255, 71, 87),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(190, 108),
                Size = new Size(150, 36)
            };
            btnOk.FlatAppearance.BorderSize = 0;

            passDlg.Controls.AddRange(new Control[] { lblPrompt, txtPass, btnOk });
            passDlg.AcceptButton = btnOk;

            if (passDlg.ShowDialog(this) == DialogResult.OK)
            {
                ConnectToRemote(targetInput, txtPass.Text.Trim());
            }
        }

        private void ConnectToRemote(string targetInput, string password)
        {
            bool useRelay = _chkUseRelay.Checked;
            string relayEndpoint = _txtRelayHost.Text.Trim();

            RemoteViewerForm viewer = new RemoteViewerForm(this, targetInput, password, useRelay, relayEndpoint, _myIp);
            viewer.Show();
        }

        public static (string host, int port) ParseHostPort(string input, int defaultPort)
        {
            if (input.Contains(':'))
            {
                var p = input.Split(':');
                if (int.TryParse(p[1], out int port))
                    return (p[0].Trim(), port);
            }
            return (input.Trim(), defaultPort);
        }
    }

    // =========================================================================
    // REMOTE VIEWER WINDOW (LIVE STREAM, MOUSE/KEYBOARD CONTROL, TOOLBAR, CHAT)
    // =========================================================================
    public class RemoteViewerForm : Form
    {
        private readonly MainForm _owner;
        private readonly string _targetInput;
        private readonly string _password;
        private readonly bool _useRelay;
        private readonly string _relayEndpoint;
        private readonly IPAddress _myIp;

        private TcpClient _client;
        private NetworkStream _stream;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();

        private PictureBox _screenBox;
        private Label _lblStats;
        private ComboBox _cmbQuality;
        private CheckBox _chkControlEnabled;
        private Panel _chatPanel;
        private RichTextBox _txtChatHistory;
        private TextBox _txtChatInput;

        private int _frameCount = 0;
        private int _currentFps = 0;
        private long _bytesReceived = 0;
        private int _pingMs = 0;
        private DateTime _lastFpsTime = DateTime.Now;
        private string _remoteMachineName = "Удаленный ПК";

        public RemoteViewerForm(MainForm owner, string targetInput, string password, bool useRelay, string relayEndpoint, IPAddress myIp)
        {
            _owner = owner;
            _targetInput = targetInput;
            _password = password;
            _useRelay = useRelay;
            _relayEndpoint = relayEndpoint;
            _myIp = myIp;

            BuildViewerUI();
            Shown += async (s, e) => await StartViewerSessionAsync();
            FormClosing += (s, e) =>
            {
                _cts.Cancel();
                _client?.Close();
            };
        }

        private void BuildViewerUI()
        {
            Text = $"Sako Desk — Подключение к {_targetInput}...";
            Size = new Size(1200, 760);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(12, 14, 20);
            ForeColor = Color.White;
            KeyPreview = true;

            // Top Control Toolbar (AnyDesk style floating bar)
            Panel topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 46,
                BackColor = Color.FromArgb(24, 27, 38),
                Padding = new Padding(12, 6, 12, 6)
            };

            _lblStats = new Label
            {
                Text = "🔄 Установка соединения...",
                Font = new Font("Consolas", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(46, 213, 115),
                Location = new Point(14, 13),
                AutoSize = true
            };

            _chkControlEnabled = new CheckBox
            {
                Text = "🖱 Управление",
                Checked = true,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = Color.White,
                Location = new Point(430, 11),
                AutoSize = true
            };

            Label lblQual = new Label
            {
                Text = "Качество:",
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = Color.FromArgb(180, 190, 210),
                Location = new Point(560, 13),
                AutoSize = true
            };

            _cmbQuality = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(18, 20, 28),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(630, 9),
                Size = new Size(175, 28)
            };
            _cmbQuality.Items.AddRange(new object[]
            {
                "Ультра (90% | 100% | 30 FPS)",
                "Сбалансированное (65% | 100% | 25 FPS)",
                "Быстрое (45% | 75% | 25 FPS)",
                "Эконом (30% | 50% | 15 FPS)"
            });
            _cmbQuality.SelectedIndex = 1;
            _cmbQuality.SelectedIndexChanged += async (s, e) => await SendQualityPresetAsync();

            Button btnSendFile = CreateToolbarButton("📁 Отправить файл", new Point(820, 7), new Size(145, 32));
            btnSendFile.Click += async (s, e) => await SendFileDialogAsync();

            Button btnShot = CreateToolbarButton("📸 Скриншот", new Point(972, 7), new Size(105, 32));
            btnShot.Click += (s, e) => SaveScreenshot();

            Button btnChat = CreateToolbarButton("💬 Чат", new Point(1084, 7), new Size(85, 32));
            btnChat.Click += (s, e) => _chatPanel.Visible = !_chatPanel.Visible;

            topBar.Controls.AddRange(new Control[]
            {
                _lblStats, _chkControlEnabled, lblQual, _cmbQuality, btnSendFile, btnShot, btnChat
            });

            // Collapsible Right Chat Drawer
            _chatPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 280,
                BackColor = Color.FromArgb(20, 23, 32),
                Visible = false,
                Padding = new Padding(8)
            };

            _txtChatHistory = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 17, 24),
                ForeColor = Color.FromArgb(220, 225, 235),
                Font = new Font("Segoe UI", 9.5F),
                BorderStyle = BorderStyle.None,
                ReadOnly = true
            };

            Panel chatBottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 36
            };

            _txtChatInput = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(28, 32, 44),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10F),
                BorderStyle = BorderStyle.FixedSingle,
                PlaceholderText = "Написать сообщение..."
            };
            _txtChatInput.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && !string.IsNullOrWhiteSpace(_txtChatInput.Text))
                {
                    e.SuppressKeyPress = true;
                    string msg = _txtChatInput.Text.Trim();
                    _txtChatInput.Clear();
                    AppendChat($"Вы: {msg}");
                    if (_stream != null)
                        await PacketIO.SendPacketAsync(_stream, _writeLock, PacketType.ChatMessage, Encoding.UTF8.GetBytes(msg), _cts.Token);
                }
            };

            chatBottom.Controls.Add(_txtChatInput);
            _chatPanel.Controls.Add(_txtChatHistory);
            _chatPanel.Controls.Add(chatBottom);

            // Remote Screen Display PictureBox
            _screenBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };

            // Mouse hooks
            _screenBox.MouseMove += OnScreenMouseMove;
            _screenBox.MouseDown += OnScreenMouseDown;
            _screenBox.MouseUp += OnScreenMouseUp;
            _screenBox.MouseWheel += OnScreenMouseWheel;

            // Keyboard hooks
            KeyDown += OnViewerKeyDown;
            KeyUp += OnViewerKeyUp;

            Controls.Add(_screenBox);
            Controls.Add(_chatPanel);
            Controls.Add(topBar);
        }

        private Button CreateToolbarButton(string text, Point loc, Size size)
        {
            Button b = new Button
            {
                Text = text,
                Location = loc,
                Size = size,
                BackColor = Color.FromArgb(40, 45, 62),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private void AppendChat(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendChat), line); return; }
            _txtChatHistory.AppendText($"[{DateTime.Now:HH:mm}] {line}{Environment.NewLine}");
            _txtChatHistory.ScrollToCaret();
        }

        // =====================================================================
        // CONNECTION & STREAM RECEIVER LOOP
        // =====================================================================
        private async Task StartViewerSessionAsync()
        {
            try
            {
                _client = new TcpClient();
                _client.NoDelay = true;

                string cleanDigits = NetworkHelper.NormalizeId(_targetInput);

                if (_useRelay)
                {
                    var (rHost, rPort) = MainForm.ParseHostPort(_relayEndpoint, 9871);
                    _owner.AppendLog($"Подключение к Relay-серверу {rHost}:{rPort} для вызова ID {cleanDigits}...");
                    await _client.ConnectAsync(rHost, rPort);
                    _stream = _client.GetStream();

                    await PacketIO.SendPacketAsync(_stream, _writeLock, PacketType.RelayConnect, Encoding.UTF8.GetBytes(cleanDigits), _cts.Token);
                    var relayResp = await PacketIO.ReadPacketAsync(_stream, _cts.Token);
                    if (relayResp == null || relayResp.Value.type != PacketType.RelayReady)
                    {
                        string err = relayResp != null ? Encoding.UTF8.GetString(relayResp.Value.payload) : "Relay не ответил";
                        MessageBox.Show($"Ошибка Relay: {err}", "Sako Desk", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Close();
                        return;
                    }
                }
                else
                {
                    // Direct IP or 9-digit LAN ID
                    string hostIp = _targetInput;
                    int port = MainForm.HOST_PORT;

                    if (cleanDigits.Length == 9 && !_targetInput.Contains('.'))
                    {
                        IPAddress resolved = NetworkHelper.TryResolveIdToLanIp(cleanDigits, _myIp);
                        if (resolved != null)
                            hostIp = resolved.ToString();
                    }
                    else
                    {
                        (hostIp, port) = MainForm.ParseHostPort(_targetInput, MainForm.HOST_PORT);
                    }

                    _owner.AppendLog($"Прямое подключение к {hostIp}:{port}...");
                    await _client.ConnectAsync(hostIp, port);
                    _stream = _client.GetStream();
                }

                // Send password authentication
                await PacketIO.SendPacketAsync(_stream, _writeLock, PacketType.AuthRequest, Encoding.UTF8.GetBytes(_password), _cts.Token);
                var authResp = await PacketIO.ReadPacketAsync(_stream, _cts.Token);
                if (authResp == null)
                {
                    MessageBox.Show("Сервер разорвал соединение.", "Sako Desk", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                    return;
                }

                string respStr = Encoding.UTF8.GetString(authResp.Value.payload);
                if (!respStr.StartsWith("OK|"))
                {
                    MessageBox.Show($"Отказ в доступе: {respStr}", "Sako Desk", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                    return;
                }

                var parts = respStr.Split('|');
                _remoteMachineName = parts.Length > 1 ? parts[1] : _targetInput;
                Text = $"Sako Desk — {_remoteMachineName} ({_targetInput})";
                _owner.AppendLog($"✅ Успешное подключение к {_remoteMachineName}!");

                // Start ping loop
                _ = Task.Run(PingLoopAsync, _cts.Token);

                // Receive frames loop
                while (!_cts.Token.IsCancellationRequested)
                {
                    var pkt = await PacketIO.ReadPacketAsync(_stream, _cts.Token);
                    if (pkt == null) break;

                    _bytesReceived += pkt.Value.payload.Length;

                    if (pkt.Value.type == PacketType.VideoFrame)
                    {
                        using MemoryStream ms = new MemoryStream(pkt.Value.payload);
                        Image frame = Image.FromStream(ms);

                        _frameCount++;
                        var now = DateTime.Now;
                        if ((now - _lastFpsTime).TotalSeconds >= 1.0)
                        {
                            _currentFps = _frameCount;
                            _frameCount = 0;
                            _lastFpsTime = now;
                        }

                        BeginInvoke(new Action(() =>
                        {
                            var old = _screenBox.Image;
                            _screenBox.Image = frame;
                            old?.Dispose();

                            double mbps = (_bytesReceived * 8.0) / (1024.0 * 1024.0);
                            _bytesReceived = 0;
                            _lblStats.Text = $"🟢 {_remoteMachineName} | FPS: {_currentFps} | Пинг: {_pingMs} мс | {frame.Width}x{frame.Height}";
                        }));
                    }
                    else if (pkt.Value.type == PacketType.Pong)
                    {
                        long sentTicks = BitConverter.ToInt64(pkt.Value.payload, 0);
                        _pingMs = (int)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - sentTicks).TotalMilliseconds;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_cts.Token.IsCancellationRequested)
                {
                    _owner.AppendLog($"Ошибка подключения: {ex.Message}");
                    MessageBox.Show($"Не удалось подключиться к {_targetInput}:\n{ex.Message}", "Sako Desk", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                }
            }
        }

        private async Task PingLoopAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (_stream != null)
                    {
                        byte[] payload = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
                        await PacketIO.SendPacketAsync(_stream, _writeLock, PacketType.Ping, payload, _cts.Token);
                    }
                }
                catch { break; }
                await Task.Delay(2000, _cts.Token);
            }
        }

        // =====================================================================
        // COORDINATE MAPPING (PICTUREBOX ZOOM -> NORMALIZED 0..1 SCREEN COORDS)
        // =====================================================================
        private bool TryMapMouseToNormalized(int mouseX, int mouseY, out float normX, out float normY)
        {
            normX = 0; normY = 0;
            Image img = _screenBox.Image;
            if (img == null || _screenBox.ClientSize.Width <= 0 || _screenBox.ClientSize.Height <= 0)
                return false;

            float imgAspect = (float)img.Width / img.Height;
            float boxAspect = (float)_screenBox.ClientSize.Width / _screenBox.ClientSize.Height;

            float drawW, drawH, offsetX, offsetY;
            if (boxAspect > imgAspect)
            {
                drawH = _screenBox.ClientSize.Height;
                drawW = drawH * imgAspect;
                offsetX = (_screenBox.ClientSize.Width - drawW) / 2f;
                offsetY = 0;
            }
            else
            {
                drawW = _screenBox.ClientSize.Width;
                drawH = drawW / imgAspect;
                offsetX = 0;
                offsetY = (_screenBox.ClientSize.Height - drawH) / 2f;
            }

            normX = Math.Clamp((mouseX - offsetX) / drawW, 0f, 1f);
            normY = Math.Clamp((mouseY - offsetY) / drawH, 0f, 1f);
            return true;
        }

        private DateTime _lastMouseMoveSent = DateTime.MinValue;

        private async void OnScreenMouseMove(object sender, MouseEventArgs e)
        {
            if (!_chkControlEnabled.Checked || _stream == null) return;
            if ((DateTime.UtcNow - _lastMouseMoveSent).TotalMilliseconds < 25) return; // Throttle to 40 Hz
            _lastMouseMoveSent = DateTime.UtcNow;

            if (TryMapMouseToNormalized(e.X, e.Y, out float nx, out float ny))
                await SendMousePacketAsync(0, nx, ny, 0);
        }

        private async void OnScreenMouseDown(object sender, MouseEventArgs e)
        {
            _screenBox.Focus();
            if (!_chkControlEnabled.Checked || _stream == null) return;
            if (TryMapMouseToNormalized(e.X, e.Y, out float nx, out float ny))
            {
                byte act = e.Button == MouseButtons.Left ? (byte)1 : e.Button == MouseButtons.Right ? (byte)3 : (byte)5;
                await SendMousePacketAsync(act, nx, ny, 0);
            }
        }

        private async void OnScreenMouseUp(object sender, MouseEventArgs e)
        {
            if (!_chkControlEnabled.Checked || _stream == null) return;
            if (TryMapMouseToNormalized(e.X, e.Y, out float nx, out float ny))
            {
                byte act = e.Button == MouseButtons.Left ? (byte)2 : e.Button == MouseButtons.Right ? (byte)4 : (byte)6;
                await SendMousePacketAsync(act, nx, ny, 0);
            }
        }

        private async void OnScreenMouseWheel(object sender, MouseEventArgs e)
        {
            if (!_chkControlEnabled.Checked || _stream == null) return;
            if (TryMapMouseToNormalized(e.X, e.Y, out float nx, out float ny))
                await SendMousePacketAsync(7, nx, ny, e.Delta);
        }

        private async Task SendMousePacketAsync(byte action, float nx, float ny, int wheel)
        {
            try
            {
                byte[] buf = new byte[13];
                buf[0] = action;
                BitConverter.GetBytes(nx).CopyTo(buf, 1);
                BitConverter.GetBytes(ny).CopyTo(buf, 5);
                BitConverter.GetBytes(wheel).CopyTo(buf, 9);
                await PacketIO.SendPacketAsync(_stream, _writeLock, PacketType.MouseEvent, buf, _cts.Token);
            }
            catch { }
        }

        private async void OnViewerKeyDown(object sender, KeyEventArgs e)
        {
            if (_txtChatInput.Focused || !_chkControlEnabled.Checked || _stream == null) return;
            try
            {
                byte[] buf = new byte[] { (byte)e.KeyValue, 1 };
                await PacketIO.SendPacketAsync(_stream, _writeLock, PacketType.KeyEvent, buf, _cts.Token);
            }
            catch { }
        }

        private async void OnViewerKeyUp(object sender, KeyEventArgs e)
        {
            if (_txtChatInput.Focused || !_chkControlEnabled.Checked || _stream == null) return;
            try
            {
                byte[] buf = new byte[] { (byte)e.KeyValue, 0 };
                await PacketIO.SendPacketAsync(_stream, _writeLock, PacketType.KeyEvent, buf, _cts.Token);
            }
            catch { }
        }

        // =====================================================================
        // QUALITY PRESETS, FILE TRANSFER & SCREENSHOTS
        // =====================================================================
        private async Task SendQualityPresetAsync()
        {
            if (_stream == null) return;
            string cfg = _cmbQuality.SelectedIndex switch
            {
                0 => "90|1.0|30",
                1 => "65|1.0|25",
                2 => "45|0.75|25",
                _ => "30|0.50|15"
            };
            try
            {
                await PacketIO.SendPacketAsync(_stream, _writeLock, PacketType.QualityConfig, Encoding.UTF8.GetBytes(cfg), _cts.Token);
            }
            catch { }
        }

        private async Task SendFileDialogAsync()
        {
            if (_stream == null) return;
            using OpenFileDialog ofd = new OpenFileDialog { Title = "Выберите файл для отправки на удаленный ПК" };
            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                try
                {
                    byte[] fileBytes = await File.ReadAllBytesAsync(ofd.FileName);
                    string fileName = Path.GetFileName(ofd.FileName);

                    using MemoryStream ms = new MemoryStream();
                    using BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
                    bw.Write(fileName);
                    bw.Write(fileBytes.Length);
                    bw.Write(fileBytes);

                    await PacketIO.SendPacketAsync(_stream, _writeLock, PacketType.FileOffer, ms.ToArray(), _cts.Token);
                    _owner.AppendLog($"📁 Файл '{fileName}' ({fileBytes.Length / 1024.0:F1} КБ) отправлен на {_remoteMachineName}!");
                    MessageBox.Show($"Файл '{fileName}' успешно доставлен на рабочий стол удаленного ПК (папка SakoDesk_Received)!",
                        "Sako Desk", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка отправки файла: {ex.Message}", "Sako Desk", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void SaveScreenshot()
        {
            if (_screenBox.Image == null) return;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SakoDesk_Screenshots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            _screenBox.Image.Save(path, ImageFormat.Png);
            _owner.AppendLog($"📸 Скриншот сохранен: {path}");
            MessageBox.Show($"Скриншот сохранен:\n{path}", "Sako Desk", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
