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

    static string Wrap(string content) =>
        "{\"choices\":[{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(content) + "}}]}";

    static int Main()
    {
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

        int calls = 0; string seenUrl = null, seenKey = null;
        var r = Solver.Solve("2x + 3 = 11, solve for x", "sk-test", "https://api.openai.com/v1", "gpt-4o-mini",
            (url, body, key) => { calls++; seenUrl = url; seenKey = key; return Wrap(Good); });
        Check("verified first try", r.Verified && r.Retries == 0 && r.Evaluated == 4 && calls == 1);
        Check("url/key passed", seenUrl.EndsWith("/chat/completions") && seenKey == "sk-test");

        int n = 0;
        r = Solver.Solve("2x+3=11", "sk", "https://x", "m", (u, b, k) => { n++; return Wrap(n == 1 ? Wrong : Good); });
        Check("retry recovers", r.Verified && r.Retries == 1);

        int n2 = 0;
        r = Solver.Solve("1+1", "sk", "https://x", "m", (u, b, k) => { n2++; return n2 == 1 ? "no json" : Wrap(Good); });
        Check("invalid json then ok", r.Verified);

        bool threw2 = false;
        try { Solver.Solve("1+1", "sk", "https://x", "m", (u, b, k) => "nothing"); }
        catch (Solver.SolverException e) { threw2 = e.Code == "INVALID_JSON"; }
        Check("invalid twice raises", threw2);

        threw2 = false;
        try { Solver.Solve("1+1", ""); } catch (Solver.SolverException e) { threw2 = e.Code == "NO_API_KEY"; }
        Check("NO_API_KEY", threw2);

        int calls2 = 0; threw2 = false;
        try
        {
            Solver.Solve("1+1", "sk", "https://x", "m", (u, b, k) => { calls2++; throw new Solver.SolverException("HTTP_ERROR", "401"); });
        }
        catch (Solver.SolverException e) { threw2 = e.Code == "HTTP_ERROR"; }
        Check("http error no retry", threw2 && calls2 == 1);

        r = Solver.Solve("2x+3=11", "sk", "https://x", "m", (u, b, k) => Wrap(Wrong));
        Check("still wrong unverified", !r.Verified && r.Retries == 1);

        Console.WriteLine(_failures == 0 ? "\nALL PASS" : $"\n{_failures} FAILURES");
        return _failures == 0 ? 0 : 1;
    }
}
