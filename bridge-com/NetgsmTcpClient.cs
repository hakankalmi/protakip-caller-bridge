using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ProTakipCallerBridgeCom
{
    /// <summary>
    /// NetGSM Bulut Santral TCP subscriber — bridge-com için port edildi (eski
    /// .NET 8 bridge'deki NetgsmTcpClient.cs paritesi). PBX modundaki müşteriler
    /// için sekreterin PC'sinde TCP açar, gelen çağrıları bridge'in PostIngest
    /// fonksiyonuna iletir. Sunucu yönünden hiçbir state tutulmaz, ProTakip
    /// backend sadece credentials servis eder.
    ///
    /// Protocol (NegroPos anaForm.cs paritesi):
    ///   1. Connect to crmsntrl.netgsm.com.tr:9110
    ///   2. Send {command:"login", crm_id:&lt;rnd&gt;, username, password}\n\n
    ///   3. Server pushes JSON event frames separated by \n\n
    ///   4. React only when scenario in {Inbound_call, InboundtoPBX} or
    ///      context_name contains "mesai"; extract customer_num and fire.
    ///
    /// Reconnect otomatik: socket drop / auth fail → 10sn sonra yeniden dener.
    /// Stop() ile temiz kapanış.
    ///
    /// JSON parse: System.Text.Json net48'de yok, 3 alan için regex yeterli;
    /// dependency eklemiyoruz.
    /// </summary>
    internal sealed class NetgsmTcpClient : IDisposable
    {
        private const int ReconnectDelaySeconds = 10;

        private readonly string _host;
        private readonly int _port;
        private readonly string _username;
        private readonly string _password;
        private readonly Func<string, Task> _onIncomingNumber;
        private readonly Action<string> _log;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _loop;

        public string Version { get; }

        public NetgsmTcpClient(
            string host,
            int port,
            string username,
            string password,
            string version,
            Func<string, Task> onIncomingNumber,
            Action<string> log)
        {
            _host = host;
            _port = port;
            _username = username;
            _password = password;
            Version = version;
            _onIncomingNumber = onIncomingNumber;
            _log = log;
        }

        public void Start()
        {
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Stop()
        {
            try { _cts.Cancel(); } catch { }
            try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ConnectOnceAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log("Netgsm loop error: " + ex.Message);
                }

                if (ct.IsCancellationRequested) return;

                // Backoff — yanlış credential NetGSM'i dövmesin
                try { await Task.Delay(TimeSpan.FromSeconds(ReconnectDelaySeconds), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task ConnectOnceAsync(CancellationToken ct)
        {
            using (var client = new TcpClient())
            {
                Log(string.Format("Netgsm dialing {0}:{1}", _host, _port));
                // net48 TcpClient.ConnectAsync yok, TAP sarıcı API var ama
                // CancellationToken kabul etmez. Küçük helper ile iptal yönetimi.
                await ConnectWithCancellationAsync(client, _host, _port, ct).ConfigureAwait(false);
                Log("Netgsm connected");

                using (var stream = client.GetStream())
                {
                    // Login paketi — NegroPos ile birebir aynı şekil.
                    var crmId = Guid.NewGuid().ToString("N").Substring(0, 16);
                    var loginJson = BuildLoginJson(crmId, _username, _password);
                    var loginBytes = Encoding.UTF8.GetBytes(loginJson + "\n\n");
                    await stream.WriteAsync(loginBytes, 0, loginBytes.Length, ct).ConfigureAwait(false);
                    Log("Netgsm login sent");

                    var buffer = new byte[8192];
                    var accumulator = new StringBuilder();

                    while (!ct.IsCancellationRequested)
                    {
                        int read;
                        try
                        {
                            read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            if (ct.IsCancellationRequested) return;
                            Log("Netgsm read error: " + ex.Message);
                            return;
                        }

                        if (read == 0)
                        {
                            Log("Netgsm socket closed by server");
                            return;
                        }

                        accumulator.Append(Encoding.UTF8.GetString(buffer, 0, read));

                        // Event ayracı \n\n. Tamamlanan frame'leri boşalt,
                        // kuyrukta kalan parçayı sonraki read için bırak.
                        while (true)
                        {
                            var payload = accumulator.ToString();
                            var idx = payload.IndexOf("\n\n", StringComparison.Ordinal);
                            if (idx < 0) break;

                            var frame = payload.Substring(0, idx).Trim();
                            accumulator.Remove(0, idx + 2);

                            if (frame.Length == 0) continue;
                            await HandleFrameAsync(frame).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        private static async Task ConnectWithCancellationAsync(TcpClient client, string host, int port, CancellationToken ct)
        {
            var connectTask = client.ConnectAsync(host, port);
            using (ct.Register(() => { try { client.Close(); } catch { } }))
            {
                await connectTask.ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
        }

        private static string BuildLoginJson(string crmId, string username, string password)
        {
            // Küçük custom serializer — System.Text.Json/Newtonsoft dependency
            // eklemeden 4 alanlı sabit nesne üretiyoruz. Alan değerleri JSON
            // escape edilmeli ki password'te özel karakter varsa frame
            // kırılmasın.
            var sb = new StringBuilder();
            sb.Append('{');
            AppendField(sb, "command", "login", isFirst: true);
            AppendField(sb, "crm_id", crmId);
            AppendField(sb, "username", username);
            AppendField(sb, "password", password);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendField(StringBuilder sb, string key, string value, bool isFirst = false)
        {
            if (!isFirst) sb.Append(',');
            sb.Append('"').Append(key).Append("\":\"").Append(JsonEscape(value)).Append('"');
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 4);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:X4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // "scenario":"...", "context_name":"...", "customer_num":"..." —
        // string değerleri yakalar. Backslash kaçış zinciri NetGSM event'lerinde
        // çok nadir olduğu için basit [^"]+ pattern'i yeterli; frame bozuksa
        // zaten sonraki frame'de temiz gelir.
        private static readonly Regex ScenarioRegex =
            new Regex("\"scenario\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex ContextRegex =
            new Regex("\"context_name\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex CustomerNumRegex =
            new Regex("\"customer_num\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);

        private async Task HandleFrameAsync(string json)
        {
            string scenario = null, contextName = null, customerNum = null;

            var m = ScenarioRegex.Match(json);
            if (m.Success) scenario = m.Groups[1].Value;
            m = ContextRegex.Match(json);
            if (m.Success) contextName = m.Groups[1].Value;
            m = CustomerNumRegex.Match(json);
            if (m.Success) customerNum = m.Groups[1].Value;

            // Yalnızca NetGSM "gelen çağrı" dediğinde ingest'e gönder.
            // Diğer senaryolar (keep-alive, agent durum, IVR ilerlemesi) aynı
            // soketten akıyor; ghost popup atmamak için elemeliyiz.
            bool isIncoming =
                scenario == "Inbound_call" ||
                scenario == "InboundtoPBX" ||
                (contextName != null && contextName.IndexOf("mesai", StringComparison.OrdinalIgnoreCase) >= 0);

            if (!isIncoming) return;
            if (string.IsNullOrWhiteSpace(customerNum)) return;

            Log(string.Format("Netgsm ring: {0} (scenario={1})", customerNum, scenario));
            try
            {
                await _onIncomingNumber(customerNum).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("Netgsm ingest callback threw: " + ex.Message);
            }
        }

        private void Log(string message)
        {
            _log?.Invoke(message);
        }
    }
}
