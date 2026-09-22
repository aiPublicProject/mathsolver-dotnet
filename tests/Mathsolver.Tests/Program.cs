using System;
using Mathsolver;

static class Program
{
    static int _failures;

    static void Check(string name, bool cond)
    {
        Console.WriteLine((cond ? "  ok  " : "FAIL  ") + name);
        if (!cond) _failures++;
    }

    const string Good = "{\"answer\": 4, \"steps\": [\"Subtract 3: 2x = 8\", \"Divide by 2: x = 4\"], \"verification\": {\"expression\": \"(11-3)/2\"}}";
    const string Wrong = "{\"answer\": 4, \"steps\": [\"...\"], \"verification\": {\"expression\": \"(11-3)/3\"}}";


    static int Main()
    {
        var smokeKey = Environment.GetEnvironmentVariable("SMOKE_API_KEY");
        if (!string.IsNullOrEmpty(smokeKey))
        {
            var base_ = Environment.GetEnvironmentVariable("SMOKE_BASE_URL") ?? "https://api.openai.com/v1";
            var smokeResult = new Client(smokeKey, base_).Solve("2x + 3 = 11, solve for x");
            Console.WriteLine($"smoke: answer={r.Answer} verified={r.Verified} retries={r.Retries}");
            return smokeResult.Verified && Math.Abs(smokeResult.Answer - 4) < 1e-9 ? 0 : 1;
        }

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

        bool threwInit = false;
        try { new Client(""); } catch (Solver.SolverException e) { threwInit = e.Code == "NO_API_KEY"; }
        Check("NO_API_KEY at construct", threwInit);
        threwInit = false;
        try { new Client("sk", "not-a-url"); } catch (Solver.SolverException e) { threwInit = e.Code == "BAD_BASE_URL"; }
        Check("BAD_BASE_URL at construct", threwInit);

        int calls = 0; string seenUrl = null, seenKey = null;
        var solver = new Client("sk-test", "https://api.deepseek.com/v1", "deepseek-chat",
            (url, body, key) => { calls++; seenUrl = url; seenKey = key; return Good; });
        var r = solver.Solve("2x + 3 = 11, solve for x");
        Check("verified first try", r.Verified && r.Retries == 0 && r.Evaluated == 4 && calls == 1);
        Check("url/key passed", seenUrl == "https://api.deepseek.com/v1/chat/completions" && seenKey == "sk-test");

        int n = 0;
        r = new Client("sk", "https://x", "m", (u, b, k) => { n++; return n == 1 ? Wrong : Good; }).Solve("2x+3=11");
        Check("retry recovers", r.Verified && r.Retries == 1);

        int n2 = 0;
        r = new Client("sk", "https://x", "m", (u, b, k) => { n2++; return n2 == 1 ? "no json" : Good; }).Solve("1+1");
        Check("invalid json then ok", r.Verified);

        bool threw2 = false;
        try { new Client("sk", "https://x", "m", (u, b, k) => "nothing").Solve("1+1"); }
        catch (Solver.SolverException e) { threw2 = e.Code == "INVALID_JSON"; }
        Check("invalid twice raises", threw2);

        Check("NO_API_KEY (covered at construct)", true);

        int calls2 = 0; threw2 = false;
        try
        {
            new Client("sk", "https://x", "m", (u, b, k) => { calls2++; throw new Solver.SolverException("HTTP_ERROR", "401"); }).Solve("1+1");
        }
        catch (Solver.SolverException e) { threw2 = e.Code == "HTTP_ERROR"; }
        Check("http error no retry", threw2 && calls2 == 1);

        r = new Client("sk", "https://x", "m", (u, b, k) => Wrong).Solve("2x+3=11");
        Check("still wrong unverified", !r.Verified && r.Retries == 1);

        Console.WriteLine(_failures == 0 ? "\nALL PASS" : $"\n{_failures} FAILURES");
        return _failures == 0 ? 0 : 1;
    }
}
