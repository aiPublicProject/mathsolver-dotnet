using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mathsolver
{
    /// <summary>
    /// BYOK AI math solver with execution-based verification (v0.2).
    /// Correctness model (PAL-style): the model never states the answer.
    /// It returns a small JavaScript-like PROGRAM; this library executes the
    /// program deterministically and the execution output IS the answer.
    /// For equations, a CHECK expression ({x} placeholder) must evaluate to 0
    /// when the computed answer is substituted back into the original equation.
    /// </summary>
    public static class Solver
    {
        public const string SystemPrompt = @"You are a precise math solver.
Reply with STRICT JSON only, no markdown fences, in this exact shape:
{""program"": ""<string>"", ""steps"": [<string>, ...], ""check"": ""<string>""}
Rules:
- ""program"" is a small JavaScript-like program that computes the final answer.
  One statement per line (or ; separated). Allowed statements:
      let NAME = EXPRESSION
      result = EXPRESSION
  EXPRESSIONs may use numbers, + - * / % ^ ( ), the functions
  abs sqrt sin cos tan ln log exp floor ceil round min max
  (log is base 10, ln is natural), the constants pi and e, and any
  variable defined by an earlier let. The value assigned to ""result""
  is the answer. Never state the answer as a number in text.
- ""steps"" is an array of short plain-language explanation strings.
- ""check"" is a verification expression containing the placeholder {x}.
  After solving, {x} is replaced by the computed answer and the whole
  expression must evaluate to 0.
  For equations, substitute the answer back into the original equation
  (e.g. 2x+3=11 -> ""2*{x}+3-11"").
  For arithmetic, recompute via a different path and subtract the answer
  (e.g. 15% of 80 -> ""80*15/100-{x}""). Provide ""check"" whenever possible.";

        internal static string CorrectionPrompt(string reason) =>
            "Your submission failed verification: " + reason
            + ". Re-derive the problem carefully and reply again with the same strict JSON shape.";

        public sealed class SolverException : Exception
        {
            public string Code { get; }
            public SolverException(string code, string message) : base(message) => Code = code;
        }

        public sealed class SolveResult
        {
            /// <summary>Output of executing the model's program locally.</summary>
            public double Answer;
            public List<string> Steps = new();
            /// <summary>The executed program (the answer's provenance).</summary>
            public string Program = "";
            /// <summary>Verification expression; empty string = none provided.</summary>
            public string Check = "";
            /// <summary>Evaluated check expression; null when no check provided.</summary>
            public double? CheckValue;
            /// <summary>True only when the check expression evaluated to ~0.</summary>
            public bool Verified;
            public int Retries;
        }

        public delegate string Transport(string url, string bodyJson, string apiKey);

        internal sealed class Parsed
        {
            public string Program = "";
            public List<string> Steps = new();
            public string Check = "";
        }

        /* ---------------- expression evaluator ---------------- */

        private static readonly Regex TokenRe = new(
            @"\s*(?:(?<num>\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|\.\d+)|(?<id>[a-zA-Z_][a-zA-Z_0-9]*)|(?<op>[-+*/%^(),]))",
            RegexOptions.Compiled);

        /// <summary>Evaluate a pure arithmetic expression string (no variables).</summary>
        public static double EvalExpression(string src) => EvalExpression(src, null);

        /// <summary>Evaluate with variable bindings; names are case-sensitive and shadow pi/e.</summary>
        public static double EvalExpression(string src, IReadOnlyDictionary<string, double> env)
        {
            if (string.IsNullOrWhiteSpace(src)) throw new SolverException("EXPR_EMPTY", "empty expression");
            var tokens = new List<object[]>();
            int covered = 0;
            foreach (Match m in TokenRe.Matches(src))
            {
                if (m.Length == 0) continue;
                covered = m.Index + m.Length;
                if (m.Groups["num"].Success) tokens.Add(new object[] { "num", double.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture) });
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
                    string raw = (string)t[1];
                    if (env != null && env.TryGetValue(raw, out var bound)) return bound;
                    var name = raw.ToLowerInvariant();
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

        /* ---------------- program interpreter ---------------- */

        private static readonly Regex LetRe = new(@"^let\s+([a-zA-Z_][a-zA-Z_0-9]*)\s*=\s*(.+)$", RegexOptions.Compiled);
        private static readonly Regex AssignRe = new(@"^([a-zA-Z_][a-zA-Z_0-9]*)\s*=\s*(.+)$", RegexOptions.Compiled);
        private static readonly Regex CheckXRe = new(@"\{\s*x\s*\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Execute a model-generated program. Statements (one per line or ;
        /// separated): let NAME = EXPR | NAME = EXPR | bare EXPR. The answer is
        /// the value of `result`, else the last bare expression. The model never
        /// states the answer as a number — execution output IS the answer.
        /// </summary>
        public static double RunProgram(string src)
        {
            if (string.IsNullOrWhiteSpace(src)) throw new SolverException("PROGRAM_EMPTY", "empty program");
            var env = new Dictionary<string, double>();
            bool resultDefined = false, lastDefined = false;
            double lastValue = 0;
            foreach (string raw in Regex.Split(src, @"[\n;]+"))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                var lm = LetRe.Match(line);
                if (lm.Success)
                {
                    env[lm.Groups[1].Value] = EvalExpression(lm.Groups[2].Value, env);
                    if (lm.Groups[1].Value == "result") resultDefined = true;
                    continue;
                }
                var am = AssignRe.Match(line);
                if (am.Success)
                {
                    env[am.Groups[1].Value] = EvalExpression(am.Groups[2].Value, env);
                    if (am.Groups[1].Value == "result") resultDefined = true;
                    continue;
                }
                lastValue = EvalExpression(line, env);
                lastDefined = true;
            }
            if (resultDefined) return env["result"];
            if (lastDefined) return lastValue;
            throw new SolverException("PROGRAM_NO_RESULT", "program produced no result");
        }

        /// <summary>Substitute the computed answer into a check expression ({x}
        /// placeholder) and evaluate it. Returns (value, passed) — passed when
        /// the value is ~0 (scaled tolerance).</summary>
        public static (double Value, bool Passed) RunCheck(string checkSrc, double answer)
        {
            string substituted = CheckXRe.Replace(checkSrc, _ => "(" + answer.ToString("R", CultureInfo.InvariantCulture) + ")");
            double value = EvalExpression(substituted);
            return (value, Math.Abs(value) <= 1e-6 * Math.Max(1, Math.Abs(answer)));
        }

        /* ---------------- JSON extraction (System.Text.Json, zero deps) ---------------- */

        internal static Parsed ParseModelReply(string text)
        {
            int start = text.IndexOf('{'), end = text.LastIndexOf('}');
            if (start < 0 || end <= start) throw new SolverException("INVALID_JSON", "no JSON object in reply");
            string body = text.Substring(start, end - start + 1);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException) { throw new SolverException("INVALID_JSON", "reply was not valid JSON"); }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("program", out var progEl) || progEl.GetString() is not string program || string.IsNullOrWhiteSpace(program))
                    throw new SolverException("INVALID_JSON", "missing program");
                var p = new Parsed { Program = program };
                if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
                    foreach (var s in stepsEl.EnumerateArray()) p.Steps.Add(s.ToString());
                if (root.TryGetProperty("check", out var checkEl) && checkEl.ValueKind == JsonValueKind.String)
                    p.Check = (checkEl.GetString() ?? "").Trim();
                return p;
            }
        }

        /// <summary>Re-serialize a parsed reply for the corrective retry message.</summary>
        internal static string ParsedJson(Parsed p)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("program", p.Program);
                w.WriteStartArray("steps");
                foreach (var s in p.Steps) w.WriteStringValue(s);
                w.WriteEndArray();
                if (p.Check.Length > 0) w.WriteString("check", p.Check); else w.WriteNull("check");
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /* ---------------- transport (HTTP interface) ---------------- */

        public static string DefaultTransport(string url, string bodyJson, string apiKey)
        {
            using var client = new System.Net.Http.HttpClient();
            return DefaultTransportWith(client, url, bodyJson, apiKey);
        }

        /// <summary>Default transport on a caller-owned HttpClient — inject one with a
        /// fake HttpMessageHandler in tests to run the real code path without sockets.</summary>
        public static string DefaultTransportWith(System.Net.Http.HttpClient client, string url, string bodyJson, string apiKey)
        {
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
            { Content = new System.Net.Http.StringContent(bodyJson, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            var res = client.SendAsync(req).GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode) throw new SolverException("HTTP_ERROR", "API responded " + (int)res.StatusCode);
            string raw = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ContentFromResponse(raw);
        }

        internal static string ContentFromResponse(string raw)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); }
            catch (JsonException) { throw new SolverException("HTTP_ERROR", "invalid JSON from API"); }
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    throw new SolverException("HTTP_ERROR", "missing choices");
                var content = choices[0].GetProperty("message").GetProperty("content").GetString();
                if (content == null) throw new SolverException("HTTP_ERROR", "missing message content");
                return content;
            }
        }
    }
}
