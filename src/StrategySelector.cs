using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretDesktop {
    public sealed class CandidateResult {
        public string Strategy, Error = "";
        public CheckResult[] Results = new CheckResult[0];
        public bool Confirmed;
        public int Passed {get {return Results.Count(x => x.State == 1);}}
        public bool Complete {get {return Results.Length == ServiceChecks.Targets.Length && Results.All(x => x.State == 1);}}
        public double Latency {get {return Results.Length == 0 ? Double.MaxValue : Results.Average(x => (double)x.Milliseconds);}}
    }
    public sealed class SelectionResult {
        public readonly List<CandidateResult> Candidates = new List<CandidateResult>();
        public CandidateResult Winner;
    }
    public static class StrategySelector {
        public static async Task<SelectionResult> Run(string[] strategies,
            Func<string, CancellationToken, Task> start, Action stop,
            Func<CancellationToken, Task<CheckResult[]>> check, CancellationToken token,
            Action<int,int,string> progress, Action<CandidateResult> completed) {
            var selection = new SelectionResult(); bool keep = false;
            try {
                for (int i = 0; i < strategies.Length; i++) {
                    token.ThrowIfCancellationRequested(); stop();
                    var candidate = new CandidateResult {Strategy = strategies[i]};
                    if (progress != null) progress(i+1,strategies.Length,candidate.Strategy);
                    try {await start(candidate.Strategy,token); candidate.Results = await check(token);}
                    catch (OperationCanceledException) {throw;}
                    catch (Exception ex) {candidate.Error = ex.Message;}
                    finally {stop();}
                    selection.Candidates.Add(candidate); if (completed != null) completed(candidate);
                }
                foreach (var candidate in selection.Candidates.Where(x => x.Complete).OrderBy(x => x.Latency).ToArray()) {
                    token.ThrowIfCancellationRequested();
                    if (progress != null) progress(strategies.Length,strategies.Length,"Подтверждение: " + candidate.Strategy);
                    try {
                        await start(candidate.Strategy,token); var fresh = await check(token);
                        token.ThrowIfCancellationRequested(); candidate.Results = fresh;
                        if (candidate.Complete) {candidate.Confirmed = true; selection.Winner = candidate; keep = true; if (completed != null) completed(candidate); break;}
                        candidate.Error = "Повторная проверка не прошла"; if (completed != null) completed(candidate);
                    } catch (OperationCanceledException) {throw;}
                    catch (Exception ex) {candidate.Error = ex.Message; if (completed != null) completed(candidate);}
                    finally {if (!keep) stop();}
                }
                return selection;
            } finally {if (!keep) stop();}
        }
    }
}
