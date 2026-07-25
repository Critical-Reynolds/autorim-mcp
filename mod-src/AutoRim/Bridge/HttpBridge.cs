using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using AutoRim.Core;

namespace AutoRim.Bridge
{
    /// <summary>
    /// Loopback-only HTTP endpoint for the MCP server.
    ///
    /// Deliberately a raw TcpListener with a small HTTP/1.1 parser rather than
    /// System.Net.HttpListener: the game runs on MonoBleedingEdge, and the raw socket path
    /// avoids Mono's HttpListener quirks and needs no URL ACL registration on Windows.
    ///
    /// Nothing in this file may touch game state. Requests are handed to Dispatcher, which
    /// marshals them onto the main thread.
    /// </summary>
    public static class HttpBridge
    {
        private const int MaxRequestBytes = 1024 * 1024;
        private const int SocketTimeoutMs = 15000;
        private const int DefaultCommandTimeoutMs = 10000;
        private const int MinCommandTimeoutMs = 1000;
        private const int MaxCommandTimeoutMs = 120000;

        private static TcpListener _listener;
        private static Thread _acceptThread;
        private static volatile bool _running;
        private static string _token;

        public static bool Running => _running;
        public static int Port { get; private set; }

        /// <summary>Set when the last start attempt failed, for reporting through control.bridge_status.</summary>
        public static string LastError { get; private set; }

        public static void Start(int port)
        {
            if (_running) return;

            try
            {
                _token = LoadOrCreateToken();

                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                Port = port;
                _running = true;
                LastError = null;

                _acceptThread = new Thread(AcceptLoop)
                {
                    Name = "AutoRim.Bridge",
                    IsBackground = true // dies with the process; RimWorld must never hang on exit
                };
                _acceptThread.Start();

                ARLog.Message($"Bridge listening on http://127.0.0.1:{port} (token in {Paths.TokenFile}).");
            }
            catch (SocketException ex)
            {
                _running = false;
                LastError = $"Could not bind port {port}: {ex.Message}";
                ARLog.Error($"{LastError} Change the port in Options > Mod settings > AutoRim.");
            }
            catch (Exception ex)
            {
                _running = false;
                LastError = ex.Message;
                ARLog.Exception("starting bridge", ex);
            }
        }

        public static void Stop()
        {
            if (!_running) return;
            _running = false;
            try
            {
                _listener?.Stop();
            }
            catch (Exception ex)
            {
                ARLog.Exception("stopping bridge", ex);
            }
            _listener = null;
            ARLog.Message("Bridge stopped.");
        }

        /// <summary>Applies a settings change (enable/disable, port) without a game restart.</summary>
        public static void ApplySettings()
        {
            var settings = AutoRimMod.Settings;
            if (settings == null) return;

            if (!settings.bridgeEnabled)
            {
                Stop();
                return;
            }

            if (_running && Port != settings.port) Stop();
            if (!_running) Start(settings.port);
        }

        // ---- token ------------------------------------------------------------------------

        private static string LoadOrCreateToken()
        {
            string path = Paths.TokenFile;
            try
            {
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path).Trim();
                    if (existing.Length >= 32) return existing;
                }
            }
            catch (Exception ex)
            {
                ARLog.Exception($"reading token file '{path}'", ex);
            }

            string token = GenerateToken();
            try
            {
                File.WriteAllText(path, token);
            }
            catch (Exception ex)
            {
                ARLog.Exception($"writing token file '{path}'", ex);
            }
            return token;
        }

        private static string GenerateToken()
        {
            var bytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            var sb = new StringBuilder(64);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Length-independent comparison, so the token cannot be probed by timing.</summary>
        private static bool TokenMatches(string presented)
        {
            if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(_token)) return false;
            if (presented.Length != _token.Length) return false;
            int diff = 0;
            for (int i = 0; i < presented.Length; i++) diff |= presented[i] ^ _token[i];
            return diff == 0;
        }

        // ---- accept loop ------------------------------------------------------------------

        private static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (_running) ARLog.Warning("Accept failed; bridge stopping.");
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return; // Stop() closed the listener
                }
                catch (Exception ex)
                {
                    ARLog.Exception("accepting connection", ex);
                    continue;
                }

                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
        }

        private static void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = SocketTimeoutMs;
                    client.SendTimeout = SocketTimeoutMs;

                    if (!IsLoopback(client))
                    {
                        ARLog.Warning("Rejected a non-loopback connection.");
                        return;
                    }

                    using (var stream = client.GetStream())
                    {
                        if (!TryReadRequest(stream, out var request))
                        {
                            WriteResponse(stream, 400, Envelope.Error(ErrorCode.BadArgs, "Malformed HTTP request."));
                            return;
                        }

                        var response = Route(request, out int status);
                        WriteResponse(stream, status, response);
                    }
                }
            }
            catch (IOException)
            {
                // Client hung up mid-exchange; nothing to report.
            }
            catch (Exception ex)
            {
                ARLog.Exception("handling request", ex);
            }
        }

        private static bool IsLoopback(TcpClient client)
        {
            try
            {
                return client.Client.RemoteEndPoint is IPEndPoint endpoint && IPAddress.IsLoopback(endpoint.Address);
            }
            catch
            {
                return false;
            }
        }

        // ---- routing ----------------------------------------------------------------------

        private static JsonValue Route(HttpRequest request, out int status)
        {
            if (request.Method == "GET" && request.Path == "/health")
            {
                status = 200;
                return Envelope.Ok(JsonValue.NewObject()
                    .Set("service", "autorim")
                    .Set("version", typeof(HttpBridge).Assembly.GetName().Version.ToString())
                    .Set("bridgeRunning", true)
                    .Set("gameLoaded", Dispatcher.GameLoopAlive)
                    .Set("queueDepth", Dispatcher.QueueDepth)
                    .Set("commandCount", CommandRegistry.Count));
            }

            if (!TokenMatches(request.Header("x-autorim-token")))
            {
                status = 401;
                return Envelope.Error(ErrorCode.NotAllowed,
                    "Missing or invalid X-AutoRim-Token header.",
                    $"The token is generated by the mod; read it from {Paths.TokenFile}.");
            }

            if (request.Method != "POST" || request.Path != "/rpc")
            {
                status = 404;
                return Envelope.Error(ErrorCode.NotFound,
                    $"No route for {request.Method} {request.Path}.",
                    "Use POST /rpc, or GET /health.");
            }

            if (!JsonValue.TryParse(request.Body, out var payload) || payload.Type != JsonType.Object)
            {
                status = 400;
                return Envelope.Error(ErrorCode.BadArgs, "Request body must be a JSON object.");
            }

            string command = payload["command"].AsString();
            if (string.IsNullOrEmpty(command))
            {
                status = 400;
                return Envelope.Error(ErrorCode.BadArgs, "Missing 'command'.");
            }

            var args = payload["args"];
            if (args.Type != JsonType.Object) args = JsonValue.NewObject();

            int timeoutMs = payload["timeoutMs"].Type == JsonType.Number
                ? payload["timeoutMs"].AsInt()
                : DefaultCommandTimeoutMs;
            timeoutMs = Math.Max(MinCommandTimeoutMs, Math.Min(MaxCommandTimeoutMs, timeoutMs));

            if (AutoRimMod.Settings != null && AutoRimMod.Settings.logRequests)
                ARLog.Message($"-> {command} {args}");

            var result = Dispatcher.ExecuteBlocking(command, args, timeoutMs);

            // Transport succeeded even when the command failed; the envelope carries the
            // outcome. Keeping this 200 means the MCP server reads one shape, not two.
            status = 200;
            return result;
        }

        // ---- HTTP plumbing ----------------------------------------------------------------

        private sealed class HttpRequest
        {
            public string Method = "";
            public string Path = "";
            public string Body = "";
            public readonly Dictionary<string, string> Headers =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        }

        private static bool TryReadRequest(NetworkStream stream, out HttpRequest request)
        {
            request = new HttpRequest();

            var buffer = new byte[8192];
            var accumulated = new MemoryStream();
            int headerEnd = -1;

            // Read until the blank line that ends the headers.
            while (headerEnd < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return false;

                accumulated.Write(buffer, 0, read);
                if (accumulated.Length > MaxRequestBytes) return false;

                headerEnd = FindHeaderEnd(accumulated.GetBuffer(), (int)accumulated.Length);
            }

            byte[] raw = accumulated.GetBuffer();
            int totalRead = (int)accumulated.Length;

            string headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return false;

            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return false;
            request.Method = requestLine[0].ToUpperInvariant();
            request.Path = StripQuery(requestLine[1]);

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string name = lines[i].Substring(0, colon).Trim();
                string value = lines[i].Substring(colon + 1).Trim();
                request.Headers[name] = value;
            }

            int contentLength = 0;
            if (request.Header("content-length") != null)
                int.TryParse(request.Header("content-length"), out contentLength);

            if (contentLength <= 0) return true;
            if (contentLength > MaxRequestBytes) return false;

            int bodyStart = headerEnd + 4;
            int bodyAvailable = totalRead - bodyStart;

            var body = new byte[contentLength];
            int copied = Math.Min(bodyAvailable, contentLength);
            if (copied > 0) Buffer.BlockCopy(raw, bodyStart, body, 0, copied);

            while (copied < contentLength)
            {
                int read = stream.Read(body, copied, contentLength - copied);
                if (read <= 0) return false;
                copied += read;
            }

            request.Body = Encoding.UTF8.GetString(body);
            return true;
        }

        private static string StripQuery(string path)
        {
            int q = path.IndexOf('?');
            return q >= 0 ? path.Substring(0, q) : path;
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    return i;
            }
            return -1;
        }

        private static void WriteResponse(NetworkStream stream, int status, JsonValue payload)
        {
            byte[] body = Encoding.UTF8.GetBytes(payload.ToString());

            var head = new StringBuilder(256);
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(StatusText(status)).Append("\r\n");
            head.Append("Content-Type: application/json; charset=utf-8\r\n");
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Connection: close\r\n\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string StatusText(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 404: return "Not Found";
                default: return "Error";
            }
        }
    }
}
