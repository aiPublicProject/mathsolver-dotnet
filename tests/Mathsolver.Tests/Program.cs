using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mathsolver;

static class Program
{
    static int _failures;

    static void Check(string name, bool cond)
    {
        Console.WriteLine((cond ? "  ok  " : "FAIL  ") + name);
        if (!cond) _failures++;
    }

    static bool ThrowsWithCode(string want, Func<object?> fn)
    {
        try { fn(); return false; }
        catch (Solver.SolverException e) { return e.Code == want; }
    }

    // v0.2 protocol fixtures: the model returns program/steps/check — never an answer.
    const string Good = "{\"program\": \"let d = 11 - 3;\\nlet x = d / 2;\\nresult = x\", \"steps\": [\"Subtract 3: 2x = 8\", \"Divide by 2: x = 4\"], \"check\": \"2*{x} + 3 - 11\"}";
    const string NoCheck = "{\"program\": \"result = 0.15 * 80\", \"steps\": [\"Compute 15% of 80\"]}";
    const string WrongCheck = "{\"program\": \"let d = 11 - 3;\\nresult = d / 2\", \"steps\": [\"...\"], \"check\": \"2*{x} + 3 - 12\"}";
    const string BrokenProgram = "{\"program\": \"result = undefinedvar + 1\", \"steps\": []}";

    static int Main()
    {
        var smokeKey = Environment.GetEnvironmentVariable("SMOKE_API_KEY");
        if (!string.IsNullOrEmpty(smokeKey))
        {
            var base_ = Environment.GetEnvironmentVariable("SMOKE_BASE_URL");
            if (string.IsNullOrEmpty(base_)) base_ = "https://api.openai.com/v1";
            var model_ = Environment.GetEnvironmentVariable("SMOKE_MODEL"); // null/empty -> constructor default
            var smokeResult = new Client(smokeKey, base_, model_).Solve("2x + 3 = 11, solve for x");
            Console.WriteLine($"smoke: answer={smokeResult.Answer} verified={smokeResult.Verified} retries={smokeResult.Retries}");
            return smokeResult.Verified && Math.Abs(smokeResult.Answer - 4) < 1e-9 ? 0 : 1;
        }

        /* ---- expression evaluator ---- */
        Check("2*3+4=10", Math.Abs(Solver.EvalExpression("2*3+4") - 10) < 1e-9);
        Check("2+3*4=14", Math.Abs(Solver.EvalExpression("2+3*4") - 14) < 1e-9);
        Check("2^3^2=512", Math.Abs(Solver.EvalExpression("2^3^2") - 512) < 1e-9);
        Check("-3^2=-9", Math.Abs(Solver.EvalExpression("-3^2") + 9) < 1e-9);
        Check("sqrt(16)=4", Math.Abs(Solver.EvalExpression("sqrt(16)") - 4) < 1e-9);
        Check("min(3,5)=3", Math.Abs(Solver.EvalExpression("min(3,5)") - 3) < 1e-9);
        foreach (var bad in new[] { "System.Environment.Exit(0)", "1+2)", "foo(1)", "" })
        {
            bool threw = false;
            try { Solver.EvalExpression(bad); } catch (Solver.SolverException) { threw = true; }
            Check("rejects <" + bad + ">", threw);
        }
        Check("env resolves vars", Math.Abs(Solver.EvalExpression("d / 2", new Dictionary<string, double> { ["d"] = 8 }) - 4) < 1e-9);
        Check("env resolves two vars", Math.Abs(Solver.EvalExpression("x + y", new Dictionary<string, double> { ["x"] = 1.5, ["y"] = 2.5 }) - 4) < 1e-9);
        Check("undefined var errors", ThrowsWithCode("EXPR_UNKNOWN_ID", () => Solver.EvalExpression("d")));
        Check("env shadows pi", Math.Abs(Solver.EvalExpression("pi", new Dictionary<string, double> { ["pi"] = 3 }) - 3) < 1e-12);

        /* ---- program interpreter ---- */
        Check("runProgram let+result", Math.Abs(Solver.RunProgram("let d = 11 - 3;\nlet x = d / 2;\nresult = x") - 4) < 1e-9);
        Check("runProgram semicolons+bare", Math.Abs(Solver.RunProgram("let a = 3; let b = 4; a * b") - 12) < 1e-9);
        Check("runProgram bare", Math.Abs(Solver.RunProgram("0.15 * 80") - 12) < 1e-9);
        foreach (var bad in new[] { "result = undefinedvar + 1", "", "let a = 1; let b = 2" })
        {
            bool threw = false;
            try { Solver.RunProgram(bad); } catch (Solver.SolverException) { threw = true; }
            Check("runProgram rejects <" + bad.Replace("\n", ";") + ">", threw);
        }
        var pass = Solver.RunCheck("2*{x} + 3 - 11", 4);
        var fail = Solver.RunCheck("2*{x} + 3 - 12", 4);
        var alt = Solver.RunCheck("80*15/100 - {x}", 12);
        Check("runCheck pass", pass.Passed && Math.Abs(pass.Value) < 1e-9);
        Check("runCheck fail", !fail.Passed && Math.Abs(fail.Value + 1) < 1e-9);
        Check("runCheck recompute path", alt.Passed);

        /* ---- constructor validation ---- */
        Check("NO_API_KEY at construct", ThrowsWithCode("NO_API_KEY", () => new Client("")));
        Check("BAD_BASE_URL at construct", ThrowsWithCode("BAD_BASE_URL", () => new Client("sk", "not-a-url")));

        /* ---- solve via transport injection ---- */
        int calls = 0;
        string seenUrl = null, seenKey = null, seenBody = null;
        var solver = new Client("sk-test", "https://api.deepseek.com/v1", "deepseek-chat",
            (url, body, key) => { calls++; seenUrl = url; seenKey = key; seenBody = body; return Good; });
        var r = solver.Solve("2x + 3 = 11, solve for x");
        // 答案=执行产物(4), 代回检验=0; 模型 JSON 里没有 answer 字段
        Check("answer from execution, verified first try",
            r.Verified && r.Retries == 0 && r.CheckValue != null && Math.Abs(r.CheckValue.Value) < 1e-9
            && Math.Abs(r.Answer - 4) < 1e-9 && calls == 1);
        Check("url/body/key passed",
            seenUrl == "https://api.deepseek.com/v1/chat/completions" && seenKey == "sk-test"
            && seenBody.Contains("\"model\":\"deepseek-chat\"") && seenBody.Contains("\"temperature\":0"));
        Check("protocol: no answer field in model JSON", !Good.Contains("\"answer\""));

        r = new Client("sk", "https://x", "m", (u, b, k) => NoCheck).Solve("15% of 80");
        Check("no check -> unverified, answer from execution",
            Math.Abs(r.Answer - 12) < 1e-9 && !r.Verified && r.Check == "" && r.CheckValue == null);

        int n = 0;
        r = new Client("sk", "https://x", "m", (u, b, k) => { n++; return n == 1 ? WrongCheck : Good; }).Solve("2x+3=11");
        Check("check fail retry recovers", r.Verified && r.Retries == 1 && Math.Abs(r.Answer - 4) < 1e-9);

        int n2 = 0;
        r = new Client("sk", "https://x", "m", (u, b, k) => { n2++; return n2 == 1 ? BrokenProgram : Good; }).Solve("2x+3=11");
        Check("program error retry recovers", r.Verified && Math.Abs(r.Answer - 4) < 1e-9);

        bool persisted = false;
        try { new Client("sk", "https://x", "m", (u, b, k) => BrokenProgram).Solve("2x+3=11"); }
        catch (Solver.SolverException e) { persisted = e.Code.StartsWith("PROGRAM_") || e.Code.StartsWith("EXPR_"); }
        Check("program error persists -> PROGRAM_*/EXPR_* thrown", persisted);

        int n3 = 0;
        r = new Client("sk", "https://x", "m", (u, b, k) => { n3++; return n3 == 1 ? "no json" : Good; }).Solve("1+1");
        Check("invalid json then ok", r.Verified);

        Check("invalid twice raises", ThrowsWithCode("INVALID_JSON",
            () => new Client("sk", "https://x", "m", (u, b, k) => "nothing").Solve("1+1")));

        int calls2 = 0;
        Check("http error no retry", ThrowsWithCode("HTTP_ERROR", () =>
        {
            new Client("sk", "https://x", "m", (u, b, k) => { calls2++; throw new Solver.SolverException("HTTP_ERROR", "401"); }).Solve("1+1");
            return null;
        }) && calls2 == 1);

        r = new Client("sk", "https://x", "m", (u, b, k) => WrongCheck).Solve("2x+3=11");
        Check("check still failing after retry -> unverified, answer kept",
            Math.Abs(r.Answer - 4) < 1e-9 && !r.Verified && r.Retries == 1);

        /* ---- HTTP-interface mock (fake HttpMessageHandler below the default transport) ---- */
        HttpMockSection();

        Console.WriteLine(_failures == 0 ? "\nALL PASS" : $"\n{_failures} FAILURES");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Serves scripted model contents / HTTP statuses; records every request.</summary>
    sealed class FakeHandler : HttpMessageHandler
    {
        int _n;
        readonly string[] _contents;
        readonly int[] _statuses;
        public readonly List<string> Urls = new();
        public readonly List<string> Auths = new();
        public readonly List<string> Bodies = new();

        public FakeHandler(string[] contents, int[] statuses) { _contents = contents; _statuses = statuses; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int i = _n++;
            Urls.Add(request.RequestUri!.ToString());
            Auths.Add(request.Headers.Authorization?.ToString() ?? "");
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            string content = i < _contents.Length ? _contents[i] : Good;
            int status = i < _statuses.Length ? _statuses[i] : 200;
            string raw = status >= 300 ? "upstream boom"
                : JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
        }
    }

    static Client MockedClient(string apiKey, FakeHandler handler)
        => new(apiKey, "https://mock.test/v1", "mock-model", null, new HttpClient(handler));

    static void HttpMockSection()
    {
        var fake = new FakeHandler(new[] { Good }, Array.Empty<int>());
        var r = MockedClient("sk-mock", fake).Solve("2x + 3 = 11, solve for x");
        Check("http mock: round trip verified", r.Verified && r.Retries == 0 && Math.Abs(r.Answer - 4) < 1e-9);
        Check("http mock: one call", fake.Urls.Count == 1);
        Check("http mock: url joined", fake.Urls[0] == "https://mock.test/v1/chat/completions");
        Check("http mock: bearer auth", fake.Auths[0] == "Bearer sk-mock");
        string b0 = fake.Bodies[0];
        Check("http mock: model + temperature", b0.Contains("\"model\":\"mock-model\"") && b0.Contains("\"temperature\":0"));
        Check("http mock: system prompt shape", b0.Contains("\"role\":\"system\"") && b0.Contains("STRICT JSON"));

        var fake2 = new FakeHandler(new[] { WrongCheck, Good }, Array.Empty<int>());
        r = MockedClient("sk", fake2).Solve("2x+3=11");
        Check("http mock: retry recovers", r.Verified && r.Retries == 1 && fake2.Urls.Count == 2);
        Check("http mock: retry mentions failed verification", fake2.Urls.Count == 2 && fake2.Bodies[1].Contains("failed verification"));

        var fake3 = new FakeHandler(new[] { "certainly not json", Good }, Array.Empty<int>());
        r = MockedClient("sk", fake3).Solve("1+1");
        Check("http mock: invalid json re-ask", r.Verified && fake3.Urls.Count == 2);

        var fake4 = new FakeHandler(Array.Empty<string>(), new[] { 500 });
        Check("http mock: 500 -> HTTP_ERROR no retry", ThrowsWithCode("HTTP_ERROR",
            () => MockedClient("sk", fake4).Solve("1+1")) && fake4.Urls.Count == 1);

        var fake5 = new FakeHandler(Array.Empty<string>(), new[] { 401 });
        Check("http mock: 401 -> HTTP_ERROR", ThrowsWithCode("HTTP_ERROR",
            () => MockedClient("sk-bad", fake5).Solve("1+1")));
    }
}
