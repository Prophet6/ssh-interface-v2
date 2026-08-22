using System;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronIO;
using Crestron.SimplSharp.Ssh;
using Crestron.SimplSharp.Ssh.Common;

namespace SSH_Interface
{
    public delegate void DataHandler(SimplSharpString data);
    public delegate void ConnectionStatusHandler(ushort status, SimplSharpString text);
    public delegate void FingerprintHandler(SimplSharpString fingerprint);

    public class SshInterface
    {
        public const ushort StatusIdle = 0;
        public const ushort StatusConnecting = 1;
        public const ushort StatusConnected = 2;
        public const ushort StatusDisconnected = 3;
        public const ushort StatusKeyMismatch = 4;

        public DataHandler SendFromDevice { get; set; }
        public ConnectionStatusHandler SendConnectionStatus { get; set; }
        public FingerprintHandler SendFingerprint { get; set; }

        private readonly object _sync = new object();
        private readonly CTimer _workTimer;
        private readonly CTimer _statusTimer;
        private readonly CTimer _watchTimer;

        private string _instanceId = "default";
        private ushort _debug;
        private bool _acceptAnyKey;
        private bool _wantConnected;
        private bool _busy;
        private bool _hostKeyRejected;
        private bool _programStopping;
        private ushort _status = StatusIdle;
        private ushort _pendingStatus;
        private string _pendingText = "";

        private string _host = "";
        private int _port = 22;
        private string _username = "";
        private string _password = "";
        private string _storedFingerprint = "";
        private string _pendingFingerprint = "";

        private SshClient _sshClient;
        private ShellStream _stream;

        public SshInterface()
        {
            _workTimer = new CTimer(WorkCallback, null, Timeout.Infinite);
            _statusTimer = new CTimer(StatusTimerCallback, null, Timeout.Infinite);
            _watchTimer = new CTimer(WatchCallback, null, Timeout.Infinite);
            CrestronEnvironment.ProgramStatusEventHandler += ProgramStatusHandler;
        }

        public void Debug(ushort enable)
        {
            _debug = enable;
        }

        public void Accept_Any_Key(ushort enable)
        {
            _acceptAnyKey = (enable != 0);
        }

        public void Unique_ID(string id)
        {
            _instanceId = FileToken(id);
        }

        public ushort SessionAlive()
        {
            try
            {
                lock (_sync)
                {
                    if (_status != StatusConnected)
                        return 0;
                    if (_sshClient == null)
                        return 0;
                    if (!_sshClient.IsConnected)
                        return 0;
                    return 1;
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public void LoadSettings()
        {
            lock (_sync)
            {
                _storedFingerprint = ReadKeyFile();
                if (_storedFingerprint.Length > 0)
                    Log("Loaded host key " + _storedFingerprint);
            }
        }

        public void Connect(string ipAddress, ushort port, string username, string password)
        {
            lock (_sync)
            {
                _host = ipAddress == null ? "" : ipAddress.Trim();
                _port = (port == 0) ? 22 : (int)port;
                _username = username == null ? "" : username;
                _password = password == null ? "" : password;
                _wantConnected = true;
                _hostKeyRejected = false;
                RaiseStatus(StatusConnecting, "Trying To Connect");
                _workTimer.Reset(0);
            }
        }

        public void Disconnect()
        {
            lock (_sync)
            {
                _wantConnected = false;
                _workTimer.Stop();
                TearDownLocked();
                RaiseStatus(StatusDisconnected, "Not Connected");
            }
        }

        public void Command_In(string data)
        {
            if (data == null || data.Length == 0)
                return;

            lock (_sync)
            {
                SendSshLocked(data);
            }
        }

        public void Accept_New_Key()
        {
            lock (_sync)
            {
                if (_pendingFingerprint.Length == 0)
                {
                    Log("Accept_New_Key with no pending fingerprint");
                    return;
                }

                _storedFingerprint = _pendingFingerprint;
                WriteKeyFile(_storedFingerprint);
                _hostKeyRejected = false;
                _wantConnected = true;
                Log("Accepted host key " + _storedFingerprint);
                RaiseStatus(StatusConnecting, "Trying To Connect");
                _workTimer.Reset(0);
            }
        }

        public void Decline_New_Key()
        {
            lock (_sync)
            {
                _pendingFingerprint = "";
                _hostKeyRejected = false;
                _wantConnected = false;
                _workTimer.Stop();
                TearDownLocked();
                RaiseStatus(StatusDisconnected, "Not Connected");
            }
        }

        private void WorkCallback(object userobj)
        {
            if (_programStopping || !_wantConnected)
                return;

            lock (_sync)
            {
                if (_programStopping || !_wantConnected || _busy)
                    return;

                _busy = true;
                try
                {
                    ConnectSshLocked();
                }
                finally
                {
                    _busy = false;
                }
            }
        }

        private void ConnectSshLocked()
        {
            if (_host.Length == 0 || _host == "0.0.0.0")
            {
                Log("Connect skipped (invalid IP)");
                RaiseStatus(StatusDisconnected, "Not Connected");
                _wantConnected = false;
                return;
            }

            TearDownLocked();
            _hostKeyRejected = false;

            try
            {
                PasswordAuthenticationMethod passwordAuth = new PasswordAuthenticationMethod(_username, _password);
                KeyboardInteractiveAuthenticationMethod kbdAuth = new KeyboardInteractiveAuthenticationMethod(_username);
                kbdAuth.AuthenticationPrompt += AuthenticationPromptHandler;

                ConnectionInfo info = new ConnectionInfo(_host, _port, _username, passwordAuth, kbdAuth);
                _sshClient = new SshClient(info);
                _sshClient.KeepAliveInterval = TimeSpan.Zero;
                _sshClient.ErrorOccurred += ClientErrorHandler;
                _sshClient.HostKeyReceived += HostKeyReceivedHandler;

                Log("Connecting to " + _host + ":" + _port + " as " + _username);
                _sshClient.Connect();

                if (!_sshClient.IsConnected)
                {
                    Log("Connect returned without a session");
                    TearDownLocked();
                    ScheduleReconnect();
                    return;
                }

                _stream = _sshClient.CreateShellStream("xterm", 80, 24, 800, 600, 4096);
                _stream.DataReceived += StreamDataReceivedHandler;
                _stream.ErrorOccurred += StreamErrorHandler;

                RaiseStatus(StatusConnected, "Connected");
                Log("SSH connected");
                HookSessionDisconnected(_sshClient);
                _watchTimer.Reset(2000);
            }
            catch (Exception ex)
            {
                Log("SSH connect error: " + ex.Message);
                TearDownLocked();

                if (_hostKeyRejected)
                {
                    _wantConnected = false;
                    RaiseStatus(StatusKeyMismatch, "Non-Matching Fingerprint");
                    RaiseFingerprint(_pendingFingerprint);
                    return;
                }

                ScheduleReconnect();
            }
        }

        private void SendSshLocked(string data)
        {
            if (_stream == null || !_stream.CanWrite || _sshClient == null || !_sshClient.IsConnected)
            {
                Log("Send dropped (not connected)");
                return;
            }

            try
            {
                _stream.Write(data);
                if (_debug >= 2)
                    Log("TX " + Sanitize(data));
            }
            catch (Exception ex)
            {
                Log("Send error: " + ex.Message);
                ErrorLog.Error("SSH_Interface_v2: Send error: {0}", ex.Message);
                HandleDrop();
            }
        }

        private void TearDownLocked()
        {
            try { _watchTimer.Stop(); }
            catch (Exception) { }

            if (_stream != null)
            {
                try { _stream.DataReceived -= StreamDataReceivedHandler; }
                catch (Exception) { }
                try { _stream.ErrorOccurred -= StreamErrorHandler; }
                catch (Exception) { }
                try { _stream.Dispose(); }
                catch (Exception ex) { Log("Dispose stream: " + ex.Message); }
                _stream = null;
            }

            if (_sshClient != null)
            {
                try { _sshClient.ErrorOccurred -= ClientErrorHandler; }
                catch (Exception) { }
                try { _sshClient.HostKeyReceived -= HostKeyReceivedHandler; }
                catch (Exception) { }
                try
                {
                    if (_sshClient.IsConnected)
                        _sshClient.Disconnect();
                }
                catch (Exception ex) { Log("SSH disconnect: " + ex.Message); }
                try { _sshClient.Dispose(); }
                catch (Exception ex) { Log("Dispose SSH client: " + ex.Message); }
                _sshClient = null;
            }
        }

        private void ScheduleReconnect()
        {
            if (!_wantConnected || _programStopping || _hostKeyRejected)
            {
                if (!_wantConnected)
                    RaiseStatus(StatusDisconnected, "Not Connected");
                return;
            }

            RaiseStatus(StatusConnecting, "Trying To Connect");
            _workTimer.Reset(5000);
            Log("Retry in 5s");
        }

        private void HostKeyReceivedHandler(object sender, HostKeyEventArgs e)
        {
            string fingerprint = FormatFingerprint(e.FingerPrint);
            _pendingFingerprint = fingerprint;
            Log("Host key " + e.HostKeyName + " " + fingerprint);

            if (_acceptAnyKey)
            {
                e.CanTrust = true;
                return;
            }

            if (_storedFingerprint.Length > 0 &&
                string.Compare(_storedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) == 0)
            {
                e.CanTrust = true;
                return;
            }

            e.CanTrust = false;
            _hostKeyRejected = true;
        }

        private void AuthenticationPromptHandler(object sender, AuthenticationPromptEventArgs e)
        {
            foreach (AuthenticationPrompt prompt in e.Prompts)
            {
                string request = prompt.Request == null ? "" : prompt.Request.ToLower();
                if (request.IndexOf("user") >= 0 && request.IndexOf("pass") < 0)
                    prompt.Response = _username;
                else
                    prompt.Response = _password;
            }
        }

        private void StreamDataReceivedHandler(object sender, ShellDataEventArgs e)
        {
            ShellStream stream = sender as ShellStream;
            if (stream == null)
                return;

            try
            {
                StringBuilder sb = new StringBuilder();
                while (stream.DataAvailable)
                    sb.Append(stream.Read());

                if (sb.Length > 0)
                    RaiseFromDevice(sb.ToString());
            }
            catch (Exception ex)
            {
                Log("SSH RX error: " + ex.Message);
                ErrorLog.Error("SSH_Interface_v2: SSH RX error: {0}", ex.Message);
            }
        }

        private void StreamErrorHandler(object sender, ExceptionEventArgs e)
        {
            string msg = (e != null && e.Exception != null) ? e.Exception.Message : "shell error";
            Log("SSH shell error: " + msg);
            HandleDrop();
        }

        private void ClientErrorHandler(object sender, ExceptionEventArgs e)
        {
            string msg = (e != null && e.Exception != null) ? e.Exception.Message : "client error";
            Log("SSH client error: " + msg);
            HandleDrop();
        }

        private void HandleDrop()
        {
            lock (_sync)
            {
                if (_sshClient == null && _stream == null && _status != StatusConnected)
                    return;

                TearDownLocked();
                ScheduleReconnect();
            }
        }

        private void WatchCallback(object userobj)
        {
            SshClient client;
            lock (_sync)
            {
                if (!_wantConnected || _status != StatusConnected)
                    return;
                client = _sshClient;
            }

            if (client == null || SocketIsHungUp(client))
            {
                Log("SSH TCP hangup detected");
                HandleDrop();
                return;
            }

            try { _watchTimer.Reset(2000); }
            catch (Exception) { }
        }

        private void HookSessionDisconnected(SshClient client)
        {
            try
            {
                object session = GetSessionObject(client);
                if (session == null)
                    return;
                EventInfo ev = session.GetType().GetEvent("Disconnected",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ev != null)
                    ev.AddEventHandler(session, new EventHandler(OnSessionDisconnected));
            }
            catch (Exception ex)
            {
                Log("HookSessionDisconnected: " + ex.Message);
            }
        }

        private void OnSessionDisconnected(object sender, EventArgs e)
        {
            Log("SSH Session.Disconnected");
            HandleDrop();
        }

        private static object GetSessionObject(SshClient client)
        {
            if (client == null)
                return null;
            PropertyInfo prop = typeof(BaseClient).GetProperty("Session",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null)
                return null;
            return prop.GetValue(client, null);
        }

        private static Socket TryGetSessionSocket(SshClient client)
        {
            try
            {
                object session = GetSessionObject(client);
                if (session == null)
                    return null;
                FieldInfo[] fields = session.GetType().GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (fields[i].FieldType == typeof(Socket))
                        return fields[i].GetValue(session) as Socket;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static bool SocketIsHungUp(SshClient client)
        {
            try
            {
                if (!client.IsConnected)
                    return true;

                Socket socket = TryGetSessionSocket(client);
                if (socket == null)
                    return false;

                if (!socket.Connected)
                    return true;

                if (socket.Poll(0, SelectMode.SelectError))
                    return true;

                if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                    return true;

                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private void StatusTimerCallback(object userobj)
        {
            ushort status;
            string text;
            ConnectionStatusHandler handler;
            lock (_sync)
            {
                status = _pendingStatus;
                text = _pendingText;
                handler = SendConnectionStatus;
            }

            if (handler == null)
                return;

            try { handler(status, new SimplSharpString(text ?? "")); }
            catch (Exception ex)
            {
                ErrorLog.Error("SSH_Interface_v2: SendConnectionStatus handler error: {0}", ex.Message);
            }
        }

        private void ProgramStatusHandler(eProgramStatusEventType programEventType)
        {
            if (programEventType != eProgramStatusEventType.Stopping)
                return;

            _programStopping = true;
            _wantConnected = false;
            try { _workTimer.Stop(); }
            catch (Exception) { }
            try { _watchTimer.Stop(); }
            catch (Exception) { }
            lock (_sync)
            {
                TearDownLocked();
            }
        }

        private void RaiseFromDevice(string data)
        {
            if (data == null || data.Length == 0)
                return;

            if (_debug >= 2)
                Log("RX " + Sanitize(data));

            DataHandler handler = SendFromDevice;
            if (handler == null)
                return;

            const int chunkSize = 250;
            int offset = 0;
            while (offset < data.Length)
            {
                int len = data.Length - offset;
                if (len > chunkSize)
                    len = chunkSize;

                string chunk = data.Substring(offset, len);
                try { handler(new SimplSharpString(chunk)); }
                catch (Exception ex)
                {
                    ErrorLog.Error("SSH_Interface_v2: SendFromDevice handler error: {0}", ex.Message);
                }
                offset += len;
            }
        }

        private void RaiseStatus(ushort status, string text)
        {
            _status = status;
            _pendingStatus = status;
            _pendingText = text == null ? "" : text;
            Log(_pendingText);
            try { _statusTimer.Reset(0); }
            catch (Exception)
            {
                StatusTimerCallback(null);
            }
        }

        private void RaiseFingerprint(string fingerprint)
        {
            FingerprintHandler handler = SendFingerprint;
            if (handler == null)
                return;

            try { handler(new SimplSharpString(fingerprint)); }
            catch (Exception ex)
            {
                ErrorLog.Error("SSH_Interface_v2: SendFingerprint handler error: {0}", ex.Message);
            }
        }

        private string KeyFilePath()
        {
            return @"\NVRAM\SSH_Interface_v2_" + _instanceId + ".key";
        }

        private string ReadKeyFile()
        {
            string path = KeyFilePath();
            try
            {
                if (!File.Exists(path))
                    return "";

                using (StreamReader reader = new StreamReader(path))
                {
                    string value = reader.ReadToEnd();
                    return value == null ? "" : value.Trim();
                }
            }
            catch (Exception ex)
            {
                Log("LoadSettings: " + ex.Message);
                return "";
            }
        }

        private void WriteKeyFile(string fingerprint)
        {
            string path = KeyFilePath();
            try
            {
                string folder = @"\NVRAM";
                if (!Directory.Exists(folder))
                    Directory.Create(folder);

                using (StreamWriter writer = new StreamWriter(path, false))
                {
                    writer.Write(fingerprint);
                }
            }
            catch (Exception ex)
            {
                Log("Save key: " + ex.Message);
                ErrorLog.Error("SSH_Interface_v2: Save key error: {0}", ex.Message);
            }
        }

        private static string FormatFingerprint(byte[] fingerprint)
        {
            if (fingerprint == null || fingerprint.Length == 0)
                return "";

            StringBuilder sb = new StringBuilder(fingerprint.Length * 3);
            for (int i = 0; i < fingerprint.Length; i++)
            {
                if (i > 0)
                    sb.Append(':');
                sb.Append(fingerprint[i].ToString("X2"));
            }
            return sb.ToString();
        }

        private static string Sanitize(string value)
        {
            if (value == null)
                return "";
            return value.Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string FileToken(string value)
        {
            if (value == null || value.Length == 0)
                return "default";

            StringBuilder sb = new StringBuilder(value.Length);
            int n = value.Length;
            if (n > 64)
                n = 64;

            for (int i = 0; i < n; i++)
            {
                char c = value[i];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
                else
                    sb.Append('_');
            }

            if (sb.Length == 0)
                return "default";

            return sb.ToString();
        }

        private void Log(string message)
        {
            if (_debug == 0)
                return;

            CrestronConsole.PrintLine("[SSH_v2 id={0}] {1}", _instanceId, message);
        }
    }
}
