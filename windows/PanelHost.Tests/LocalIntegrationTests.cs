using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Services;
using PanelOS.Windows.Management;

namespace PanelHost.Tests;

[TestClass]
public sealed class LocalIntegrationTests
{
    [TestMethod]
    public void Clean_is_idempotent_and_stays_nfc_when_invisible_characters_separate_combining_marks()
    {
        for (var code = 0; code <= 0x10ffff; code++)
        {
            if (!Rune.IsValid(code)) continue;
            var rune = new Rune(code);
            if (Rune.GetUnicodeCategory(rune) is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format)) continue;
            var text = "e" + rune + "\u0301";
            var clean = LocalPayloadParser.Clean(text);
            Assert.IsTrue(clean.IsNormalized(NormalizationForm.FormC), $"U+{code:X}");
            Assert.AreEqual(clean, LocalPayloadParser.Clean(clean), $"U+{code:X}");
        }
    }

    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    [TestMethod]
    public void Payloads_are_plain_nfc_code_points_and_never_silently_truncated()
    {
        var valid = LocalPayloadParser.Notice(Json(new { title = string.Concat(Enumerable.Repeat("😀", 80)), text = "<b>x</b>\u202e\nsecond", level = "warning" }));
        Assert.AreEqual(80, valid.Title.EnumerateRunes().Count());
        Assert.AreEqual("<b>x</b>\nsecond", valid.Text);
        Assert.AreEqual(8, valid.DurationSeconds);
        Assert.AreEqual("é", LocalPayloadParser.Notice(Json(new { title = "e\u0301" })).Title);
        Assert.AreEqual("ab", LocalPayloadParser.Clean("a\ud800\udc00\ud800b\udc00").Replace("𐀀", ""));
        foreach (var payload in new[] {
            Json(new { title = new string('a', 81) }), Json(new { title = "ok", text = new string('a', 281) }),
            Json(new { title = "ok", text = "1\n2\n3\n4\n5\n6" }), Json(new { title = "ok", key = new string('k', 41) }),
            Json(new { title = "ok", page = new string('p', 41) }), Json(new { title = "ok", durationSeconds = 2 }),
            Json(new { title = "ok", durationSeconds = 31 }), Json(new { title = "ok", level = "allow" }),
            Json(new { title = "ok", token = "a-secret-in-body" }), Encoding.UTF8.GetBytes("{\"title\":\"a\",\"title\":\"b\"}") })
            Assert.ThrowsException<ArgumentException>(() => LocalPayloadParser.Notice(payload));
        try { LocalPayloadParser.Notice(Encoding.UTF8.GetBytes("{\"title\": {\"nested\":{\"too\":{\"deep\":1}}}}")); Assert.Fail("Deep JSON accepted"); }
        catch (JsonException) { }
        Assert.ThrowsException<DecoderFallbackException>(() => LocalPayloadParser.Notice([0xc3, 0x28]));
    }

    [TestMethod]
    public void Value_fields_and_ttl_enforce_security_boundaries()
    {
        var valid = LocalPayloadParser.Value(Json(new { name = new string('n', 40), value = new string('v', 140), unit = new string('u', 16), ttlSeconds = 86400 }));
        Assert.AreEqual(86400, valid.TtlSeconds);
        foreach (var payload in new[] { Json(new { name = new string('n', 41), value = "v" }),
            Json(new { name = "n", value = new string('v', 141) }), Json(new { name = "n", value = "v", unit = new string('u', 17) }),
            Json(new { name = "n", value = "v", ttlSeconds = 86401 }), Json(new { name = "n", value = "v", ttlSeconds = "3" }) })
            Assert.ThrowsException<ArgumentException>(() => LocalPayloadParser.Value(payload));
    }

    [TestMethod]
    public void Broker_bounds_memory_replaces_keys_and_expires_using_host_clock()
    {
        var clock = new FakeClock();
        var broker = new LocalNoticeBroker(clock);
        for (var i = 0; i < 21; i++) broker.Publish(new LocalNoticePayload("notice" + i, "text", "info", null, null, 5, 300));
        Assert.AreEqual(20, broker.Snapshot().Notices.Count);
        Assert.AreEqual("notice1", broker.Snapshot().Notices[0].Title);
        broker.Publish(new LocalNoticePayload("Permitir acesso?", "text", "info", "replace", null, 5, 300));
        broker.Publish(new LocalNoticePayload("replaced", "text", "info", "replace", null, 5, 300));
        Assert.AreEqual(1, broker.Snapshot().Notices.Count(x => x.Key == "replace"));
        for (var i = 0; i < 12; i++) Assert.IsTrue(broker.Publish(new LocalValuePayload("v" + i, "1", null, "info", 1)));
        Assert.IsFalse(broker.Publish(new LocalValuePayload("13th", "1", null, "info", null)));
        Assert.IsTrue(broker.Publish(new LocalValuePayload("v0", "2", null, "info", 1)));
        clock.Advance(301);
        Assert.AreEqual(0, broker.Snapshot().Notices.Count);
        Assert.AreEqual(0, broker.Snapshot().Values.Count);
    }

    [TestMethod]
    public void Rate_limits_enforce_general_and_notice_bursts_and_auth_lockout()
    {
        var limits = new LocalRateLimits();
        for (var i = 0; i < 20; i++) Assert.IsTrue(limits.Take(0, false));
        Assert.IsFalse(limits.Take(0, false));
        Assert.IsTrue(limits.Take(.1, false));
        var notices = new LocalRateLimits();
        for (var i = 0; i < 5; i++) Assert.IsTrue(notices.Take(0, true));
        Assert.IsFalse(notices.Take(0, true));
        Assert.IsTrue(notices.Take(.5, true));
        for (var i = 0; i < 10; i++) limits.AuthenticationFailed(1);
        Assert.IsTrue(limits.IsBlocked(60.9));
        Assert.IsFalse(limits.IsBlocked(61));
    }

    [TestMethod]
    public async Task Http_rejects_browser_fetch_rebinding_chunked_missing_length_and_oversize_before_body()
    {
        const string good = "POST /v1/notices HTTP/1.1\r\nHost: 127.0.0.1:8777\r\nContent-Type: application/json\r\nContent-Length: 2\r\nAuthorization: Bearer valid\r\n\r\n{}";
        var accepted = await LocalHttpRequest.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(good)), 8777, default);
        Assert.AreEqual("valid", accepted.Key);
        foreach (var (request, status) in new[] {
            (good.Replace("127.0.0.1:8777", "evil.example:8777").Replace("Content-Type:", "Origin: http://evil.example\r\nContent-Type:"),421),
            (good.Replace("Content-Type:", "Origin: http://evil.example\r\nContent-Type:"),403),
            (good.Replace("Content-Type:", "Sec-Fetch-Site: cross-site\r\nContent-Type:"),403),
            (good.Replace("POST", "OPTIONS"),405), (good.Replace("Content-Length: 2\r\n", ""),411),
            (good.Replace("Content-Length: 2", "Content-Length: 8193"),413),
            (good.Replace("Content-Length: 2", "Content-Length: 2\r\nTransfer-Encoding: chunked"),400),
            (good.Replace("/v1/notices", "/v1/notices?key=secret"),400),
            (good.Replace("application/json", "text/plain"),415),
            (good.Replace("Content-Length: 2", "Content-Length: 2\r\nContent-Length: 2"),400) })
        {
            var error = await Assert.ThrowsExceptionAsync<LocalHttpError>(() => LocalHttpRequest.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(request)), 8777, default));
            Assert.AreEqual(status, error.Status);
        }
    }

    [TestMethod]
    public async Task Loopback_service_is_off_by_default_rotates_key_and_closes_when_disabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PanelOS-IntegrationsTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalIntegrationSettingsStore(directory);
            var broker = new LocalNoticeBroker();
            await using var service = new LocalIntegrationService(store, broker, isElevated: () => false);
            service.Initialize();
            Assert.IsFalse(Directory.Exists(directory));
            Assert.AreEqual("disabled", service.Admin(new("status")).State);
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var key = service.Admin(new("rotate_key")).OneTimeKey!;
            Assert.AreEqual(51, key.Length);
            Assert.IsNull(service.Admin(new("status")).OneTimeKey);
            Assert.AreEqual("listening", service.Admin(new("configure", true, port)).State);
            async Task<string> Send(string token, string extra = "")
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                var body = "{\"title\":\"Permitir acesso?\"}";
                var bytes = Encoding.UTF8.GetBytes($"POST /v1/notices HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nAuthorization: Bearer {token}\r\n{extra}\r\n{body}");
                await client.GetStream().WriteAsync(bytes);
                using var reader = new StreamReader(client.GetStream());
                var status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(6));
                var length = 0;
                while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(6)) is string line && line.Length > 0)
                    if (line.StartsWith("Content-Length: ")) length = int.Parse(line[16..]);
                var response = new char[length];
                Assert.AreEqual(length, await reader.ReadBlockAsync(response, 0, length).WaitAsync(TimeSpan.FromSeconds(6)));
                return status + new string(response);
            }
            StringAssert.Contains(await Send(key), "200 Result");
            Assert.AreEqual(1, broker.Snapshot().Notices.Count);
            StringAssert.Contains(await Send(key, "Origin: http://evil.example\r\n"), "403 Result");
            var newKey = service.Admin(new("rotate_key")).OneTimeKey!;
            StringAssert.Contains(await Send(key), "401 Result");
            StringAssert.Contains(await Send(newKey), "200 Result");
            Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "settings.dpapi"))).Contains(newKey));
            Assert.AreEqual(Convert.ToBase64String(LocalIntegrationKey.Hash(newKey)), store.Load().KeyHash);
            service.Admin(new("configure", false));
            Assert.IsNull(store.Load().KeyHash);
            Assert.AreEqual(0, broker.Snapshot().Notices.Count);
            Assert.AreEqual("key_required", service.Admin(new("configure", true)).State);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
