using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace SupplyRunLogic
{
    // Development inspector: lets us explore the running game over HTTP instead of guessing.
    // Not part of the gameplay feature.
    internal static class Inspect
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const int MaxOut = 60000;

        private static Dictionary<string, Type> byFull;
        private static Dictionary<string, List<Type>> byShort;

        private static void BuildTypeIndex()
        {
            byFull = new Dictionary<string, Type>();
            byShort = new Dictionary<string, List<Type>>();
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts;
                try { ts = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { ts = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (Type t in ts)
                {
                    if (t.FullName == null) continue;
                    byFull[t.FullName] = t;
                    if (!byShort.TryGetValue(t.Name, out var l)) byShort[t.Name] = l = new List<Type>();
                    l.Add(t);
                }
            }
        }

        internal static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (byFull == null) BuildTypeIndex();
            name = name.Replace('/', '+');
            if (byFull.TryGetValue(name, out Type t)) return t;
            if (byFull.TryGetValue("Il2Cpp" + name, out t)) return t;
            if (byShort.TryGetValue(name, out var l))
            {
                // prefer game types over others when the short name is ambiguous
                return l.FirstOrDefault(x => x.FullName.StartsWith("Il2CppScheduleOne")) ?? l[0];
            }
            return null;
        }

        private static string Sig(MethodBase m)
        {
            return (m.DeclaringType != null ? m.DeclaringType.FullName : "?") + "::" + m.Name + "(" +
                   string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";
        }

        // list members of a type by reflection: cmd=members&type=T[&q=substring][&base=1]
        internal static string Members(NameValueCollection q)
        {
            Type t = FindType(q["type"]);
            if (t == null) return "type not found: " + q["type"];
            string f = q["q"];
            var sb = new StringBuilder(t.FullName + " : " + (t.BaseType != null ? t.BaseType.FullName : "") + "\n");
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | (q["base"] == "1" ? 0 : BindingFlags.DeclaredOnly);
            foreach (MemberInfo m in t.GetMembers(flags))
            {
                if (sb.Length > MaxOut) break;
                string n = m.Name;
                if (n.StartsWith("NativeFieldInfoPtr") || n.StartsWith("NativeMethodInfoPtr") || n.StartsWith("get_") || n.StartsWith("set_")) continue;
                if (f != null && n.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (m is MethodInfo mi) sb.AppendLine("  M " + (mi.IsStatic ? "static " : "") + mi.ReturnType.Name + " " + n + "(" + string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
                else if (m is PropertyInfo pi) sb.AppendLine("  P " + ((pi.GetMethod ?? pi.SetMethod).IsStatic ? "static " : "") + pi.PropertyType.Name + " " + n);
                else if (m is FieldInfo fi) sb.AppendLine("  F " + (fi.IsStatic ? "static " : "") + fi.FieldType.Name + " " + n);
                else if (m is Type nt) sb.AppendLine("  T " + nt.Name);
                else if (m is ConstructorInfo ci) sb.AppendLine("  C (" + string.Join(", ", ci.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
            }
            return sb.ToString();
        }

        internal static string PathOf(Transform t)
        {
            if (t == null) return "";
            string p = t.name;
            Transform cur = t.parent;
            int guard = 0;
            while (cur != null && guard++ < 24) { p = cur.name + "/" + p; cur = cur.parent; }
            return p;
        }

        private static object CastTo(Il2CppObjectBase o, Type t)
        {
            if (o == null) return null;
            if (t.IsInstanceOfType(o)) return o;
            MethodInfo cast = typeof(Il2CppObjectBase).GetMethod("TryCast").MakeGenericMethod(t);
            return cast.Invoke(o, null);
        }

        internal static List<object> Instances(Type t, NameValueCollection q)
        {
            var result = new List<object>();
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(t));
            string nameFilter = q["name"];
            bool live = q["live"] == "1";
            for (int i = 0; i < arr.Length; i++)
            {
                UnityEngine.Object o = arr[i];
                if (o == null) continue;
                if (nameFilter != null && (o.name ?? "").IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (live)
                {
                    Component c = o.TryCast<Component>();
                    if (c != null && (c.transform.root.name == "Player" || !c.gameObject.scene.IsValid())) continue;
                }
                object casted = CastTo(o, t);
                if (casted != null) result.Add(casted);
            }
            return result;
        }

        internal static string Find(NameValueCollection q)
        {
            Type t = FindType(q["type"]);
            if (t == null) return "type not found: " + q["type"];
            var list = Instances(t, q);
            var sb = new StringBuilder();
            sb.AppendLine(t.FullName + ": " + list.Count + " instance(s)");
            for (int i = 0; i < list.Count && sb.Length < MaxOut; i++)
            {
                var uo = list[i] as UnityEngine.Object;
                Component c = uo != null ? uo.TryCast<Component>() : null;
                sb.AppendLine($"[{i}] '{(uo != null ? uo.name : "?")}'" + (c != null ? $" activeInHierarchy={c.gameObject.activeInHierarchy} path={PathOf(c.transform)}" : ""));
            }
            return sb.ToString();
        }

        private static object Root(NameValueCollection q, out Type type, out string err)
        {
            err = null;
            type = FindType(q["type"]);
            if (type == null) { err = "type not found: " + q["type"]; return null; }
            if (q["static"] == "1") return null;
            var list = Instances(type, q);
            int.TryParse(q["index"] ?? "0", out int idx);
            if (idx < 0 || idx >= list.Count) { err = $"index {idx} out of range ({list.Count} instances of {type.FullName})"; return null; }
            return list[idx];
        }

        // ---- member path evaluation: a.b[2].c(1,"x") ----

        private static List<string> SplitTop(string s, char sep)
        {
            var parts = new List<string>();
            int depth = 0; bool quote = false; var cur = new StringBuilder();
            foreach (char ch in s)
            {
                if (ch == '"') quote = !quote;
                if (!quote) { if (ch == '(' || ch == '[') depth++; else if (ch == ')' || ch == ']') depth--; }
                if (ch == sep && depth == 0 && !quote) { parts.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            parts.Add(cur.ToString());
            return parts;
        }

        private static bool TryConvert(string raw, Type target, out object value)
        {
            raw = raw.Trim();
            value = null;
            try
            {
                if (raw == "null") return !target.IsValueType;
                if (target == typeof(string)) { value = raw.Trim('"'); return true; }
                if (target == typeof(int)) { value = int.Parse(raw, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(uint)) { value = uint.Parse(raw, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(float)) { value = float.Parse(raw, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(double)) { value = double.Parse(raw, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(bool)) { value = bool.Parse(raw); return true; }
                if (target.IsEnum) { value = Enum.Parse(target, raw, true); return true; }
            }
            catch { }
            return false;
        }

        private static object Step(object target, Type type, string seg)
        {
            // split off trailing [i] indexers
            var indexers = new List<string>();
            while (seg.EndsWith("]"))
            {
                int lb = seg.LastIndexOf('[');
                indexers.Insert(0, seg.Substring(lb + 1, seg.Length - lb - 2));
                seg = seg.Substring(0, lb);
            }

            object val;
            Type t = target != null ? target.GetType() : type;
            if (seg.EndsWith(")"))
            {
                int lp = seg.IndexOf('(');
                string name = seg.Substring(0, lp);
                string argText = seg.Substring(lp + 1, seg.Length - lp - 2);
                List<string> rawArgs = argText.Trim().Length == 0 ? new List<string>() : SplitTop(argText, ',');
                MethodInfo chosen = null; object[] args = null;
                foreach (MethodInfo m in t.GetMethods(All | BindingFlags.FlattenHierarchy).Where(m => m.Name == name && !m.IsGenericMethodDefinition))
                {
                    var ps = m.GetParameters();
                    if (ps.Length < rawArgs.Count) continue;
                    if (ps.Skip(rawArgs.Count).Any(p => !p.HasDefaultValue)) continue;
                    var conv = new object[ps.Length];
                    bool ok = true;
                    for (int i = 0; i < ps.Length && ok; i++)
                    {
                        if (i >= rawArgs.Count) conv[i] = ps[i].DefaultValue;
                        else ok = TryConvert(rawArgs[i], ps[i].ParameterType, out conv[i]);
                    }
                    if (ok) { chosen = m; args = conv; break; }
                }
                if (chosen == null) throw new Exception($"no method '{name}' with {rawArgs.Count} convertible arg(s) on {t.FullName}");
                val = chosen.Invoke(chosen.IsStatic ? null : target, args);
                if (chosen.ReturnType == typeof(void)) val = "(void ok)";
            }
            else if (seg.Length == 0)
            {
                val = target;
            }
            else
            {
                PropertyInfo p = null; FieldInfo f = null;
                for (Type c = t; c != null && p == null && f == null; c = c.BaseType)
                {
                    p = c.GetProperty(seg, All | BindingFlags.DeclaredOnly);
                    if (p == null) f = c.GetField(seg, All | BindingFlags.DeclaredOnly);
                }
                if (p != null) val = p.GetValue(p.GetMethod.IsStatic ? null : target);
                else if (f != null) val = f.GetValue(f.IsStatic ? null : target);
                else throw new Exception($"no member '{seg}' on {t.FullName}");
            }

            foreach (string ix in indexers)
            {
                if (val == null) throw new Exception("indexing null");
                int i = int.Parse(ix);
                if (val is Array a) { val = a.GetValue(i); continue; }
                PropertyInfo item = val.GetType().GetProperty("Item", new[] { typeof(int) });
                if (item == null) throw new Exception("no int indexer on " + val.GetType().FullName);
                val = item.GetValue(val, new object[] { i });
            }
            return val;
        }

        internal static object Eval(object root, Type rootType, string path)
        {
            object cur = root;
            Type curType = rootType;
            if (string.IsNullOrEmpty(path)) return cur;
            foreach (string seg in SplitTop(path, '.'))
            {
                cur = Step(cur, curType, seg);
                curType = cur != null ? cur.GetType() : null;
                if (cur == null) break;
            }
            return cur;
        }

        private static string RealType(object o)
        {
            try
            {
                if (o is Il2CppSystem.Object io) return io.GetIl2CppType().FullName;
            }
            catch { }
            return o.GetType().FullName;
        }

        internal static string Format(object o, int depth = 1)
        {
            if (o == null) return "null";
            if (o is string s) return "\"" + s + "\"";
            Type t = o.GetType();
            if (t.IsPrimitive || t.IsEnum) return Convert.ToString(o, CultureInfo.InvariantCulture);
            if (o is UnityEngine.Object uo)
            {
                if (uo == null) return "null(destroyed)";
                return $"'{uo.name}' <{RealType(o)}>";
            }
            if (depth > 0)
            {
                PropertyInfo count = t.GetProperty("Count", typeof(int)) ?? t.GetProperty("Length", typeof(int));
                PropertyInfo item = t.GetProperty("Item", new[] { typeof(int) });
                if (count != null && item != null)
                {
                    int n = (int)count.GetValue(o);
                    var sb = new StringBuilder($"<{t.Name} count={n}>");
                    for (int i = 0; i < n && i < 60; i++)
                    {
                        object e;
                        try { e = item.GetValue(o, new object[] { i }); } catch (Exception ex) { e = "<err " + ex.Message + ">"; }
                        sb.Append("\n    [" + i + "] " + Format(e, depth - 1));
                    }
                    return sb.ToString();
                }
            }
            string str;
            try { str = o.ToString(); } catch { str = "?"; }
            return str + " <" + RealType(o) + ">";
        }

        internal static string Get(NameValueCollection q)
        {
            object root = Root(q, out Type type, out string err);
            if (err != null) return err;
            object val = Eval(root, type, q["member"] ?? "");
            if (q["dump"] == "1" && val != null) return DumpObject(val, q["base"] != "0");
            return Format(val, 2);
        }

        private static string DumpObject(object o, bool includeBase)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<" + RealType(o) + "> as " + o.GetType().FullName);
            for (Type c = o.GetType(); c != null; c = c.BaseType)
            {
                if (c == typeof(Il2CppObjectBase) || c == typeof(Il2CppSystem.Object) || c == typeof(UnityEngine.Object) ||
                    c == typeof(Component) || c == typeof(Behaviour) || c == typeof(MonoBehaviour)) break;
                sb.AppendLine("-- " + c.FullName);
                foreach (PropertyInfo p in c.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (p.GetIndexParameters().Length > 0 || p.GetMethod == null) continue;
                    if (p.PropertyType == typeof(IntPtr)) continue;
                    string v;
                    try { v = Format(p.GetValue(o), 0); } catch (Exception e) { v = "<threw " + (e.InnerException ?? e).GetType().Name + ">"; }
                    sb.AppendLine("  " + p.PropertyType.Name + " " + p.Name + " = " + v);
                    if (sb.Length > MaxOut) return sb.ToString() + "\n... truncated";
                }
                if (!includeBase) break;
            }
            return sb.ToString();
        }

        internal static string Dump(NameValueCollection q)
        {
            object root = Root(q, out Type type, out string err);
            if (err != null) return err;
            object val = Eval(root, type, q["member"] ?? "");
            if (val == null) return "null";
            return DumpObject(val, q["base"] != "0");
        }

        private static void TreeRec(Transform t, int depth, int maxDepth, StringBuilder sb)
        {
            if (sb.Length > MaxOut) return;
            string comps = "";
            try
            {
                var cs = t.GetComponents<Component>();
                for (int i = 0; i < cs.Length; i++)
                {
                    if (cs[i] == null) continue;
                    string n = cs[i].GetIl2CppType().Name;
                    if (n == "RectTransform" || n == "Transform" || n == "CanvasRenderer") continue;
                    comps += n + " ";
                }
            }
            catch { }
            sb.AppendLine(new string(' ', depth * 2) + t.name + (t.gameObject.activeSelf ? "" : " (inactive)") + (comps.Length > 0 ? "  {" + comps.Trim() + "}" : ""));
            if (depth >= maxDepth) return;
            for (int i = 0; i < t.childCount; i++) TreeRec(t.GetChild(i), depth + 1, maxDepth, sb);
        }

        // tree of the transform reached from type/index/member (member must evaluate to a Component or GameObject)
        internal static string Tree(NameValueCollection q)
        {
            object root = Root(q, out Type type, out string err);
            if (err != null) return err;
            object val = Eval(root, type, q["member"] ?? "");
            Transform tr = null;
            if (val is Component c) tr = c.transform;
            else if (val is GameObject go) tr = go.transform;
            else if (val is UnityEngine.Object uo) { var cc = uo.TryCast<Component>(); if (cc != null) tr = cc.transform; }
            if (tr == null) return "not a Component/GameObject: " + Format(val, 0);
            int.TryParse(q["depth"] ?? "4", out int depth);
            var sb = new StringBuilder();
            TreeRec(tr, 0, depth, sb);
            return sb.ToString();
        }
    }
}
