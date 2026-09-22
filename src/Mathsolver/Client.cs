using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Mathsolver
{
    /// <summary>
    /// BYOK client for an OpenAI-compatible endpoint. Instantiate once, solve many.
    /// <code>
    /// var solver = new Client("sk-...", "https://api.deepseek.com/v1", "deepseek-chat");
    /// var r = solver.Solve("2x + 3 = 11, solve for x"); // r.Verified == true
    /// </code>
    /// </summary>
    public sealed class Client
    {
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _model;
        private readonly Solver.Transport _transport;

        public Client(string apiKey, string baseUrl = "https://api.openai.com/v1",
                      string model = "gpt-4o-mini", Solver.Transport transport = null)
        {
            if (string.IsNullOrEmpty(apiKey)) throw new Solver.SolverException("NO_API_KEY", "apiKey is required (BYOK)");
            var base_ = (baseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
            if (!base_.StartsWith("http://", StringComparison.Ordinal) && !base_.StartsWith("https://", StringComparison.Ordinal))
                throw new Solver.SolverException("BAD_BASE_URL", "baseUrl must be an http(s) URL, e.g. https://api.deepseek.com/v1");
            _apiKey = apiKey;
            _baseUrl = base_;
            _model = string.IsNullOrEmpty(model) ? "gpt-4o-mini" : model;
            _transport = transport;
        }

        /// <summary>Solve a math problem. Verified is true only when the model's
        /// verification expression independently re-evaluates to the answer.</summary>
        public Solver.SolveResult Solve(string problem)
        {
            if (string.IsNullOrWhiteSpace(problem)) throw new Solver.SolverException("NO_PROBLEM", "problem must be non-empty");
            var tr = _transport ?? Solver.DefaultTransport;
            string url = _baseUrl + "/chat/completions";
            var messages = new List<string[]> { new[] { "system", Solver.SystemPrompt }, new[] { "user", problem } };
            string Call() => tr(url, JsonSerializer.Serialize(new { model = _model, messages = ToAnonymous(messages), temperature = 0 }), _apiKey);

            Solver.Parsed parsed;
            try { parsed = Solver.ParseModelReply(Call()); }
            catch (Solver.SolverException e)
            {
                if (e.Code != "INVALID_JSON") throw;
                messages.Add(new[] { "assistant", "invalid JSON" });
                messages.Add(new[] { "user", "Your reply was not valid JSON. Reply again with the exact strict JSON shape." });
                parsed = Solver.ParseModelReply(Call());
            }

            (double? ev, bool ok) Evaluate(Solver.Parsed p)
            {
                try { double v = Solver.EvalExpression(p.Expression); return (v, Solver.NumericallyEqual(v, p.Answer)); }
                catch (Solver.SolverException) { return (null, false); }
            }

            var (evaluated, verified) = Evaluate(parsed);
            int retries = 0;
            if (!verified)
            {
                retries = 1;
                messages.Add(new[] { "user", $"Your verification expression evaluated to {evaluated?.ToString() ?? "an error"}, which does not match your answer {parsed.Answer}. Re-derive carefully and reply again with the same strict JSON shape." });
                try
                {
                    var second = Solver.ParseModelReply(Call());
                    var (ev2, ok2) = Evaluate(second);
                    if (ev2 != null) evaluated = ev2;
                    if (ok2) { parsed = second; verified = true; }
                }
                catch (Solver.SolverException) { }
            }

            return new Solver.SolveResult { Answer = parsed.Answer, Steps = parsed.Steps, Expression = parsed.Expression, Evaluated = evaluated, Verified = verified, Retries = retries };
        }

        private static IEnumerable<object> ToAnonymous(List<string[]> messages)
        {
            foreach (var m in messages) yield return new { role = m[0], content = m[1] };
        }
    }
}
