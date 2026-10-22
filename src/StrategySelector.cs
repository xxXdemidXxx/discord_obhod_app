using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace ZapretDesktop {
    public sealed class CandidateResult {
        public string Strategy,Error="";public CheckResult[] Results=new CheckResult[0];public bool Confirmed;
        public int Passed{get{return Results.Count(x=>!x.Informational && x.State==1);}}
        public int Total{get{return Results.Count(x=>!x.Informational && x.State!=3);}}
        public bool Complete{get{return Results.Length==ServiceChecks.Targets.Length && Passed>0 && Results.All(x=>x.Informational || x.State==1 || x.State==3);}}
        public double Latency{get{return Total==0?Double.MaxValue:Results.Where(x=>!x.Informational && x.State!=3).Average(x=>(double)x.Milliseconds);}}
    }
    public sealed class SelectionResult {public readonly List<CandidateResult> Candidates=new List<CandidateResult>();public CandidateResult Winner;}
    public static class StrategySelector {
        static async Task<bool> Confirm(CandidateResult candidate,Func<string,CancellationToken,Task> start,Action stop,Func<CancellationToken,Task<CheckResult[]>> check,CancellationToken token,Action<CandidateResult> completed){
            bool keep=false;
            try{await start(candidate.Strategy,token);candidate.Results=await check(token);token.ThrowIfCancellationRequested();if(candidate.Complete){candidate.Confirmed=true;keep=true;}else candidate.Error="Повторная проверка не прошла";}
            catch(OperationCanceledException){throw;}catch(EngineSetupException){throw;}catch(Exception ex){candidate.Error=ex.Message;}
            finally{if(!keep)stop();}
            if(completed!=null)completed(candidate);return keep;
        }
        public static async Task<SelectionResult> Run(string[] strategies,Func<string,CancellationToken,Task> start,Action stop,Func<CancellationToken,Task<CheckResult[]>> check,CancellationToken token,Action<int,int,string> progress,Action<CandidateResult> completed,bool firstMatch=false,Func<CancellationToken,Task<CheckResult[]>> confirmation=null,string preferred=null){
            var selection=new SelectionResult();bool keep=false;
            try{
                for(int i=0;i<strategies.Length;i++){
                    token.ThrowIfCancellationRequested();stop();var candidate=new CandidateResult{Strategy=strategies[i]};if(progress!=null)progress(i+1,strategies.Length,candidate.Strategy);
                    try{await start(candidate.Strategy,token);candidate.Results=await check(token);}catch(OperationCanceledException){throw;}catch(EngineSetupException){throw;}catch(Exception ex){candidate.Error=ex.Message;}finally{stop();}
                    selection.Candidates.Add(candidate);if(completed!=null)completed(candidate);
                    if(firstMatch && candidate.Complete){if(progress!=null)progress(i+1,strategies.Length,"Повторные пробы: "+candidate.Strategy);keep=await Confirm(candidate,start,stop,confirmation??check,token,completed);if(keep){selection.Winner=candidate;return selection;}}
                }
                if(!firstMatch)foreach(var candidate in selection.Candidates.Where(x=>x.Complete).OrderBy(x=>String.Equals(x.Strategy,preferred,StringComparison.OrdinalIgnoreCase)?0:1).ThenBy(x=>x.Latency).ToArray()){
                    token.ThrowIfCancellationRequested();if(progress!=null)progress(strategies.Length,strategies.Length,"Повторные пробы: "+candidate.Strategy);keep=await Confirm(candidate,start,stop,confirmation??check,token,completed);if(keep){selection.Winner=candidate;break;}
                }
                return selection;
            }finally{if(!keep)stop();}
        }
    }
}
