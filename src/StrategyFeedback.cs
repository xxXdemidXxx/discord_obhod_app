using System;
using System.Collections.Generic;
using System.Linq;

namespace ZapretDesktop {
    // Network probes are evidence about endpoints, not proof that the installed client works.
    public sealed class StrategyFeedback {
        readonly Store store;
        readonly HashSet<string> rejected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string engine;
        public StrategyFeedback(Store store) {this.store=store;engine=store.Settings.Engine;}
        void Sync() {if(engine!=store.Settings.Engine){engine=store.Settings.Engine;rejected.Clear();}}
        public string Working {get {Sync();return store.Settings.WorkingStrategyEngine==engine?store.Settings.WorkingStrategy??"":"";}}
        public bool IsRejected(string strategy) {Sync();return rejected.Contains(strategy);}
        public void ResetRejected() {Sync();rejected.Clear();}
        public void Accept(string strategy) {
            Sync();if(!store.Strategies().Contains(strategy,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("Выбранная стратегия отсутствует в ядре.");
            store.Settings.WorkingStrategy=strategy;store.Settings.WorkingStrategyEngine=engine;
            store.Settings.Strategy=strategy;store.Save();rejected.Remove(strategy);
        }
        public void Reject(string strategy) {
            Sync();rejected.Add(strategy);
            if(String.Equals(Working,strategy,StringComparison.OrdinalIgnoreCase)) {
                store.Settings.WorkingStrategy="";store.Settings.WorkingStrategyEngine="";store.Save();
            }
        }
        public string Preferred(string current) {return !String.IsNullOrEmpty(Working) && !IsRejected(Working)?Working:current;}
        public string[] Order(string[] available,string current) {
            Sync();var names=available.Where(x=>!rejected.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var priority=new[]{Working,current}.Where(x=>names.Contains(x,StringComparer.OrdinalIgnoreCase));
            return priority.Concat(names).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
