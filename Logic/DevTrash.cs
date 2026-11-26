using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using UnityEngine;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Trash;

namespace SupplyRunLogic
{
    // Development-only: feed a trash recycler a known load and read back what it pays.
    // Used to test the Fixed Cash Drops mod, not part of any shipped mod.
    internal static class DevTrash
    {
        private static Recycler Pick(NameValueCollection q)
        {
            var all = UnityEngine.Object.FindObjectsOfType<Recycler>();
            int.TryParse(q["i"] ?? "0", out int i);
            return i >= 0 && i < all.Length ? all[i] : null;
        }

        private static string State(Recycler r)
        {
            var trash = r.GetTrash();
            int sum = 0;
            var names = new List<string>();
            for (int k = 0; k < trash.Length; k++) { if (trash[k] != null) { sum += trash[k].SellValue; names.Add(trash[k].ID); } }
            return $"recycler '{r.name}' state={r.State} hatchOpen={r.IsHatchOpen} cashValue={r.cashValue} label='{(r.ValueLabel != null ? r.ValueLabel.text : "")}' trashInside={trash.Length} sumOfSellValue={sum} [{string.Join(",", names)}]";
        }

        internal static string Handle(string cmd, NameValueCollection q)
        {
            if (!cmd.StartsWith("t.")) return null;
            if (cmd == "t.breakatm")
            {
                // what hitting an ATM hard enough does: break it and run its payout routine
                var atms = UnityEngine.Object.FindObjectsOfType<Il2CppScheduleOne.Money.ATM>();
                int.TryParse(q["i"] ?? "0", out int ai);
                var atm = atms[ai];
                if (atm.IsBroken) return "ATM " + ai + " is already broken";
                atm.SendBreak();
                atm.StartCoroutine(atm.Method_Private_IEnumerator_PDM_0());
                return "ATM " + ai + " '" + atm.name + "' broken";
            }
            if (cmd == "t.breakcuke")
            {
                var vms = UnityEngine.Object.FindObjectsOfType<VendingMachine>();
                int.TryParse(q["i"] ?? "0", out int vi);
                var vm = vms[vi];
                if (vm.IsBroken) return "Cuke machine " + vi + " is already broken";
                int.TryParse(q["r"] ?? "0", out int which);
                Il2CppSystem.Collections.IEnumerator routine = which == 0 ? vm.Method_Private_IEnumerator_PDM_0() : which == 1 ? vm.Method_Private_IEnumerator_PDM_1() : vm.Method_Private_IEnumerator_PDM_2();
                string type = routine.Cast<Il2CppSystem.Object>().GetIl2CppType().FullName;
                if (q["dry"] == "1") return "routine " + which + " is " + type;
                vm.SendBreak();
                vm.StartCoroutine(routine);
                return "Cuke machine " + vi + " '" + vm.name + "' broken with routine " + type;
            }
            var r = Pick(q);
            if (r == null) return "no recycler";
            switch (cmd)
            {
                case "t.state": return State(r);
                case "t.fill":
                {
                    // items=glassbottle:3,cigarette:2
                    var tm = UnityEngine.Object.FindObjectOfType<TrashManager>();
                    Vector3 center = r.CheckCollider.transform.TransformPoint(r.CheckCollider.center);
                    int made = 0;
                    foreach (string part in (q["items"] ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] kv = part.Split(':');
                        int n = kv.Length > 1 ? int.Parse(kv[1]) : 1;
                        for (int k = 0; k < n; k++)
                        {
                            Vector3 pos = center + new Vector3(((made % 3) - 1) * 0.08f, 0f, ((made / 3 % 3) - 1) * 0.08f);
                            tm.CreateTrashItem(kv[0], pos, Quaternion.identity, Vector3.zero, Guid.NewGuid().ToString());
                            made++;
                        }
                    }
                    return $"created {made}\n" + State(r);
                }
                case "t.press": r.ButtonInteracted(); return "pressed\n" + State(r);
                case "t.collect":
                {
                    float before = Game.Money.cashBalance;
                    r.CashInteracted();
                    return $"collect: cash {before} -> {Game.Money.cashBalance}\n" + State(r);
                }
            }
            return null;
        }
    }
}
