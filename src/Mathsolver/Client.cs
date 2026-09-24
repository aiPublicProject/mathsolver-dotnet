using System;
using System.Collections.Generic;
using System.Net.Http;
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
        private readonly HttpClient _httpClient;

        public Client(string apiKey, string baseUrl = "https://api.openai.com/v1",
                      string model = "gpt-4o-mini", Solver.Transport transport = null, HttpClient httpClient = null)
        {
            if (string.IsNullOrEmpty(apiKey)) throw new Solver.SolverException("NO_API_KEY", "apiKey is required (BYOK)");
            var base_ = (baseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
            if (!base_.StartsWith("http://", StringComparison.Ordinal) && !base_.StartsWith("https://", StringComparison.Ordinal))
                throw new Solver.SolverException("BAD_BASE_URL", "baseUrl must be an http(s) URL, e.g. https://api.deepseek.com/v1");
            _apiKey = apiKey;
            _baseUrl = base_;
            _model = string.IsNullOrEmpty(model) ? "gpt-4o-mini" : model;
            _transport = transport;
            _httpClient = httpClient;
        }

        /// <summary>Solve a math problem. Answer is the output of executing the
        /// model's program; Verified is true only when the check expression
        /// ({x} substituted with the answer) evaluated to ~0.</summary>
        public Solver.SolveResult Solve(string problem)
        {
            if (string.IsNullOrWhiteSpace(problem)) throw new Solver.SolverException("NO_PROBLEM", "problem must be non-empty");
            Solver.Transport tr;
            if (_transport != null) tr = _transport;
            else if (_httpClient != null) tr = (u, b, k) => Solver.DefaultTransportWith(_httpClient, u, b, k);
            else tr = Solver.DefaultTransport;
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

            (bool ok, double answer, double? checkValue, bool verified, Solver.SolverException err) Attempt(Solver.Parsed p)
            {
                try
                {
                    double answer = Solver.RunProgram(p.Program);
                    double? cv = null;
                    bool v = false;
                    if (p.Check.Length > 0)
                    {
                        var (value, passed) = Solver.RunCheck(p.Check, answer);
                        cv = value;
                        v = passed;
                    }
                    return (true, answer, cv, v, null);
                }
                catch (Solver.SolverException ex)
                {
                    return (false, 0, null, false, ex);
                }
            }

            var outcome = Attempt(parsed);
            int retries = 0;
            if (!outcome.ok || !outcome.verified)
            {
                retries = 1;
                string reason = outcome.ok
                    ? $"check evaluated to {outcome.checkValue} instead of 0"
                    : $"program failed to execute ({outcome.err.Code}: {outcome.err.Message})";
                messages.Add(new[] { "assistant", Solver.ParsedJson(parsed) });
                messages.Add(new[] { "user", Solver.CorrectionPrompt(reason) });
                var secondParsed = Solver.ParseModelReply(Call()); // second failure throws
                var second = Attempt(secondParsed);
                if (!second.ok) throw second.err; // PROGRAM_* error persisted after retry
                parsed = secondParsed;
                outcome = second;
            }

            return new Solver.SolveResult
            {
                Answer = outcome.answer,
                Steps = parsed.Steps,
                Program = parsed.Program,
                Check = parsed.Check,
                CheckValue = outcome.checkValue,
                Verified = outcome.verified,
                Retries = retries,
            };
        }

        private static IEnumerable<object> ToAnonymous(List<string[]> messages)
        {
            foreach (var m in messages) yield return new { role = m[0], content = m[1] };
        }
    }
}
