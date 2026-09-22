using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mathsolver
{
    /// <summary>BYOK AI math solver utilities: local expression evaluation + strict-JSON protocol helpers.</summary>
    public static class Solver
    {
        public const string SystemPrompt = @"You are a precise math solver.
Reply with STRICT JSON only, no markdown fences, in this exact shape:
{""answer"": <number>, ""steps"": [<string>, ...], ""verification"": {""expression"": ""<string>""}}
Rules:
- ""answer"" must be a single number (the final result).
- ""steps"" must be an array of short plain-language explanation strings.
- ""verification.expression"" must be a pure arithmetic expression that
  evaluates to the answer. Allowed: numbers, + - * / % ^ ( ), and the
  functions abs sqrt sin cos tan ln log exp floor ceil round min max
  (log is base 10, ln is natural), and the constants pi and e.
- The expression must recompute the answer independently.";

        public sealed class SolverException : Exception
        {
            public string Code { get; }
            public SolverException(string code, string message) : base(message) => Code = code;
        }

        public sealed class SolveResult
        {
            public double Answer;
            public List<string> Steps = new();
            public string Expression = "";
            public double? Evaluated;
            public bool Verified;
            public int Retries;
        }

        public delegate string Transport(string url, string bodyJson, string apiKey);

        internal sealed class Parsed { public double Answer; public List<string> Steps = new(); public string Expression = ""; }

        /* ---------------- expression evaluator ---------------- */

        private static readonly Regex TokenRe = new(
            @"\s*(?:(?<num>\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|\.\d+)|(?<id>[a-zA-Z_][a-zA-Z_0-9]*)|(?<op>[-+*/%^(),]))",
            RegexOptions.Compiled);

        /// <summary>Evaluate a pure arithmetic expression string.</summary>
        public static double EvalExpression(string src)
        {
            if (string.IsNullOrWhiteSpace(src)) throw new SolverException("EXPR_EMPTY", "empty expression");
            var tokens = new List<object[]>();
            int covered = 0;
            foreach (Match m in TokenRe.Matches(src))
            {
                if (m.Length == 0) continue;
                covered = m.Index + m.Length;
                if (m.Groups["num"].Success) tokens.Add(new object[] { "num", double.Parse(m.Groups["num"].Value, System.Globalization.CultureInfo.InvariantCulture) });
                else if (m.Groups["id"].Success) tokens.Add(new object[] { "id", m.Groups["id"].Value });
                else tokens.Add(new object[] { m.Groups["op"].Value, null });
            }
            if (src.Substring(covered).Trim().Length > 0) throw new SolverException("EXPR_BAD_CHAR", "unexpected character");

            int pos = 0;
            object[] Next() { if (pos >= tokens.Count) throw new SolverException("EXPR_SYNTAX", "expected more tokens"); return tokens[pos++]; }
            object[]? Peek() { return pos < tokens.Count ? tokens[pos] : null; }
            bool Op(string op) { var t = Peek(); if (t != null && (string)t[0] == op) { pos++; return true; } return false; }
            void Expect(string op) { var t = Next(); if ((string)t[0] != op) throw new SolverException("EXPR_SYNTAX", "expected " + op); }

            double Expr() { double v = Term(); while (true) { if (Op("+")) v += Term(); else if (Op("-")) v -= Term(); else return v; } }
            double Term() { double v = Unary(); while (true) { if (Op("*")) v *= Unary(); else if (Op("/")) v /= Unary(); else if (Op("%")) v %= Unary(); else return v; } }
            double Unary() { if (Op("-")) return -Unary(); if (Op("+")) return Unary(); return Power(); }
            double Power() { double b = Atom(); if (Op("^")) return Math.Pow(b, Unary()); return b; }
            double Atom()
            {
                var t = Next();
                if ((string)t[0] == "num") return (double)t[1];
                if ((string)t[0] == "id")
                {
                    var name = ((string)t[1]).ToLowerInvariant();
                    if (Op("("))
                    {
                        var args = new List<double> { Expr() };
                        while (Op(",")) args.Add(Expr());
                        Expect(")");
                        return ApplyFn(name, args);
                    }
                    if (name == "pi") return Math.PI;
                    if (name == "e") return Math.E;
                    throw new SolverException("EXPR_UNKNOWN_ID", "unknown identifier " + name);
                }
                if ((string)t[0] == "(") { double v = Expr(); Expect(")"); return v; }
                throw new SolverException("EXPR_SYNTAX", "unexpected token " + t[0]);
            }

            double value = Expr();
            if (pos != tokens.Count) throw new SolverException("EXPR_TRAILING", "trailing tokens");
            if (double.IsInfinity(value) || double.IsNaN(value)) throw new SolverException("EXPR_NON_FINITE", "non-finite result");
            return value;
        }

        private static double ApplyFn(string name, List<double> args)
        {
            double a0 = args.Count > 0 ? args[0] : double.NaN;
            return name switch
            {
                "abs" => Math.Abs(a0),
                "sqrt" => Math.Sqrt(a0),
                "sin" => Math.Sin(a0),
                "cos" => Math.Cos(a0),
                "tan" => Math.Tan(a0),
                "ln" => Math.Log(a0),
                "log" => Math.Log10(a0),
                "exp" => Math.Exp(a0),
                "floor" => Math.Floor(a0),
                "ceil" => Math.Ceiling(a0),
                "round" => Math.Round(a0),
                "min" => args.Min(),
                "max" => args.Max(),
                _ => throw new SolverException("EXPR_UNKNOWN_FUNC", "unknown function " + name),
            };
        }

        /* ---------------- JSON ---------------- */

        private static readonly Regex AnswerRe = new(@"""answer""\s*:\s*(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)", RegexOptions.Compiled);
        private static readonly Regex ExprRe = new(@"""expression""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

        internal static Parsed ParseModelReply(string text)
        {
            int start = text.IndexOf('{'), end = text.LastIndexOf('}');
            if (start < 0 || end <= start) throw new SolverException("INVALID_JSON", "no JSON object in reply");
            string body = text.Substring(start, end - start + 1);
            var am = AnswerRe.Match(body);
            if (!am.Success) throw new SolverException("INVALID_JSON", "missing numeric answer");
            var em = ExprRe.Match(body);
            if (!em.Success) throw new SolverException("INVALID_JSON", "missing verification.expression");
            var p = new Parsed { Answer = double.Parse(am.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), Expression = em.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") };
            int s = body.IndexOf("\"steps\"");
            if (s >= 0)
            {
                int open = body.IndexOf('[', s), close = body.IndexOf(']', open);
                if (open >= 0 && close > open)
                    foreach (Match m in Regex.Matches(body.Substring(open, close - open), @"((?:[^""\\]|\\.)*)"))
                        p.Steps.Add(m.Groups[1].Value);
            }
            return p;
        }

        internal static bool NumericallyEqual(double a, double b) => Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

        public static string DefaultTransport(string url, string bodyJson, string apiKey)
        {
            using var client = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(bodyJson, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            var res = client.SendAsync(req).GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode) throw new SolverException("HTTP_ERROR", "API responded " + (int)res.StatusCode);
            string raw = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(raw);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (content == null) throw new SolverException("HTTP_ERROR", "missing message content");
            return content;
        }
    }
}
