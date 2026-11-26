using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Iced.Intel;
using Il2CppInterop.Common;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.XrefScans;

namespace SupplyRunLogic
{
    // Development tool: lists what a native game method calls and which string literals / classes it
    // references. All memory access goes through ReadProcessMemory so a bad pointer cannot crash the game
    // (the stock XrefScanner dereferences uninitialized metadata slots and takes the process down).
    internal static class SafeXref
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private static bool Read(ulong addr, byte[] buf, int count)
        {
            if (addr < 0x10000 || addr > 0x00007FFFFFFFFFFFUL) return false;
            return ReadProcessMemory(GetCurrentProcess(), (IntPtr)(long)addr, buf, (IntPtr)count, out IntPtr n) && (long)n == count;
        }

        private static bool ReadU64(ulong addr, out ulong value)
        {
            var b = new byte[8];
            value = 0;
            if (!Read(addr, b, 8)) return false;
            value = BitConverter.ToUInt64(b, 0);
            return true;
        }

        private static byte[] ReadCode(ulong start, int max)
        {
            var result = new List<byte>();
            ulong cur = start;
            while (result.Count < max)
            {
                int chunk = (int)(0x1000 - (cur & 0xFFF));
                var b = new byte[chunk];
                if (!Read(cur, b, chunk)) break;
                result.AddRange(b);
                cur += (ulong)chunk;
            }
            return result.ToArray();
        }

        private static string ReadCString(ulong addr)
        {
            var b = new byte[96];
            if (!Read(addr, b, 96))
            {
                b = new byte[16];
                if (!Read(addr, b, 16)) return null;
            }
            int n = Array.IndexOf(b, (byte)0);
            if (n <= 0) return null;
            for (int i = 0; i < n; i++) if (b[i] < 0x20 || b[i] > 0x7E) return null;
            return Encoding.ASCII.GetString(b, 0, n);
        }

        private static ulong stringClass;

        // Describe what a global slot points at: a managed string literal, or an Il2CppClass.
        private static string DescribeGlobal(ulong slot)
        {
            if (!ReadU64(slot, out ulong v) || v == 0 || (v & 1) != 0) return null; // odd = still an encoded metadata token
            if (!ReadU64(v, out ulong first)) return null;
            if (stringClass == 0) stringClass = (ulong)(long)Il2CppClassPointerStore<string>.NativeClassPtr;
            if (first == stringClass)
            {
                var lenBuf = new byte[4];
                if (!Read(v + 0x10, lenBuf, 4)) return null;
                int len = BitConverter.ToInt32(lenBuf, 0);
                if (len < 0 || len > 400) return null;
                var chars = new byte[len * 2];
                if (len > 0 && !Read(v + 0x14, chars, len * 2)) return null;
                return "str  \"" + Encoding.Unicode.GetString(chars) + "\"";
            }
            // Il2CppClass: image, gc_desc, name, namespaze
            if (ReadU64(v + 0x10, out ulong namePtr) && ReadU64(v + 0x18, out ulong nsPtr))
            {
                string name = ReadCString(namePtr);
                if (name != null && name.Length > 1 && (char.IsLetter(name[0]) || name[0] == '_' || name[0] == '<'))
                {
                    string ns = ReadCString(nsPtr);
                    return "type " + (string.IsNullOrEmpty(ns) ? "" : ns + ".") + name;
                }
            }
            return null;
        }

        private static string Sig(MethodBase m)
        {
            return (m.DeclaringType != null ? m.DeclaringType.FullName : "?") + "::" + m.Name + "(" +
                   string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";
        }

        private static ulong CodeStart(MethodBase m)
        {
            FieldInfo f = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m);
            if (f == null) return 0;
            ulong methodInfo = (ulong)(long)(IntPtr)f.GetValue(null);
            return ReadU64(methodInfo, out ulong code) ? code : 0;
        }

        private static void ScanMethod(MethodBase m, StringBuilder sb, bool showUnresolved)
        {
            ulong start = CodeStart(m);
            if (start == 0) { sb.AppendLine("  (no native method pointer)"); return; }
            byte[] code = ReadCode(start, 0x6000);
            if (code.Length == 0) { sb.AppendLine("  (code unreadable)"); return; }

            var reader = new ByteArrayCodeReader(code);
            Iced.Intel.Decoder decoder = Iced.Intel.Decoder.Create(64, reader);
            decoder.IP = start;
            var seen = new HashSet<string>();
            int unresolved = 0, count = 0;
            while (reader.CanReadByte && count++ < 6000)
            {
                decoder.Decode(out Instruction ins);
                if (ins.IsInvalid) break;
                int next = (int)(ins.NextIP - start);
                bool padNext = next < code.Length && code[next] == 0xCC;

                string line = null;
                if (ins.FlowControl == FlowControl.Call || ins.FlowControl == FlowControl.UnconditionalBranch)
                {
                    if (ins.Op0Kind == OpKind.NearBranch64)
                    {
                        ulong target = ins.NearBranchTarget;
                        bool inside = target >= start && target < start + (ulong)code.Length && ins.FlowControl != FlowControl.Call;
                        if (!inside)
                        {
                            MethodBase r = null;
                            try { r = new XrefInstance(XrefType.Method, (IntPtr)(long)target, (IntPtr)(long)ins.IP).TryResolve(); } catch { }
                            if (r != null) line = "call " + Sig(r);
                            else unresolved++;
                        }
                    }
                }
                else if (ins.IsIPRelativeMemoryOperand)
                {
                    string d = DescribeGlobal(ins.IPRelativeMemoryAddress);
                    if (d != null) line = d;
                }
                if (line != null && (seen.Add(line) || line.StartsWith("call"))) sb.AppendLine("  " + line);

                if (ins.Mnemonic == Mnemonic.Int3) break;
                if ((ins.FlowControl == FlowControl.Return || ins.FlowControl == FlowControl.UnconditionalBranch) && padNext) break;
            }
            sb.AppendLine($"  ({count} instructions, {unresolved} unresolved runtime/helper calls)");
        }

        // cmd=xref&type=T&method=Name
        internal static string Xref(NameValueCollection q)
        {
            Type t = Inspect.FindType(q["type"]);
            if (t == null) return "type not found: " + q["type"];
            string mname = q["method"];
            if (string.IsNullOrEmpty(mname)) return "method= is required";
            var sb = new StringBuilder();
            var methods = t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                .Concat(t.GetConstructors(All | BindingFlags.DeclaredOnly))
                .Where(m => mname == "*" ? (!m.Name.StartsWith("Rpc") && !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_") && !m.Name.Contains("NetworkInitialize") && !m.IsGenericMethodDefinition) : string.Equals(m.Name, mname, StringComparison.OrdinalIgnoreCase));
            foreach (MethodBase m in methods)
            {
                sb.AppendLine("== " + Sig(m));
                try { ScanMethod(m, sb, false); }
                catch (Exception e) { sb.AppendLine("  scan failed: " + e.Message); }
            }
            return sb.Length == 0 ? "no method '" + mname + "' on " + t.FullName : sb.ToString();
        }
    }
}
